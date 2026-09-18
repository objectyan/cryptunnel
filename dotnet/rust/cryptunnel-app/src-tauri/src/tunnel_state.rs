//! 隧道生命周期管理：把 `cryptunnel-tunnel` 库桥接到 Tauri（多隧道版）。
//!
//! 每条隧道（对应一个配置项目）在独立 tokio 任务里跑，运行期只发 [`TunnelEvent`]，
//! 这里把它转成 Tauri `emit` 推给前端实时刷新。架构红线：隧道库不碰 UI，UI 靠事件驱动。

use std::collections::HashMap;
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex};

use cryptunnel_tunnel::{
    Direction, EventSink, Tunnel, TunnelConfig, TunnelEvent, TunnelState,
};
use serde::Serialize;
use tauri::{AppHandle, Emitter, Manager};

/// 一条运行中的隧道句柄。
pub struct TunnelHandle {
    tunnel: std::sync::Arc<Tunnel>,
}

/// 全局隧道状态：按项目 `name` 管理多条隧道。
pub struct TunnelManager {
    tunnels: Mutex<HashMap<String, TunnelHandle>>,
    /// 最近一次加载出的配置（供状态查询回填展示字段）。
    configs: Mutex<HashMap<String, TunnelConfig>>,
}

impl TunnelManager {
    pub fn new() -> Self {
        TunnelManager {
            tunnels: Mutex::new(HashMap::new()),
            configs: Mutex::new(HashMap::new()),
        }
    }

    pub fn is_running(&self, name: &str) -> bool {
        self.tunnels.lock().unwrap().contains_key(name)
    }

    pub fn stop(&self, name: &str) -> bool {
        if let Some(h) = self.tunnels.lock().unwrap().remove(name) {
            h.tunnel.shutdown();
            true
        } else {
            false
        }
    }

    pub fn stop_all(&self) {
        let names: Vec<String> = self.tunnels.lock().unwrap().keys().cloned().collect();
        for n in names {
            self.stop(&n);
        }
    }

    pub fn set_configs(&self, configs: Vec<TunnelConfig>) {
        let mut map = self.configs.lock().unwrap();
        map.clear();
        for c in configs {
            map.insert(c.name.clone(), c);
        }
    }

    pub fn get_config(&self, name: &str) -> Option<TunnelConfig> {
        self.configs.lock().unwrap().get(name).cloned()
    }
}

/// 前端「启动隧道」表单的入参（spike 手动模式，兼容保留）。
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

/// 推给前端的隧道事件载荷（带项目名，便于多隧道区分）。
#[derive(Debug, Clone, Serialize)]
pub struct TunnelEventPayload {
    pub tunnel: String,
    pub kind: String,
    pub message: String,
}

