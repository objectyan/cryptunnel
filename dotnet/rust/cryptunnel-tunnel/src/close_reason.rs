//! 服务端 WebSocket 关闭原因 → 中文提示的映射（对齐 .NET `CloseReasonMapper.cs` 与契约 §1.2）。
//!
//! 服务端认证失败时不返回任何结构化错误，只调用 `close(NOT_ACCEPTABLE, reason)`，
//! reason 是一句英文短语（`CryptunnelWebSocketHandler.java` 共 11 种）。这是客户端能
//! 拿到的全部诊断信息——若不映射，用户看到的只是一句 "Auth decrypt failed"，
//! 而这些短语恰好对应完全不同的排查方向。
//!
//! 字面量必须与服务端**逐字节一致**。这里是精确匹配，任何一字差异都会让该分支静默失效、
//! 落到未知分支（判为可重试），于是本该立即停下来改配置的错误会变成无休止的重连。
//!
//! 本模块不做任何 I/O，纯查表。

/// 隧道失败原因的结构化描述。
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct TunnelFailure {
    /// 面向用户的中文提示。
    pub message: String,
    /// 是否值得自动重连。`false` 表示配置或客户端缺陷——重连只会以同样原因再失败一次。
    pub retryable: bool,
}

/// 服务端认证解密失败。本项目最容易误诊的一条，单独提出。
///
/// 服务端在 `AesUtil.decrypt(payload, aesKey, hmacKey)` 抛异常时就走到这里（Handler:143-148），
/// 此时它还没有解析出任何字段，无从判断是密钥错还是算法错——两种配置错误产生完全相同的
/// close reason。这是 ADR-0003 方案 B「报文内零算法标识」的必然代价（换来抗 DPI）。
/// 提示文案必须同时点出两种可能——CBC 与 SM4 帧结构完全一致（同为 112 字节），
/// 算法配错时连报文长度都看不出异常。
const AUTH_DECRYPT_FAILED_MESSAGE: &str = "认证解密失败：加密算法(cipher)与服务端不一致，或 AES 密钥(aesKey)不正确。\
服务端无法区分这两种情况，请同时核对两项配置 —— \
注意 aes-256-cbc-hmac-sha256 与 sm4 的报文长度完全相同，算法配错时无法从现象上分辨。";

