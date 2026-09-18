//! Rust 端加密 parity 校验：读取 dotnet/build/parity/*.txt 基准向量，
//! 验证 Rust 实现与 Java/.NET 字节级兼容。
//!
//! 校验项：
//!   1. 密钥派生确定性（AES/HMAC/SM4 三把，逐字节相等）
//!   2. GB/T 32907-2016 附录 A.1 官方 SM4 单分组向量（不依赖任何实现，最强校验）
//!   3. 解开 Java 侧导出的三种载荷（AES-CBC / SM4-CBC / AES-GCM）
//!   4. 本地 round-trip（三算法 seal→open 还原明文）
//!   5. 篡改检测（改一个字节必须解密失败）
//!
//! 用法：cargo run --bin parity [parity目录]   （默认自动向上找 dotnet/build/parity）

use cryptunnel_crypto::cipher::TunnelCipher;
use cryptunnel_crypto::{kdf, AesCbcHmacSha256, AesGcm, Sm4CbcHmacSha256};
use std::collections::HashMap;
use std::path::PathBuf;
use std::process::exit;

struct Vectors(HashMap<String, String>);

impl Vectors {
    fn get(&self, key: &str) -> &str {
        self.0
            .get(key)
            .unwrap_or_else(|| panic!("基准向量缺少键 {key}"))
    }
}

fn locate_parity_dir() -> PathBuf {
    // 允许命令行传入；否则从当前目录向上找 dotnet/build/parity/vectors.txt
    if let Some(arg) = std::env::args().nth(1).filter(|a| !a.starts_with("--")) {
        return PathBuf::from(arg);
    }
    let mut dir = std::env::current_dir().expect("无法获取当前目录");
    loop {
        let cand = dir.join("dotnet").join("build").join("parity");
        if cand.join("vectors.txt").is_file() {
            return cand;
        }
        if dir.join("vectors.txt").is_file() {
            return dir;
        }
        if !dir.pop() {
            panic!("向上遍历未找到 dotnet/build/parity 目录，请把 parity 目录作为参数传入");
        }
    }
}

fn load_vectors(dir: &std::path::Path) -> Vectors {
    let mut map = HashMap::new();
    for file in ["vectors.txt", "vectors-sm4-gcm.txt"] {
        let path = dir.join(file);
        let content = std::fs::read_to_string(&path)
            .unwrap_or_else(|e| panic!("读取 {} 失败：{e}", path.display()));
        for raw in content.lines() {
            let line = raw.trim();
            if line.is_empty() || line.starts_with('#') {
                continue;
            }
            if let Some(idx) = line.find('=') {
                if idx > 0 {
                    map.insert(line[..idx].trim().to_string(), line[idx + 1..].trim().to_string());
                }
            }
        }
    }
    Vectors(map)
}

fn decode_payload(b64: &str) -> Vec<u8> {
    use base64::{engine::general_purpose::STANDARD as B64, Engine as _};
    B64.decode(b64).expect("Java 载荷 Base64 解码失败")
}

// 用一个可翻转字节的辅助：篡改 Base64 解码后的某一字节再重新编码
fn tamper_payload(b64: &str) -> String {
    use base64::{engine::general_purpose::STANDARD as B64, Engine as _};
    let mut raw = B64.decode(b64).expect("Base64 解码失败");
    let mid = raw.len() / 2;
    raw[mid] ^= 0x01;
    B64.encode(raw)
}

