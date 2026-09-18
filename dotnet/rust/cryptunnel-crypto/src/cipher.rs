//! 三种隧道加密算法，帧结构与 Java/.NET 逐字节对齐。
//!
//! 帧布局（与 C# AesCrypto / AesGcmCipher / Sm4Cipher 一致）：
//! - CBC 类（AES & SM4 同构）：`Base64(IV[16] ‖ HMAC[32] ‖ 密文)`，HMAC 覆盖 `IV‖密文`，PKCS7 填充
//! - GCM：`Base64(nonce[12] ‖ 密文 ‖ tag[16])`，AEAD 自带 tag、无外挂 HMAC

use aes::cipher::{block_padding::Pkcs7, BlockDecryptMut, BlockEncryptMut, KeyIvInit};
use aes_gcm::{
    aead::{Aead, KeyInit},
    Aes256Gcm, Nonce,
};use base64::{engine::general_purpose::STANDARD as B64, Engine as _};
use hmac::{Hmac, Mac};
use rand::RngCore;
use sha2::Sha256;
use subtle::ConstantTimeEq;

use crate::kdf;
use crate::sm4::{Sm4Engine, BLOCK_SIZE};

type Aes256CbcEnc = cbc::Encryptor<aes::Aes256>;
type Aes256CbcDec = cbc::Decryptor<aes::Aes256>;
type HmacSha256 = Hmac<Sha256>;

const IV_LEN: usize = 16;
const HMAC_LEN: usize = 32;
const GCM_NONCE_LEN: usize = 12;
const GCM_TAG_LEN: usize = 16;

/// 统一错误类型（区分会泄露信息，对外只暴露"解密失败"语义；内部仍分阶段便于排查）。
#[derive(Debug)]
pub enum CryptoError {
    BadBase64,
    TooShort,
    HmacMismatch,
    BadPadding,
    Aead,
}

impl std::fmt::Display for CryptoError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        let s = match self {
            CryptoError::BadBase64 => "Base64 解码失败",
            CryptoError::TooShort => "载荷长度不足",
            CryptoError::HmacMismatch => "HMAC 校验失败，数据可能被篡改或密钥不一致",
            CryptoError::BadPadding => "PKCS7 填充非法，密钥不匹配或数据损坏",
            CryptoError::Aead => "AEAD 解密失败（密钥错误或数据被篡改）",
        };
        f.write_str(s)
    }
}
impl std::error::Error for CryptoError {}

/// 隧道加密算法统一接口（对应 .NET ITunnelCipher）。
///
/// `Send + Sync`：所有实现都是无状态单元结构体（密钥按需从 raw_key 派生、IV/nonce
/// 每次随机生成），可安全跨线程共享——隧道层在 `tokio::spawn` 的连接任务间以
/// `Arc<dyn TunnelCipher>` 传递它。
pub trait TunnelCipher: Send + Sync {
    fn id(&self) -> &'static str;
    /// 明文 -> Base64 帧。
    fn seal(&self, plaintext: &[u8], raw_key: &str) -> String;
    /// Base64 帧 -> 明文。
    fn open(&self, payload: &str, raw_key: &str) -> Result<Vec<u8>, CryptoError>;
}

fn hmac_sha256(key: &[u8; 32], data: &[u8]) -> [u8; 32] {
    let mut m = <HmacSha256 as Mac>::new_from_slice(key).expect("HMAC 任意长度密钥均可");
    m.update(data);
    m.finalize().into_bytes().into()
}

// ============================================================================
// AES-256-CBC + HMAC-SHA256（默认算法，委托给 AesUtil 等价物）
// ============================================================================

pub struct AesCbcHmacSha256;