/// 用给定配置启动一条隧道（按 `cfg.name` 登记）。
pub fn start_with_config(
    app: &AppHandle,
    mgr: &TunnelManager,
    cfg: TunnelConfig,
) -> Result<String, String> {
    if !cfg.enabled {
        return Err(format!("项目「{}」已停用（enabled: false）。", cfg.name));
    }
    if mgr.is_running(&cfg.name) {
        return Err(format!("隧道「{}」已在运行。", cfg.name));
    }

    let name = cfg.name.clone();
    let display = cfg.display_name.clone();
    let port = cfg.local_port;
    let url = cfg.server_url.clone();
    let cipher = cfg.cipher.clone();

    // 把隧道事件桥到前端（emit "tunnel-event"），载荷带项目名。
    let app_handle = app.clone();
    let name_for_sink = name.clone();
    let sink: EventSink = std::sync::Arc::new(move |e: TunnelEvent| {
        let (kind, message) = match &e {
            TunnelEvent::State(s) => ("state".to_string(), state_text(*s).to_string()),
            TunnelEvent::Log(level, msg) => (format!("{:?}", level).to_lowercase(), msg.clone()),
            TunnelEvent::Bytes(dir, n) => {
                ("bytes".to_string(), format!("{} {} 字节", dir_text(*dir), n))
            }
            TunnelEvent::Fatal(m) => ("fatal".to_string(), m.clone()),
        };
        let _ = app_handle.emit(
            "tunnel-event",
            TunnelEventPayload {
                tunnel: name_for_sink.clone(),
                kind,
                message,
            },
        );
    });

    let (tunnel, shutdown_rx) = Tunnel::new(cfg, sink);
    let tunnel = std::sync::Arc::new(tunnel);

    mgr.tunnels
        .lock()
        .unwrap()
        .insert(name.clone(), TunnelHandle { tunnel: std::sync::Arc::clone(&tunnel) });

    // 在独立 tokio 任务里跑监听循环；结束后从管理器摘除自己。
    let app_handle2 = app.clone();
    let name2 = name.clone();
    let mgr_ptr = mgr as *const TunnelManager as usize; // 见下方 SAFETY 注释
    tauri::async_runtime::spawn(async move {
        if let Err(e) = tunnel.run(shutdown_rx).await {
            let _ = app_handle2.emit(
                "tunnel-event",
                TunnelEventPayload {
                    tunnel: name2.clone(),
                    kind: "fatal".into(),
                    message: format!("隧道运行失败：{e}"),
                },
            );
        }
        // SAFETY: TunnelManager 由 Tauri `.manage()` 持有，生命周期贯穿整个应用；
        // 此处仅在隧道任务收尾时摘除自身登记。
        let mgr = unsafe { &*(mgr_ptr as *const TunnelManager) };
        mgr.tunnels.lock().unwrap().remove(&name2);
        let _ = app_handle2.emit(
            "tunnel-event",
            TunnelEventPayload {
                tunnel: name2,
                kind: "state".into(),
                message: "已停止".into(),
            },
        );
    });

    Ok(format!(
        "隧道「{display}」启动中：监听 127.0.0.1:{port} → {url}（算法 {cipher}）"
    ))
}

/// spike 手动模式：由表单参数构造一个临时配置并启动。
pub fn start(
    app: &AppHandle,
    mgr: &Arc<TunnelManager>,
    params: StartParams,
) -> Result<String, String> {
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
    start_with_config(app, mgr, cfg)
}

/// 配置目录解析（对齐 .NET `PathResolver`）。
///
/// - 便携模式：exe 旁存在 `portable.txt` → 配置在 exe 旁 `config.d/`。
/// - 安装模式：平台用户配置目录下 `Cryptunnel/config.d/`。
pub fn resolve_config_dir(app: &AppHandle) -> PathBuf {
    let exe_dir = std::env::current_exe()
        .ok()
        .and_then(|p| p.parent().map(|d| d.to_path_buf()))
        .unwrap_or_else(|| PathBuf::from("."));
    if exe_dir.join("portable.txt").exists() {
        return exe_dir.join("config.d");
    }
    app.path()
        .app_config_dir()
        .map(|p| p.join("config.d"))
        .unwrap_or_else(|_| exe_dir.join("config.d"))
}

/// 项目配置文件路径：`<configDir>/<name>.yaml`。
pub fn project_file_path(config_dir: &Path, name: &str) -> PathBuf {
    config_dir.join(format!("{name}.yaml"))
}

/// 保存项目配置（原子写：先写临时文件再改名）。
pub fn save_project_file(config_dir: &Path, pf: &cryptunnel_tunnel::ProjectFile) -> Result<PathBuf, String> {
    let name = pf.name.clone().ok_or_else(|| "缺少 name 字段".to_string())?;
    std::fs::create_dir_all(config_dir).map_err(|e| format!("创建配置目录失败：{e}"))?;
    let path = project_file_path(config_dir, &name);
    let tmp = config_dir.join(format!(".{name}.yaml.tmp"));
    let yaml = cryptunnel_tunnel::build_project_yaml(pf);
    std::fs::write(&tmp, yaml).map_err(|e| format!("写入失败：{e}"))?;
    std::fs::rename(&tmp, &path).map_err(|e| format!("落盘失败：{e}"))?;
    Ok(path)
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
