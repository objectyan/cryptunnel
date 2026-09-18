//! 隧道运行配置（对齐 .NET `TunnelConfig`）。
//!
//! 这是合并 defaults + 项目文件后、隧道实际使用的配置。字段默认值与 .NET 侧一致，
//! 保证老配置文件零变化、行为完全不变。

use std::time::Duration;

use cryptunnel_crypto::TunnelCipher;
use crate::error::TunnelError;

/// 传输模式。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum TransportMode {
    /// 先 WebSocket，失败且允许降级时回落 HTTP 长轮询。
    Auto,
    /// 仅 WebSocket。
    WebSocket,
    /// 仅 HTTP 长轮询。
    Http,
}

impl Default for TransportMode {
    fn default() -> Self {
        TransportMode::Auto
    }
}

/// 隧道加密算法标识（注册表项）。默认 `aes-256-cbc-hmac-sha256`，与老客户端及现有
/// 服务端部署字节级兼容——老配置文件不写这一项时行为完全不变。
///
/// **两端必须配成同一个值**。报文内不含算法标识（ADR-0003 方案 B，防 DPI），
/// 因此无法自动协商，配错的表现是服务端 `Auth decrypt failed`，
/// 而该错误无法与 aesKey 配错相区分。
pub const DEFAULT_CIPHER: &str = "aes-256-cbc-hmac-sha256";

/// 隧道运行配置。
#[derive(Debug, Clone)]
pub struct TunnelConfig {
    pub name: String,
    pub display_name: String,
    pub enabled: bool,
    pub server_url: String,
    pub aes_key: String,
    pub auth_key: String,
    pub listen_address: String,
    /// 是否显式允许非回环监听（启动闸门，默认 false）。
    pub allow_non_loopback: bool,
    pub local_port: u16,
    pub ws_path: String,
    /// HTTP 降级通道基础路径。三端点由它拼出：`{http_base_path}/connect|/tunnel|/disconnect`。
    /// 对接旧服务端只需配 `http_base_path: /jdbc-proxy`，不必回滚代码。
    pub http_base_path: String,
    pub mode: TransportMode,
    pub allow_fallback: bool,
    pub ws_connect: Duration,
    pub http_connect: Duration,
    pub http_read: Duration,
    pub auth_response: Duration,
    pub reconnect_delay: Duration,
    pub reconnect_enabled: bool,
    /// 最大重连次数。0 表示不限。
    pub reconnect_max_attempts: u32,
    pub chunk_size: usize,
    pub cipher: String,
    /// 可选具名 target（多数据源白名单）。`None` 走服务端缺省 target。
    pub target_id: Option<String>,
}

impl Default for TunnelConfig {
    fn default() -> Self {
        TunnelConfig {
            name: String::new(),
            display_name: String::new(),
            enabled: true,
            server_url: String::new(),
            aes_key: String::new(),
            auth_key: String::new(),
            listen_address: "127.0.0.1".to_string(),
            allow_non_loopback: false,
            local_port: 0,
            ws_path: "/ws-cryptunnel".to_string(),
            http_base_path: "/cryptunnel".to_string(),
            mode: TransportMode::Auto,
            allow_fallback: true,
            ws_connect: Duration::from_millis(10_000),
            http_connect: Duration::from_millis(10_000),
            http_read: Duration::from_millis(30_000),
            auth_response: Duration::from_millis(3_000),
            reconnect_delay: Duration::from_millis(3_000),
            reconnect_enabled: true,
            reconnect_max_attempts: 0,
            chunk_size: 4096,
            cipher: DEFAULT_CIPHER.to_string(),
            target_id: None,
        }
    }
}

impl TunnelConfig {
    /// 按 `cipher` 标识从注册表解析出加密实现。
    pub fn resolve_cipher(&self) -> Result<Box<dyn TunnelCipher>, TunnelError> {
        crate::registry::get(&self.cipher)
    }

    /// 由 `server_url` + `ws_path` 拼出 WebSocket URL（http→ws，https→wss）。
    pub fn websocket_url(&self) -> String {
        let base = self
            .server_url
            .replace("http://", "ws://")
            .replace("https://", "wss://");
        format!("{}{}", base.trim_end_matches('/'), self.ws_path)
    }

    /// HTTP 端点完整 URL。
    pub fn http_endpoint(&self, kind: HttpEndpoint) -> String {
        let base = self.server_url.trim_end_matches('/');
        let base_path = self.http_base_path.trim_end_matches('/');
        let suffix = match kind {
            HttpEndpoint::Connect => "/connect",
            HttpEndpoint::Tunnel => "/tunnel",
            HttpEndpoint::Disconnect => "/disconnect",
        };
        format!("{base}{base_path}{suffix}")
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum HttpEndpoint {
    Connect,
    Tunnel,
    Disconnect,
}

#[cfg(test)]
mod tests {
    use super::*;

    fn cfg(url: &str) -> TunnelConfig {
        TunnelConfig {
            server_url: url.to_string(),
            ..Default::default()
        }
    }

    #[test]
    fn websocket_url_conversion() {
        assert_eq!(cfg("http://h:8080").websocket_url(), "ws://h:8080/ws-cryptunnel");
        assert_eq!(cfg("https://h").websocket_url(), "wss://h/ws-cryptunnel");
        assert_eq!(cfg("http://h:8080/").websocket_url(), "ws://h:8080/ws-cryptunnel");
    }

    #[test]
    fn http_endpoints() {
        let c = cfg("http://h:8080");
        assert_eq!(c.http_endpoint(HttpEndpoint::Connect), "http://h:8080/cryptunnel/connect");
        assert_eq!(c.http_endpoint(HttpEndpoint::Tunnel), "http://h:8080/cryptunnel/tunnel");
        assert_eq!(c.http_endpoint(HttpEndpoint::Disconnect), "http://h:8080/cryptunnel/disconnect");
    }

    #[test]
    fn resolve_default_cipher() {
        let c = TunnelConfig::default();
        assert!(c.resolve_cipher().is_ok());
    }
}