impl TunnelCipher for AesCbcHmacSha256 {
    fn id(&self) -> &'static str {
        "aes-256-cbc-hmac-sha256"
    }

    fn seal(&self, plaintext: &[u8], raw_key: &str) -> String {
        let aes_key = kdf::derive_aes_key(raw_key);
        let hmac_key = kdf::derive_hmac_key(raw_key);

        let mut iv = [0u8; IV_LEN];
        rand::rngs::OsRng.fill_bytes(&mut iv);

        // AES/CBC/PKCS7 加密：缓冲需 resize 到「明文长度向上取整到分组倍数」，
        // encrypt_padded_mut 在这个长度内于 msg_len 之后写入填充并就地加密。
        let ciphertext = {
            let padded_len = plaintext.len() + (IV_LEN - plaintext.len() % IV_LEN);
            let mut buf = vec![0u8; padded_len];
            buf[..plaintext.len()].copy_from_slice(plaintext);
            Aes256CbcEnc::new(&aes_key.into(), &iv.into())
                .encrypt_padded_mut::<Pkcs7>(&mut buf, plaintext.len())
                .expect("缓冲已按分组倍数预留，加密不应失败")
                .to_vec()
        };

        // HMAC 覆盖 (IV ‖ 密文)
        let mut mac_input = Vec::with_capacity(IV_LEN + ciphertext.len());
        mac_input.extend_from_slice(&iv);
        mac_input.extend_from_slice(&ciphertext);
        let mac = hmac_sha256(&hmac_key, &mac_input);

        let mut out = Vec::with_capacity(IV_LEN + HMAC_LEN + ciphertext.len());
        out.extend_from_slice(&iv);
        out.extend_from_slice(&mac);
        out.extend_from_slice(&ciphertext);
        B64.encode(out)
    }

    fn open(&self, payload: &str, raw_key: &str) -> Result<Vec<u8>, CryptoError> {
        let raw = B64.decode(payload).map_err(|_| CryptoError::BadBase64)?;
        if raw.len() < IV_LEN + HMAC_LEN {
            return Err(CryptoError::TooShort);
        }
        let iv = &raw[..IV_LEN];
        let mac = &raw[IV_LEN..IV_LEN + HMAC_LEN];
        let ciphertext = &raw[IV_LEN + HMAC_LEN..];

        let aes_key = kdf::derive_aes_key(raw_key);
        let hmac_key = kdf::derive_hmac_key(raw_key);

        let mut mac_input = Vec::with_capacity(IV_LEN + ciphertext.len());
        mac_input.extend_from_slice(iv);
        mac_input.extend_from_slice(ciphertext);
        let computed = hmac_sha256(&hmac_key, &mac_input);
        if mac.ct_eq(&computed).unwrap_u8() != 1 {
            return Err(CryptoError::HmacMismatch);
        }

        // 解密：把密文拷进可写缓冲，decrypt_padded_mut 就地解密并剥离校验 PKCS7。
        let plain = {
            let mut buf = ciphertext.to_vec();
            Aes256CbcDec::new(&aes_key.into(), iv.into())
                .decrypt_padded_mut::<Pkcs7>(&mut buf)
                .map_err(|_| CryptoError::BadPadding)?
                .to_vec()
        };
        Ok(plain)
    }
}

// ============================================================================
// AES-256-GCM（AEAD，无外挂 HMAC）
// ============================================================================

pub struct AesGcm;

impl TunnelCipher for AesGcm {
    fn id(&self) -> &'static str {
        "aes-256-gcm"
    }

    fn seal(&self, plaintext: &[u8], raw_key: &str) -> String {
        let key = kdf::derive_aes_key(raw_key);
        let cipher = Aes256Gcm::new((&key).into());

        let mut nonce = [0u8; GCM_NONCE_LEN];
        rand::rngs::OsRng.fill_bytes(&mut nonce);

        // aes-gcm crate 的 encrypt 输出 = 密文 ‖ tag（与 Java Cipher 布局一致）
        let ct_and_tag = cipher
            .encrypt(Nonce::from_slice(&nonce), plaintext)
            .expect("GCM 加密不应失败");

        let mut out = Vec::with_capacity(GCM_NONCE_LEN + ct_and_tag.len());
        out.extend_from_slice(&nonce);
        out.extend_from_slice(&ct_and_tag);
        B64.encode(out)
    }

    fn open(&self, payload: &str, raw_key: &str) -> Result<Vec<u8>, CryptoError> {
        let raw = B64.decode(payload).map_err(|_| CryptoError::BadBase64)?;
        if raw.len() < GCM_NONCE_LEN + GCM_TAG_LEN {
            return Err(CryptoError::TooShort);
        }
        let nonce = &raw[..GCM_NONCE_LEN];
        let ct_and_tag = &raw[GCM_NONCE_LEN..];

        let key = kdf::derive_aes_key(raw_key);
        let cipher = Aes256Gcm::new((&key).into());
        // crate 的 decrypt 接受 密文‖tag 合并形态，tag 校验失败即 Err
        let plain = cipher
            .decrypt(Nonce::from_slice(nonce), ct_and_tag)
            .map_err(|_| CryptoError::Aead)?;
        Ok(plain)
    }
}

// ============================================================================
// SM4-CBC + HMAC-SHA256（国密，帧结构与 AES-CBC 完全一致）
// ============================================================================

pub struct Sm4CbcHmacSha256;

