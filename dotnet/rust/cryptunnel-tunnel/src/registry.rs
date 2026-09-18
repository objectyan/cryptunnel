//! `TunnelCipher` 注册表（对齐 .NET `CipherRegistry` / Java `TunnelCiphers`）。
//!
//! 本注册表只做「标识 -> 实现」的本地映射，与线上协商无关——按 ADR-0003，报文内不含
//! 任何算法标识，两端靠配置约定保持一致。因此配置写错算法时无法自动纠正，只会表现为
//! 服务端 `Auth decrypt failed`，而该错误无法区分「算法配错」与「aesKey 配错」。
//!
//! 别名严格对齐 Java 侧：只有 SM4 声明别名（`sm4`），AES-CBC 与 AES-GCM 无别名。
//! 刻意不给 CBC/GCM 补 `aes-cbc`/`aes-gcm` 之类的顺手别名——客户端的可接受集合必须是
//! 服务端的子集，宁可让用户在本地就被拒，也不要接受一个服务端不认的写法。

use cryptunnel_crypto::{AesCbcHmacSha256, AesGcm, Sm4CbcHmacSha256, TunnelCipher};

use crate::config::DEFAULT_CIPHER;
use crate::error::TunnelError;

/// SM4 的别名（与 Java 侧 `Sm4Cipher.Alias` 一致）。
pub const SM4_ALIAS: &str = "sm4";

/// 全部正式标识（不含别名）。
pub const IDS: &[&str] = &[
    "aes-256-cbc-hmac-sha256",
    "aes-256-gcm",
    "sm4-cbc-hmac-sha256",
];

/// 已注册的全部标识与别名（用于错误提示与配置校验）。
pub fn all_ids() -> Vec<&'static str> {
    let mut v = IDS.to_vec();
    v.push(SM4_ALIAS);
    v
}

/// 是否注册了指定标识（或别名）。大小写不敏感。
pub fn contains(id: &str) -> bool {
    resolve_boxed(id).is_some()
}

/// 按标识获取实现。传入空/空白时返回缺省算法（便于配置项留空）。
///
/// 返回 `Box<dyn TunnelCipher>`：每个 cipher 都是无状态单元结构体，装箱零成本。
pub fn get(id: &str) -> Result<Box<dyn TunnelCipher>, TunnelError> {
    let id = id.trim();
    if id.is_empty() {
        return Ok(Box::new(AesCbcHmacSha256));
    }
    resolve_boxed(id).ok_or_else(|| {
        TunnelError::Config(format!(
            "未知的加密算法 '{id}'，可用：{}",
            all_ids().join(", ")
        ))
    })
}

/// 大小写不敏感地解析标识（或别名）为具体实现。
fn resolve_boxed(id: &str) -> Option<Box<dyn TunnelCipher>> {
    let lower = id.to_ascii_lowercase();
    match lower.as_str() {
        "aes-256-cbc-hmac-sha256" => Some(Box::new(AesCbcHmacSha256)),
        "aes-256-gcm" => Some(Box::new(AesGcm)),
        "sm4-cbc-hmac-sha256" | SM4_ALIAS => Some(Box::new(Sm4CbcHmacSha256)),
        _ => None,
    }
}

/// 把别名归一为正式标识，供配置校验与 YAML 回写使用。空值返回缺省算法标识。
pub fn normalize(id: &str) -> Result<String, TunnelError> {
    let id = id.trim();
    if id.is_empty() {
        return Ok(DEFAULT_CIPHER.to_string());
    }
    let c = get(id)?;
    Ok(c.id().to_string())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn default_when_empty() {
        assert_eq!(get("").unwrap().id(), "aes-256-cbc-hmac-sha256");
        assert_eq!(get("  ").unwrap().id(), "aes-256-cbc-hmac-sha256");
    }

    #[test]
    fn resolve_all_ids() {
        assert_eq!(get("aes-256-cbc-hmac-sha256").unwrap().id(), "aes-256-cbc-hmac-sha256");
        assert_eq!(get("aes-256-gcm").unwrap().id(), "aes-256-gcm");
        assert_eq!(get("sm4-cbc-hmac-sha256").unwrap().id(), "sm4-cbc-hmac-sha256");
    }

    #[test]
    fn sm4_alias_resolves() {
        assert_eq!(get("sm4").unwrap().id(), "sm4-cbc-hmac-sha256");
        assert_eq!(normalize("sm4").unwrap(), "sm4-cbc-hmac-sha256");
    }

    #[test]
    fn case_insensitive() {
        assert!(get("AES-256-GCM").is_ok());
        assert!(get("SM4").is_ok());
    }

    #[test]
    fn unknown_rejected() {
        assert!(get("aes-cbc").is_err()); // 刻意不接受顺手别名
        assert!(get("chacha20").is_err());
    }

    #[test]
    fn contains_works() {
        assert!(contains("aes-256-gcm"));
        assert!(contains("sm4"));
        assert!(!contains("aes-cbc"));
    }
}