fn main() {
    // --export 模式：导出 Rust 现场加密的三种载荷，供 C#/Java 反方向校验（Rust 加密 -> 它端解密）。
    if std::env::args().any(|a| a == "--export") {
        export_payloads();
        return;
    }

    let mut pass = 0usize;
    let mut fail = 0usize;
    macro_rules! check {
        ($name:expr, $cond:expr) => {
            if $cond {
                pass += 1;
                println!("  [PASS] {}", $name);
            } else {
                fail += 1;
                println!("  [FAIL] {}", $name);
            }
        };
    }

    let dir = locate_parity_dir();
    println!(">>> parity 向量目录：{}", dir.display());
    let v = load_vectors(&dir);

    let raw_key = v.get("RAW_KEY");
    let plain = v.get("PLAIN").as_bytes().to_vec();

    // ---- 1. 密钥派生确定性 ----
    println!("\n[1] 密钥派生（逐字节对齐 Java）");
    check!("AES_KEY", hex::encode(kdf::derive_aes_key(raw_key)) == v.get("AES_KEY"));
    check!("HMAC_KEY", hex::encode(kdf::derive_hmac_key(raw_key)) == v.get("HMAC_KEY"));
    check!("SM4_KEY", hex::encode(kdf::derive_sm4_key(raw_key)) == v.get("SM4_KEY"));

    // ---- 2. GB/T 32907-2016 官方 SM4 向量 ----
    println!("\n[2] GB/T 32907-2016 附录 A.1 官方 SM4 向量");
    {
        use cryptunnel_crypto::sm4::{Sm4Engine, BLOCK_SIZE};
        let key = hex::decode(v.get("GBT_A1_KEY")).unwrap();
        let pt = hex::decode(v.get("GBT_A1_PLAIN")).unwrap();
        let expect = hex::decode(v.get("GBT_A1_BLOCK")).unwrap();
        let engine = Sm4Engine::new(&key);
        let mut out = [0u8; BLOCK_SIZE];
        engine.encrypt_block(&pt, &mut out);
        check!("GBT_A1_BLOCK 加密", out[..] == expect[..]);
        let mut back = [0u8; BLOCK_SIZE];
        engine.decrypt_block(&out, &mut back);
        check!("GBT_A1 解密往返", back[..] == pt[..]);
    }

    // ---- 3. 解开 Java 侧导出的载荷（Java 加密 -> Rust 解密）----
    println!("\n[3] 解开 Java 侧导出的载荷（跨实现互解）");
    let aes = AesCbcHmacSha256;
    let sm4 = Sm4CbcHmacSha256;
    let gcm = AesGcm;

    let aes_opened = aes.open(v.get("JAVA_CIPHER"), raw_key);
    check!("AES-CBC 解 Java 载荷", aes_opened.ok().as_deref() == Some(&plain[..]));

    let sm4_opened = sm4.open(v.get("JAVA_SM4_PAYLOAD"), raw_key);
    check!("SM4-CBC 解 Java 载荷", sm4_opened.ok().as_deref() == Some(&plain[..]));

    let gcm_opened = gcm.open(v.get("JAVA_GCM_PAYLOAD"), raw_key);
    check!("AES-GCM 解 Java 载荷", gcm_opened.ok().as_deref() == Some(&plain[..]));

    // ---- 4. 本地 round-trip（Rust seal -> Rust open）----
    println!("\n[4] 本地 round-trip（三算法）");
    check!("AES-CBC round-trip", aes.open(&aes.seal(&plain, raw_key), raw_key).ok().as_deref() == Some(&plain[..]));
    check!("SM4-CBC round-trip", sm4.open(&sm4.seal(&plain, raw_key), raw_key).ok().as_deref() == Some(&plain[..]));
    check!("AES-GCM round-trip", gcm.open(&gcm.seal(&plain, raw_key), raw_key).ok().as_deref() == Some(&plain[..]));

    // ---- 5. 篡改检测 ----
    println!("\n[5] 篡改检测（改一字节必须解密失败）");
    check!("AES-CBC 篡改被拒", aes.open(&tamper_payload(v.get("JAVA_CIPHER")), raw_key).is_err());
    check!("SM4-CBC 篡改被拒", sm4.open(&tamper_payload(v.get("JAVA_SM4_PAYLOAD")), raw_key).is_err());
    check!("AES-GCM 篡改被拒", gcm.open(&tamper_payload(v.get("JAVA_GCM_PAYLOAD")), raw_key).is_err());

    // ---- 6. 帧结构核对（CBC 帧 = IV16+HMAC32+密文；GCM 帧 = nonce12+密文+tag16）----
    println!("\n[6] 帧结构长度核对");
    let java_cbc = decode_payload(v.get("JAVA_CIPHER"));
    check!("AES-CBC 帧头 48B (IV16+HMAC32)", java_cbc.len() >= 48 && (java_cbc.len() - 48) % 16 == 0);
    let java_sm4 = decode_payload(v.get("JAVA_SM4_PAYLOAD"));
    check!("SM4-CBC 帧头 48B (IV16+HMAC32)", java_sm4.len() >= 48 && (java_sm4.len() - 48) % 16 == 0);
    let java_gcm = decode_payload(v.get("JAVA_GCM_PAYLOAD"));
    check!("AES-GCM 帧 = nonce12+密文+tag16", java_gcm.len() >= 28 && java_gcm.len() == GCM_FRAME_OVERHEAD + plain_len_padded_gcm(&plain));

    println!("\n=======================================");
    println!("parity 结果：{pass} 通过 / {fail} 失败");
    if fail > 0 {
        println!("❌ 存在不兼容项，Rust 端与 Java/.NET 未字节级对齐");
        exit(1);
    }
    println!("✅ 全部通过：Rust 端加密与 Java/.NET 字节级兼容");
}

// GCM 无填充：密文长度 == 明文长度，帧额外开销 = nonce12 + tag16
const GCM_FRAME_OVERHEAD: usize = 12 + 16;
fn plain_len_padded_gcm(plain: &[u8]) -> usize {
    plain.len()
}

/// 导出 Rust 现场加密的三种载荷（含随机 IV/nonce），格式与 Java/C# 的 export 一致，
/// 供反方向校验：C#/Java 读取本输出并解开，能解出同一明文即证明「Rust 加密 -> 它端解密」兼容。
fn export_payloads() {
    let dir = locate_parity_dir();
    let v = load_vectors(&dir);
    let raw_key = v.get("RAW_KEY");
    let plain = v.get("PLAIN").as_bytes().to_vec();

    println!("RAW_KEY={raw_key}");
    println!("PLAIN={}", v.get("PLAIN"));
    println!("RUST_AES_CBC={}", AesCbcHmacSha256.seal(&plain, raw_key));
    println!("RUST_SM4_CBC={}", Sm4CbcHmacSha256.seal(&plain, raw_key));
    println!("RUST_AES_GCM={}", AesGcm.seal(&plain, raw_key));
}
