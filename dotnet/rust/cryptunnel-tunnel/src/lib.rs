//! Cryptunnel 隧道核心（Rust 重写）。
//!
//! 本地 TCP 监听 → 加密 WebSocket/HTTP 隧道 → 服务端，与 .NET `Cryptunnel.Core.Tunnel`
//! 及 Java 服务端逐点对齐。加密由 [`cryptunnel_crypto`]（已字节级双向对齐）提供。
//!
//! 模块职责（单一不重叠）：
//! - [`framing`]：帧大小不变量守卫（不碰网络）。
//! - [`auth`]：`AUTH:` 明文生成 + 加密封装（不碰网络）。
//! - [`close_reason`]：九种 close reason → 中文提示 + 是否可重试（纯查表）。
//! - [`addr_policy`]：本地监听地址安全策略（纯判定）。
//! - [`registry`]：`TunnelCipher` 注册表。
//! - [`config`]：隧道运行配置。
//! - [`tunnel`]：网络核心（监听/选路/认证/双向泵），只发 [`tunnel::TunnelEvent`]，不碰 UI。

pub mod addr_policy;
pub mod auth;
pub mod close_reason;
pub mod config;
pub mod error;
pub mod framing;
pub mod registry;
pub mod tunnel;

pub use config::{HttpEndpoint, TransportMode, TunnelConfig, DEFAULT_CIPHER};
pub use error::{TunnelError, TunnelFrameTooLarge};
pub use tunnel::{Direction, EventSink, LogLevel, Tunnel, TunnelEvent, TunnelState};
