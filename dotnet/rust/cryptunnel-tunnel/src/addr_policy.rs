//! 本地监听地址的安全策略（对齐 .NET `ListenAddressPolicy.cs`）。
//!
//! 本地端口是**完全无认证**的 MySQL 入口。非回环监听会让局域网内任意机器连上本地端口，
//! 并由本客户端用已配置的密钥替对方完成认证——对方**无需知道 aesKey 或 authKey**。
//! 这个后果必须被显式承认一次，而不是从「改个地址」的动作里被顺带打开。
//!
//! 本模块不碰网络，纯判定。

use std::net::IpAddr;

/// 判断是否为回环地址。
///
/// 不能只比对 `"127.0.0.1"` 字符串：整个 `127.0.0.0/8` 段都是回环，而 `0.0.0.0`/`::`/`*`/`+`
/// 这些通配写法都表示监听全部网卡，必须判为非回环。
///
/// 空值判为**非**回环：与其假定它安全，不如让它落进需要显式许可的那一侧。
pub fn is_loopback(address: Option<&str>) -> bool {
    let addr = match address.map(str::trim).filter(|s| !s.is_empty()) {
        Some(a) => a,
        None => return false,
    };
    // 通配写法一律视为非回环。
    if matches!(addr, "0.0.0.0" | "::" | "*" | "+" | "[::]") {
        return false;
    }
    let cleaned = addr.trim_start_matches('[').trim_end_matches(']');
    match cleaned.parse::<IpAddr>() {
        Ok(ip) => ip.is_loopback(),
        Err(_) => false,
    }
}

/// 校验监听地址与许可开关的组合是否可以启动。
/// 返回 `Ok(())` 表示通过，否则返回面向用户的错误说明。
///
/// 加载期拦而非启动期拦：加载期失败会让整个项目配置被拒绝并列出原因；
/// 拖到监听启动才拦，端口已进入半初始化状态。
pub fn validate(address: Option<&str>, allow_non_loopback: bool, port: u16) -> Result<(), String> {
    if is_loopback(address) || allow_non_loopback {
        return Ok(());
    }
    let addr = address.unwrap_or("(空)");
    Err(format!(
        "local.address=\"{addr}\" 不是回环地址，局域网内的其它机器可以连接本机 {port} 端口\
         并通过本隧道访问内网数据库，且对方无需知道 aesKey 或 authKey\
         （本客户端会用你配置的密钥替对方完成认证）。\
         若确需跨机共享，请显式加上 local.allowNonLoopback: true；\
         否则请把 local.address 改回 127.0.0.1。"
    ))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn loopback_detection() {
        assert!(is_loopback(Some("127.0.0.1")));
        assert!(is_loopback(Some("127.0.0.5"))); // 整个 127/8 都是回环
        assert!(is_loopback(Some("::1")));
        assert!(is_loopback(Some("[::1]")));
        assert!(is_loopback(Some(" 127.0.0.1 ")));
    }

    #[test]
    fn non_loopback_detection() {
        assert!(!is_loopback(Some("0.0.0.0")));
        assert!(!is_loopback(Some("::")));
        assert!(!is_loopback(Some("[::]")));
        assert!(!is_loopback(Some("*")));
        assert!(!is_loopback(Some("+")));
        assert!(!is_loopback(Some("192.168.1.10")));
        assert!(!is_loopback(None));
        assert!(!is_loopback(Some("")));
        assert!(!is_loopback(Some("not-an-ip")));
    }

    #[test]
    fn validate_gate() {
        assert!(validate(Some("127.0.0.1"), false, 3306).is_ok());
        assert!(validate(Some("0.0.0.0"), false, 3306).is_err());
        assert!(validate(Some("0.0.0.0"), true, 3306).is_ok()); // 显式许可
    }
}
