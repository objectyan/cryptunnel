//! 认证报文明文的生成器（对齐 .NET `AuthMessageBuilder.cs`）。
//!
//! 必须与服务端 `CryptunnelWebSocketHandler.handleAuth` 的内联解析字节级对齐
//! （校验链：段数 >= 4 → authKey 相等 → 时间窗 → nonce 重放）。
//!
//! 格式（4 段 / 5 段）：
//! ```text
//! AUTH:{authKey}:{unixSeconds}:{nonce}
//! AUTH:{authKey}:{unixSeconds}:{nonce}:{targetId}
//! ```
//!
//! 职责边界：本模块只生成明文，不碰网络。加密由调用方交给 `TunnelCipher::seal`——
//! 服务端对明文 `AUTH:` 前缀直接拒绝。

use cryptunnel_crypto::TunnelCipher;
use rand::RngCore;

use crate::error::TunnelError;

/// nonce 的字节长度。服务端只做字符串相等比较，16 字节与老客户端一致。
const NONCE_BYTES: usize = 16;

/// 生成 nonce：16 字节随机数的**小写**十六进制（32 字符）。
///
/// 随机源必须是密码学安全的（`OsRng`）——nonce 是服务端防重放的唯一凭据。
/// 大小写必须小写：Java 侧用 `String.format("%02x")`，服务端 nonce 缓存做字符串相等比较。
pub fn generate_nonce() -> String {
    let mut nonce = [0u8; NONCE_BYTES];
    rand::rngs::OsRng.fill_bytes(&mut nonce);
    let mut s = String::with_capacity(NONCE_BYTES * 2);
    for b in nonce {
        s.push_str(&format!("{b:02x}"));
    }
    s
}

/// 生成认证报文明文。
///
/// `auth_key` 不得含冒号，否则服务端按 `:` 切分会多出段数。
/// `target_id` 为 `None`/空白时生成 4 段格式，服务端按缺省 target 路由。
///
/// 含冒号时硬失败而非静默容忍：含冒号的 authKey 会让服务端解析出错误的段数，
/// 表现为 `Auth format error`。在客户端提前拦住，用户能立刻知道该改哪一项。
pub fn build(auth_key: &str, target_id: Option<&str>) -> Result<String, TunnelError> {
    if auth_key.is_empty() {
        return Err(TunnelError::Config("认证密钥(authKey) 不能为空。".into()));
    }
    if auth_key.contains(':') {
        return Err(TunnelError::Config(
            "认证密钥(authKey) 不能包含冒号 ':' —— 认证报文以冒号分段，含冒号会导致服务端解析失败\
             （表现为 Auth format error）。请修改配置中的 authKey。"
                .into(),
        ));
    }
    let has_target = matches!(target_id, Some(t) if !t.trim().is_empty());
    if has_target && target_id.unwrap().contains(':') {
        return Err(TunnelError::Config(
            "目标标识(targetId) 不能包含冒号 ':' —— 同上，会导致服务端认证报文分段错误。".into(),
        ));
    }

    // 与 Java 侧 System.currentTimeMillis()/1000 对齐，服务端用 |now-ts| 与时间窗比较。
    let timestamp = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_secs())
        .unwrap_or(0);
    let nonce = generate_nonce();

    let mut out = String::with_capacity(96);
    out.push_str("AUTH:");
    out.push_str(auth_key);
    out.push(':');
    out.push_str(&timestamp.to_string());
    out.push(':');
    out.push_str(&nonce);
    if has_target {
        out.push(':');
        out.push_str(target_id.unwrap());
    }
    Ok(out)
}

/// 生成并加密认证报文，返回可直接写入线路的载荷。
///
/// 把「生成 + 加密」收在一处，让所有认证发起点（WS 认证、HTTP connect、HTTP disconnect）
/// 共用同一条路径——三处各自拼装的话，任何一处漏掉加密都会被服务端以
/// `Plain-text auth rejected` 拒绝。
///
/// 明文只在本函数栈内存活，绝不写日志（契约 §5：密钥/明文严禁落日志）。
pub fn build_sealed(
    cipher: &dyn TunnelCipher,
    auth_key: &str,
    aes_key: &str,
    target_id: Option<&str>,
) -> Result<String, TunnelError> {
    if aes_key.is_empty() {
        return Err(TunnelError::Config("加密密钥(aesKey) 不能为空。".into()));
    }
    let plaintext = build(auth_key, target_id)?;
    Ok(cipher.seal(plaintext.as_bytes(), aes_key))
}

/// 密钥的可安全打印形式：仅前 6 字符 + 省略号，与老 Java 客户端行为一致。
pub fn mask_key(key: Option<&str>) -> String {
    match key {
        None => "(未配置)".to_string(),
        Some(k) if k.is_empty() => "(未配置)".to_string(),
        Some(k) if k.chars().count() <= 6 => "***".to_string(),
        Some(k) => format!("{}...", k.chars().take(6).collect::<String>()),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use cryptunnel_crypto::AesCbcHmacSha256;

    #[test]
    fn nonce_is_32_lowercase_hex() {
        let n = generate_nonce();
        assert_eq!(n.len(), 32);
        assert!(n.chars().all(|c| c.is_ascii_hexdigit() && !c.is_ascii_uppercase()));
    }

    #[test]
    fn build_four_segments() {
        let m = build("myauth", None).unwrap();
        let parts: Vec<&str> = m.split(':').collect();
        assert_eq!(parts.len(), 4);
        assert_eq!(parts[0], "AUTH");
        assert_eq!(parts[1], "myauth");
        assert!(parts[2].parse::<u64>().is_ok());
        assert_eq!(parts[3].len(), 32);
    }

    #[test]
    fn build_five_segments_with_target() {
        let m = build("myauth", Some("db1")).unwrap();
        let parts: Vec<&str> = m.split(':').collect();
        assert_eq!(parts.len(), 5);
        assert_eq!(parts[4], "db1");
    }

    #[test]
    fn reject_colon_in_authkey() {
        assert!(build("a:b", None).is_err());
        assert!(build("ok", Some("t:1")).is_err());
        assert!(build("", None).is_err());
    }

    #[test]
    fn sealed_is_base64_not_plaintext() {
        let s = build_sealed(&AesCbcHmacSha256, "myauth", "mykey", None).unwrap();
        assert!(!s.starts_with("AUTH:"));
        assert!(base64_is_valid(&s));
    }

    fn base64_is_valid(s: &str) -> bool {
        s.chars()
            .all(|c| c.is_ascii_alphanumeric() || c == '+' || c == '/' || c == '=')
    }

    #[test]
    fn mask_key_forms() {
        assert_eq!(mask_key(None), "(未配置)");
        assert_eq!(mask_key(Some("")), "(未配置)");
        assert_eq!(mask_key(Some("abc")), "***");
        assert_eq!(mask_key(Some("abcdefgh")), "abcdef...");
    }
}
