//! 密钥派生：与 Java/.NET 完全一致的确定性 KDF。
//!
//! 全由配置密钥 `aesKey`（rawKey）派生，三端必须逐字节相等：
//! - AES 密钥：`SHA256("AES:" + rawKey)`（全 32 字节，CBC 与 GCM 共用）
//! - HMAC 密钥：`SHA256("HMAC:" + rawKey)`（全 32 字节，CBC 与 SM4 共用）
//! - SM4 密钥：`SHA256("SM4:" + rawKey)` 的前 16 字节

use sha2::{Digest, Sha256};

fn sha256(data: &[u8]) -> [u8; 32] {
    let mut h = Sha256::new();
    h.update(data);
    h.finalize().into()
}

/// AES-256 密钥：SHA256("AES:" + rawKey)。
pub fn derive_aes_key(raw_key: &str) -> [u8; 32] {
    sha256(format!("AES:{raw_key}").as_bytes())
}

/// HMAC-SHA256 密钥：SHA256("HMAC:" + rawKey)。
pub fn derive_hmac_key(raw_key: &str) -> [u8; 32] {
    sha256(format!("HMAC:{raw_key}").as_bytes())
}

/// SM4-128 密钥：SHA256("SM4:" + rawKey) 的前 16 字节。
pub fn derive_sm4_key(raw_key: &str) -> [u8; 16] {
    let digest = sha256(format!("SM4:{raw_key}").as_bytes());
    let mut key = [0u8; 16];
    key.copy_from_slice(&digest[..16]);
    key
}

#[cfg(test)]
mod tests {
    use super::*;

    /// 与 vectors.txt / vectors-sm4-gcm.txt 中 Java 导出的派生结果逐字节对齐。
    #[test]
    fn kdf_matches_java_vectors() {
        let raw = "SxRise2026EncryptSecret";
        assert_eq!(
            hex::encode(derive_aes_key(raw)),
            "6db1c6320e180188073db9447238b3dfbb0054ad94a6938ea0142c708af81110"
        );
        assert_eq!(
            hex::encode(derive_hmac_key(raw)),
            "c41c6af84ec16c7d0c347108483ab8473b3b1e89dc6e9507589575d7f4265ff0"
        );
        assert_eq!(
            hex::encode(derive_sm4_key(raw)),
            "67a519daca64522f5aa9d985a39d4a66"
        );
    }
}
