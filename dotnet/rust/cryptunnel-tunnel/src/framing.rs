//! 帧大小不变量的唯一守卫处（对齐 .NET `TunnelFraming.cs` 与契约 §2）。
//!
//! 加密后的载荷是 Base64 文本，长度约为明文的 1.36 倍再加算法头。接收端（Tomcat）
//! `maxTextMessageBufferSize` 默认 8192，超限直接 WS 1009 CLOSE_TOO_BIG——表现给用户
//! 是 DBeaver 报 `08S01 Communications link failure`，与真正的网络故障无法区分。
//!
//! 本模块不碰网络，只做纯计算与断言。

use crate::error::TunnelFrameTooLarge;

/// 单个文本帧载荷的字符数上限，取 Tomcat `maxTextMessageBufferSize` 的默认值。
///
/// 刻意与 [`config::TunnelConfig::chunk_size`](crate::config::TunnelConfig) 分离定义：
/// 写成 `chunk_size * 2` 之类的表达式会制造隐式耦合——调大分片时上限跟着变大，
/// 断言永远不会触发，保险丝等于被短路。这个值属于对端的能力，与本端读缓冲无关。
pub const MAX_FRAME_PAYLOAD_CHARS: usize = 8192;

/// 允许的最大明文分片长度，由 [`MAX_FRAME_PAYLOAD_CHARS`] 反推（留出余量取 6000）。
///
/// 推导（CBC 族膨胀率最高，以它为准）：明文 PKCS7 补齐到 16 字节边界，再加
/// IV(16)+HMAC(32)，最后 Base64 膨胀 4/3：`ceil((48 + pad16(n)) / 3) * 4 <= 8192`。
/// 关键点位：n=6095 → 恰好 8192 字符（合法）；n=6096 → 8216 字符（首个越界值，
/// 6096 是 16 的整数倍，PKCS7 会额外补满一整块）。安全上界 6095，取 6000 留余量。
pub const MAX_PLAINTEXT_CHUNK_BYTES: usize = 6000;

/// 发送前的强制断言。**必须在发送函数的唯一入口处调用**，不得散落到各调用点。
///
/// 超限属于客户端 bug（分片逻辑或配置校验失职），不是可重试的运行时错误，
/// 因此用 `Err` 而非静默截断表达。
pub fn ensure_within_frame_limit(
    payload: &str,
    plaintext_length: usize,
    cipher_id: &str,
) -> Result<(), TunnelFrameTooLarge> {
    if payload.len() <= MAX_FRAME_PAYLOAD_CHARS {
        return Ok(());
    }
    Err(TunnelFrameTooLarge {
        payload_chars: payload.len(),
        plaintext_length,
        cipher_id: cipher_id.to_string(),
    })
}

/// 校验分片大小是否落在安全区间。配置加载或隧道启动阶段调用，
/// 把潜在的帧超限从「传大结果集时随机断线」提前暴露成「启动即报错」。
///
/// 返回 `Err(中文原因)`；安全时返回 `Ok(())`。
pub fn validate_chunk_size(chunk_size: usize) -> Result<(), String> {
    if chunk_size == 0 {
        return Err("分片大小(chunkSize) 必须为正数。".to_string());
    }
    if chunk_size > MAX_PLAINTEXT_CHUNK_BYTES {
        return Err(format!(
            "分片大小(chunkSize) {chunk_size} 字节过大：加密并 Base64 后可能超过接收端单帧上限 \
             {MAX_FRAME_PAYLOAD_CHARS} 字符，会触发 WebSocket 1009 断线。请调整为不超过 \
             {MAX_PLAINTEXT_CHUNK_BYTES}（推荐保留默认值 4096）。"
        ));
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn within_limit_ok() {
        assert!(ensure_within_frame_limit(&"a".repeat(8192), 6000, "aes-256-cbc-hmac-sha256").is_ok());
    }

    #[test]
    fn over_limit_err() {
        let r = ensure_within_frame_limit(&"a".repeat(8193), 6096, "aes-256-cbc-hmac-sha256");
        assert!(r.is_err());
    }

    #[test]
    fn chunk_size_boundaries() {
        assert!(validate_chunk_size(4096).is_ok());
        assert!(validate_chunk_size(6000).is_ok());
        assert!(validate_chunk_size(6001).is_err());
        assert!(validate_chunk_size(0).is_err());
    }
}
