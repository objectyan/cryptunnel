//! 隧道层错误类型。

use std::fmt;

/// 帧载荷超过接收端单帧上限。**语义是客户端 bug，不是运行时故障**——
/// 因此不参与重连退避，捕获方应直接终止该隧道并把完整诊断写入日志。
#[derive(Debug, Clone)]
pub struct TunnelFrameTooLarge {
    pub payload_chars: usize,
    pub plaintext_length: usize,
    /// 算法标识。本地配置标识，非机密（ADR-0003：标识不上线，但可用于本地诊断）。
    pub cipher_id: String,
}

impl fmt::Display for TunnelFrameTooLarge {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(
            f,
            "帧载荷 {} 字符超过上限 {}（明文 {} 字节，算法 {}）。\
             这是客户端分片逻辑或 chunkSize 配置的缺陷，继续发送必然触发对端 1009 断连。",
            self.payload_chars,
            crate::framing::MAX_FRAME_PAYLOAD_CHARS,
            self.plaintext_length,
            self.cipher_id
        )
    }
}
impl std::error::Error for TunnelFrameTooLarge {}

/// 隧道层统一错误。
#[derive(Debug)]
pub enum TunnelError {
    /// 监听地址/端口校验失败（含非回环未授权、端口占用）。
    Listen(String),
    /// 帧超限（客户端 bug）。
    FrameTooLarge(TunnelFrameTooLarge),
    /// 配置非法（authKey/targetId 含冒号、密钥为空等）。
    Config(String),
    /// 网络层错误。
    Io(std::io::Error),
    /// WebSocket 协议错误。
    Ws(String),
    /// HTTP 降级通道错误。
    Http(String),
    /// 加密/解密失败（跨算法误配或密钥错误，须硬失败断开）。
    Crypto(String),
}

impl fmt::Display for TunnelError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            TunnelError::Listen(m) => write!(f, "监听失败：{m}"),
            TunnelError::FrameTooLarge(e) => write!(f, "{e}"),
            TunnelError::Config(m) => write!(f, "配置错误：{m}"),
            TunnelError::Io(e) => write!(f, "网络错误：{e}"),
            TunnelError::Ws(m) => write!(f, "WebSocket 错误：{m}"),
            TunnelError::Http(m) => write!(f, "HTTP 通道错误：{m}"),
            TunnelError::Crypto(m) => write!(f, "加解密失败：{m}"),
        }
    }
}
impl std::error::Error for TunnelError {}

impl From<std::io::Error> for TunnelError {
    fn from(e: std::io::Error) -> Self {
        TunnelError::Io(e)
    }
}
impl From<TunnelFrameTooLarge> for TunnelError {
    fn from(e: TunnelFrameTooLarge) -> Self {
        TunnelError::FrameTooLarge(e)
    }
}
