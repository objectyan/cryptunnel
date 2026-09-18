//! 隧道生命周期管理：把 `cryptunnel-tunnel` 库桥接到 Tauri。
//!
//! 隧道在独立 tokio 任务里跑（监听本地端口 → 加密 WS/HTTP → 服务端），
//! 运行期只发 [`TunnelEvent`]，这里把它转成 Tauri `emit` 推给前端实时刷新。
//! 架构红线：隧道库不碰 UI，UI 靠事件驱动。

use std::sync::Mutex;

use cryptunnel_tunnel::{
    Direction, EventSink, Tunnel, TunnelConfig, TunnelEvent, TunnelState,
};
use serde::Serialize;
use tauri::{AppHandle, Emitter};

/// 隧道当前句柄（停止开关）。
pub struct TunnelHandle {
    tunnel: std::sync::Arc<Tunnel>,
}

/// 全局隧道状态（同一时刻只跑一条隧道，spike 阶段）。
pub struct TunnelManager {
    current: Mutex<Option<TunnelHandle>>,
}

impl TunnelManager {
    pub fn new() -> Self {
        TunnelManager {
            current: Mutex::new(None),
        }
    }

    pub fn is_running(&self) -> bool {
        self.current.lock().unwrap().is_some()
    }

    pub fn stop(&self) {
        if let Some(h) = self.current.lock().unwrap().take() {
            h.tunnel.shutdown();
        }
    }
}

/// 前端「启动隧道」表单的入参。
#[derive(Debug, serde::Deserialize)]
pub struct StartParams {
    pub server_url: String,
    pub aes_key: String,
    pub auth_key: String,
    #[serde(default = "default_port")]
    pub local_port: u16,
    #[serde(default)]
    pub cipher: Option<String>,
    #[serde(default)]
    pub target_id: Option<String>,
}

fn default_port() -> u16 {
    3307
}

/// 推给前端的隧道日志/状态事件载荷。
#[derive(Debug, Clone, Serialize)]
struct TunnelEventPayload {
    kind: String,
    message: String,
}

/// 启动隧道。返回启动结果描述。
pub fn start(
    app: &AppHandle,
    mgr: &TunnelManager,
    params: StartParams,
) -> Result<String, String> {
    if mgr.is_running() {
        return Err("隧道已在运行，请先停止。".to_string());
    }

    let mut cfg = TunnelConfig {
        name: "default".to_string(),
        display_name: "默认隧道".to_string(),
        server_url: params.server_url.clone(),
        aes_key: params.aes_key,
        auth_key: params.auth_key,
        local_port: params.local_port,
        target_id: params.target_id.filter(|t| !t.trim().is_empty()),
        ..Default::default()
    };
    if let Some(c) = params.cipher.filter(|c| !c.trim().is_empty()) {
        cfg.cipher = c;
    }

    // 把隧道事件桥到前端（emit "tunnel-event"）。
    let app_handle = app.clone();
    let sink: EventSink = std::sync::Arc::new(move |e: TunnelEvent| {
        let payload = match &e {
            TunnelEvent::State(s) => TunnelEventPayload {
                kind: "state".into(),
                message: state_text(*s).to_string(),
            },
            TunnelEvent::Log(level, msg) => TunnelEventPayload {
                kind: format!("{:?}", level).to_lowercase(),
                message: msg.clone(),
            },
            TunnelEvent::Bytes(dir, n) => TunnelEventPayload {
                kind: "bytes".into(),
                message: format!("{} {} 字节", dir_text(*dir), n),
            },
            TunnelEvent::Fatal(m) => TunnelEventPayload {
                kind: "fatal".into(),
                message: m.clone(),
            },
        };
        let _ = app_handle.emit("tunnel-event", payload);
    });

    let (tunnel, shutdown_rx) = Tunnel::new(cfg.clone(), sink);
    let tunnel = std::sync::Arc::new(tunnel);

    *mgr.current.lock().unwrap() = Some(TunnelHandle {
        tunnel: std::sync::Arc::clone(&tunnel),
    });

    // 在独立 tokio 任务里跑监听循环。
    let app_handle2 = app.clone();
    tauri::async_runtime::spawn(async move {
        if let Err(e) = tunnel.run(shutdown_rx).await {
            let _ = app_handle2.emit(
                "tunnel-event",
                TunnelEventPayload {
                    kind: "fatal".into(),
                    message: format!("隧道运行失败：{e}"),
                },
            );
        }
    });

    Ok(format!(
        "隧道启动中：监听 127.0.0.1:{} → {}（算法 {}）",
        cfg.local_port, cfg.server_url, cfg.cipher
    ))
}

fn state_text(s: TunnelState) -> &'static str {
    match s {
        TunnelState::Stopped => "已停止",
        TunnelState::Listening => "监听中",
        TunnelState::WsConnecting => "WS 连接中",
        TunnelState::WsConnected => "WS 已连接",
        TunnelState::HttpConnecting => "HTTP 连接中",
        TunnelState::HttpConnected => "HTTP 已连接",
        TunnelState::Error => "错误",
    }
}

fn dir_text(d: Direction) -> &'static str {
    match d {
        Direction::In => "↓收",
        Direction::Out => "↑发",
    }
}