impl Sm4CbcHmacSha256 {
    /// SM4-CBC 加密 + PKCS7 填充（明文长度为分组整数倍时也补满一整块）。
    fn encrypt_cbc_pkcs7(plaintext: &[u8], key: &[u8; 16], iv: &[u8]) -> Vec<u8> {
        let pad = BLOCK_SIZE - plaintext.len() % BLOCK_SIZE;
        let total = plaintext.len() + pad;
        let mut buffer = vec![0u8; total];
        buffer[..plaintext.len()].copy_from_slice(plaintext);
        for b in &mut buffer[plaintext.len()..] {
            *b = pad as u8;
        }

        let engine = Sm4Engine::new(key);
        let mut output = vec![0u8; total];
        let mut previous = [0u8; BLOCK_SIZE];
        previous.copy_from_slice(iv);

        for offset in (0..total).step_by(BLOCK_SIZE) {
            let mut block = [0u8; BLOCK_SIZE];
            for i in 0..BLOCK_SIZE {
                block[i] = buffer[offset + i] ^ previous[i];
            }
            let mut target = [0u8; BLOCK_SIZE];
            engine.encrypt_block(&block, &mut target);
            output[offset..offset + BLOCK_SIZE].copy_from_slice(&target);
            previous = target;
        }
        output
    }

    /// SM4-CBC 解密 + 剥离并校验 PKCS7 填充。
    fn decrypt_cbc_pkcs7(ciphertext: &[u8], key: &[u8; 16], iv: &[u8]) -> Result<Vec<u8>, CryptoError> {
        if ciphertext.is_empty() || ciphertext.len() % BLOCK_SIZE != 0 {
            return Err(CryptoError::BadPadding);
        }
        let engine = Sm4Engine::new(key);
        let mut plain = vec![0u8; ciphertext.len()];
        let mut previous = [0u8; BLOCK_SIZE];
        previous.copy_from_slice(iv);

        for offset in (0..ciphertext.len()).step_by(BLOCK_SIZE) {
            let current = &ciphertext[offset..offset + BLOCK_SIZE];
            let mut decrypted = [0u8; BLOCK_SIZE];
            engine.decrypt_block(current, &mut decrypted);
            for i in 0..BLOCK_SIZE {
                plain[offset + i] = decrypted[i] ^ previous[i];
            }
            previous.copy_from_slice(current);
        }

        let pad = *plain.last().unwrap() as usize;
        if pad == 0 || pad > BLOCK_SIZE || pad > plain.len() {
            return Err(CryptoError::BadPadding);
        }
        if plain[plain.len() - pad..].iter().any(|&b| b as usize != pad) {
            return Err(CryptoError::BadPadding);
        }
        plain.truncate(plain.len() - pad);
        Ok(plain)
    }
}

impl TunnelCipher for Sm4CbcHmacSha256 {
    fn id(&self) -> &'static str {
        "sm4-cbc-hmac-sha256"
    }

    fn seal(&self, plaintext: &[u8], raw_key: &str) -> String {
        let sm4_key = kdf::derive_sm4_key(raw_key);
        let hmac_key = kdf::derive_hmac_key(raw_key);

        let mut iv = [0u8; IV_LEN];
        rand::rngs::OsRng.fill_bytes(&mut iv);

        let ciphertext = Self::encrypt_cbc_pkcs7(plaintext, &sm4_key, &iv);

        let mut mac_input = Vec::with_capacity(IV_LEN + ciphertext.len());
        mac_input.extend_from_slice(&iv);
        mac_input.extend_from_slice(&ciphertext);
        let mac = hmac_sha256(&hmac_key, &mac_input);

        let mut out = Vec::with_capacity(IV_LEN + HMAC_LEN + ciphertext.len());
        out.extend_from_slice(&iv);
        out.extend_from_slice(&mac);
        out.extend_from_slice(&ciphertext);
        B64.encode(out)
    }

    fn open(&self, payload: &str, raw_key: &str) -> Result<Vec<u8>, CryptoError> {
        let raw = B64.decode(payload).map_err(|_| CryptoError::BadBase64)?;
        if raw.len() < IV_LEN + HMAC_LEN {
            return Err(CryptoError::TooShort);
        }
        let iv = &raw[..IV_LEN];
        let mac = &raw[IV_LEN..IV_LEN + HMAC_LEN];
        let ciphertext = &raw[IV_LEN + HMAC_LEN..];

        let sm4_key = kdf::derive_sm4_key(raw_key);
        let hmac_key = kdf::derive_hmac_key(raw_key);

        let mut mac_input = Vec::with_capacity(IV_LEN + ciphertext.len());
        mac_input.extend_from_slice(iv);
        mac_input.extend_from_slice(ciphertext);
        let computed = hmac_sha256(&hmac_key, &mac_input);
        if mac.ct_eq(&computed).unwrap_u8() != 1 {
            return Err(CryptoError::HmacMismatch);
        }

        Self::decrypt_cbc_pkcs7(ciphertext, &sm4_key, iv)
    }
}