/// 把服务端 close reason 映射为中文提示。匹配忽略大小写且做 trim。
///
/// `reason` 可能为 `None`——网络层异常断开时服务端来不及发 close 帧。
pub fn map(reason: Option<&str>) -> TunnelFailure {
    let key = reason.map(str::trim).filter(|s| !s.is_empty());

    let key = match key {
        None => {
            // 没有 reason 说明不是服务端主动拒绝，而是链路断了（WAF 掐断、网络抖动、服务重启）。
            return TunnelFailure {
                message: "连接被关闭且未收到原因，通常是网络中断、代理/WAF 掐断或服务端重启。".into(),
                retryable: true,
            };
        }
        Some(k) => k,
    };

    // 用小写归一匹配，不假设服务端未来不会调整大小写。
    let lower = key.to_ascii_lowercase();
    let (message, retryable): (String, bool) = match lower.as_str() {
        // ==== 客户端缺陷：重连无意义，必须改代码 ====
        // 注意字面量含后半句 " - use encrypted auth"（服务端 Handler:73 就是这么写的）。
        "plain-text auth rejected - use encrypted auth" => (
            "认证报文未加密，被服务端拒绝。这是客户端缺陷，请反馈给维护者。".into(),
            false,
        ),
        "first message must be auth" => (
            "首条消息不是认证报文，被服务端拒绝。这是客户端缺陷，请反馈给维护者。".into(),
            false,
        ),

        // ==== 配置错误：重连无意义，必须改配置 ====
        "auth format error" => (
            "认证报文格式错误。最常见原因是认证密钥(authKey)中含有冒号 ':' —— \
             认证报文以冒号分段，含冒号会导致服务端切分出错误的段数。"
                .into(),
            false,
        ),
        "auth key invalid" => (
            "认证密钥(authKey)不正确，请核对配置与服务端 cryptunnel 配置是否一致。".into(),
            false,
        ),
        "auth decrypt failed" => (AUTH_DECRYPT_FAILED_MESSAGE.into(), false),

        // ==== 环境问题：修正后可重试 ====
        "auth timestamp expired" => (
            "认证时间戳超出服务端允许的时间窗，通常是本机时钟与服务器偏差过大。\
             请校准系统时间（建议开启自动同步）后重试。"
                .into(),
            true,
        ),
        "auth nonce replay detected" => (
            "认证随机数被判定为重放，通常是极短时间内重复重连所致。稍后会自动重试。".into(),
            true,
        ),

        // ==== 服务端侧故障：可重试 ====
        // 三条 MySQL 相关 reason 触发点各不相同，不能合并：
        //   lost   → Handler:153，本会话要发数据时发现 socket 已失效
        //   closed → Handler:263，reader 线程读到 EOF（MySQL 主动断开）
        //   failed → 认证通过后建连失败，压根没连上
        "mysql connection lost" => (
            "服务端与 MySQL 之间的连接已断开。可能是数据库重启、空闲超时或网络抖动。".into(),
            true,
        ),
        "mysql connection closed" => (
            "MySQL 主动关闭了连接。最常见的原因是连接空闲时间超过数据库的 wait_timeout\
             （MySQL 默认 8 小时），长时间不操作后再查询就会遇到。稍后会自动重连。"
                .into(),
            true,
        ),
        "mysql connection failed" => (
            "服务端无法连接到 MySQL。请确认数据库是否可用，以及服务端白名单中的目标地址是否正确。"
                .into(),
            true,
        ),

        // ==== 服务端容量限制：可重试 ====
        // 在 afterConnectionEstablished 阶段就被拒（Handler:55），与任何密钥/算法配置无关。
        "max connections exceeded" => (
            "服务端连接数已达上限，本次连接被拒绝。这与本地配置无关 —— \
             请关闭暂时不用的数据库会话，或联系服务端管理员调高 cryptunnel 的 maxConnections。"
                .into(),
            true,
        ),

        // ==== 未知 reason ====
        // 服务端未来新增的关闭原因会走到这里。原样带上英文短语——保留原文比替换成
        // "未知错误"有用得多。保守判为可重试。
        _ => (format!("连接被服务端关闭：{key}"), true),
    };

    TunnelFailure { message, retryable }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn assert_retryable(reason: Option<&str>, expect: bool) {
        assert_eq!(map(reason).retryable, expect, "reason={reason:?}");
    }

    #[test]
    fn empty_reason_is_retryable() {
        assert_retryable(None, true);
        assert_retryable(Some(""), true);
        assert_retryable(Some("   "), true);
    }

    #[test]
    fn client_defects_not_retryable() {
        assert_retryable(Some("Plain-text auth rejected - use encrypted auth"), false);
        assert_retryable(Some("First message must be auth"), false);
    }

    #[test]
    fn config_errors_not_retryable() {
        assert_retryable(Some("Auth format error"), false);
        assert_retryable(Some("Auth key invalid"), false);
        assert_retryable(Some("Auth decrypt failed"), false);
    }

    #[test]
    fn env_and_server_retryable() {
        assert_retryable(Some("Auth timestamp expired"), true);
        assert_retryable(Some("Auth nonce replay detected"), true);
        assert_retryable(Some("MySQL connection lost"), true);
        assert_retryable(Some("MySQL connection closed"), true);
        assert_retryable(Some("MySQL connection failed"), true);
        assert_retryable(Some("Max connections exceeded"), true);
    }

    #[test]
    fn case_insensitive_and_trimmed() {
        assert_retryable(Some("  auth key invalid  "), false);
        assert_retryable(Some("AUTH DECRYPT FAILED"), false);
    }

    #[test]
    fn unknown_reason_kept_and_retryable() {
        let f = map(Some("Some future reason"));
        assert!(f.retryable);
        assert!(f.message.contains("Some future reason"));
    }

    #[test]
    fn decrypt_failed_warns_both_cipher_and_key() {
        let f = map(Some("Auth decrypt failed"));
        assert!(f.message.contains("cipher") || f.message.contains("算法"));
        assert!(f.message.contains("aesKey") || f.message.contains("密钥"));
    }
}
