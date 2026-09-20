use tauri::{
    menu::{Menu, MenuItem, PredefinedMenuItem},
    tray::{MouseButton, MouseButtonState, TrayIconBuilder, TrayIconEvent},
    Manager, WindowEvent,
};
use tauri_plugin_autostart::MacosLauncher;

mod tunnel_state;
mod file_log;

use serde::Serialize;
use tunnel_state::{StartParams, TunnelManager};

// 注意：`.manage()` 注册的类型必须与所有 `tauri::State<'_, T>` 取用点的类型
// **完全一致**。曾经注册 `Arc<TunnelManager>` 却按 `TunnelManager` 取用，
// 编译期不报错，运行到 `state()` 时才 panic（"state() called before manage()"）。
// TunnelManager 内部全部用 Mutex 管理可变状态，可直接按值注册共享。

/// 显示并聚焦主窗口
fn show_main_window(app: &tauri::AppHandle) {
    if let Some(win) = app.get_webview_window("main") {
        let _ = win.show();
        let _ = win.unminimize();
        let _ = win.set_focus();
    }
}

/// 构建系统托盘（图标 + 菜单 + 交互）
fn build_tray(app: &tauri::App) -> tauri::Result<()> {
    let show = MenuItem::with_id(app, "show", "显示主面板", true, None::<&str>)?;
    let sep = PredefinedMenuItem::separator(app)?;
    let quit = MenuItem::with_id(app, "quit", "退出", true, None::<&str>)?;
    let menu = Menu::with_items(app, &[&show, &sep, &quit])?;

    let icon = app
        .default_window_icon()
        .cloned()
        .expect("default_window_icon missing (bundle.icon 未配置)");

    let _tray = TrayIconBuilder::with_id("cryptunnel-tray")
        .icon(icon)
        .tooltip("Cryptunnel 隧道客户端")
        .menu(&menu)
        .show_menu_on_left_click(false)
        .on_menu_event(|app, event| match event.id.as_ref() {
            "show" => show_main_window(app),
            "quit" => {
                // 真正退出（绕过 close-requested 的隐藏拦截）
                app.exit(0);
            }
            _ => {}
        })
        .on_tray_icon_event(|tray, event| {
            // 左键点击托盘图标 → 显示主面板
            if let TrayIconEvent::Click {
                button: MouseButton::Left,
                button_state: MouseButtonState::Up,
                ..
            } = event
            {
                show_main_window(tray.app_handle());
            }
        })
        .build(app)?;

    Ok(())
}

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    tauri::Builder::default()
        // 单实例：已有实例时，激活已有窗口并退出新实例
        .plugin(tauri_plugin_single_instance::init(|app, _args, _cwd| {
            show_main_window(app);
        }))
        // 开机自启
        .plugin(tauri_plugin_autostart::init(
            MacosLauncher::LaunchAgent,
            Some(vec![]),
        ))
        // 系统通知
        .plugin(tauri_plugin_notification::init())
        // 自动更新（替代 Velopack，跨平台）+ 进程重启
        .plugin(tauri_plugin_updater::Builder::new().build())
        .plugin(tauri_plugin_process::init())
        // 系统文件管理器/浏览器打开（日志目录）
        .plugin(tauri_plugin_opener::init())
        // 隧道全局状态（按值注册；内部 Mutex 管可变状态，见上方类型一致性注释）
        .manage(TunnelManager::new())
        .setup(|app| {
            build_tray(app)?;
            // 装配会话日志落盘（对齐 .NET 老版 logs/proxy.log 滚动规则）。
            // 目录解析失败/无权限时 logger 仍然可用（写时吞错），绝不影响启动。
            let log_dir = file_log::resolve_log_dir(&app.handle());
            let logger = std::sync::Arc::new(file_log::FileLogger::new(
                log_dir,
                file_log::DEFAULT_MAX_FILE_SIZE_MB,
                file_log::DEFAULT_RETAIN_DAYS,
            ));
            app.state::<TunnelManager>().attach_logger(logger);
            Ok(())
        })
        // 关窗不退出，只隐藏到托盘（后台常驻）
        .on_window_event(|window, event| {
            if let WindowEvent::CloseRequested { api, .. } = event {
                if window.label() == "main" {
                    let _ = window.hide();
                    api.prevent_close();
                }
            }
        })
        .invoke_handler(tauri::generate_handler![
            greet,
            crypto_self_check,
            set_autostart,
            get_autostart,
            check_update,
            download_and_install_update,
            // 多隧道
            list_projects,
            start_project,
            stop_project,
            stop_all_projects,
            project_status,
            health_check,
            // 项目管理
            get_config_dir,
            get_log_dir,
            reveal_log_file,
            read_project,
            save_project,
            delete_project,
            // spike 手动模式（兼容保留）
            start_tunnel,
            stop_tunnel,
            tunnel_status,
        ])
        .run(tauri::generate_context!())
        .expect("error while running cryptunnel application");
}

#[tauri::command]
fn greet(name: &str) -> String {
    format!("你好，{}！Cryptunnel Rust 后端已接通。", name)
}

/// 加密层自检：调用已验证的 cryptunnel-crypto，证明依赖接通且行为正确
#[tauri::command]
fn crypto_self_check() -> String {
    use cryptunnel_crypto::kdf;
    let key = "cryptunnel-self-check";
    let aes = kdf::derive_aes_key(key);
    let hmac = kdf::derive_hmac_key(key);
    let sm4 = kdf::derive_sm4_key(key);
    format!(
        "KDF 派生成功：aes={} 字节, hmac={} 字节, sm4={} 字节",
        aes.len(),
        hmac.len(),
        sm4.len()
    )
}

#[tauri::command]
fn set_autostart(app: tauri::AppHandle, enabled: bool) -> Result<String, String> {
    use tauri_plugin_autostart::ManagerExt;
    let manager = app.autolaunch();
    if enabled {
        manager.enable().map_err(|e| e.to_string())?;
    } else {
        manager.disable().map_err(|e| e.to_string())?;
    }
    Ok(format!("开机自启已{}", if enabled { "开启" } else { "关闭" }))
}

#[tauri::command]
fn get_autostart(app: tauri::AppHandle) -> Result<bool, String> {
    use tauri_plugin_autostart::ManagerExt;
    app.autolaunch().is_enabled().map_err(|e| e.to_string())
}

// ============================================================================
// 自动更新（tauri-plugin-updater，GitHub Releases 作为更新源）
// ============================================================================

/// 更新检查结果（不含包体，仅元信息）。
#[derive(Debug, Clone, Serialize)]
struct UpdateInfo {
    available: bool,
    current: String,
    latest: String,
    notes: Option<String>,
}

/// 检查是否有新版本（不下载）。
#[tauri::command]
async fn check_update(app: tauri::AppHandle) -> Result<UpdateInfo, String> {
    use tauri_plugin_updater::UpdaterExt;
    let current = app.package_info().version.to_string();
    let updater = app.updater().map_err(|e| e.to_string())?;
    match updater.check().await {
        Ok(Some(update)) => Ok(UpdateInfo {
            available: true,
            current,
            latest: update.version.clone(),
            notes: update.body.clone(),
        }),
        Ok(None) => Ok(UpdateInfo {
            available: false,
            current: current.clone(),
            latest: current,
            notes: None,
        }),
        Err(e) => Err(format!("检查更新失败：{e}")),
    }
}

/// 下载并安装更新，完成后重启应用。
#[tauri::command]
async fn download_and_install_update(app: tauri::AppHandle) -> Result<String, String> {
    use tauri_plugin_updater::UpdaterExt;
    let updater = app.updater().map_err(|e| e.to_string())?;
    let Some(update) = updater.check().await.map_err(|e| e.to_string())? else {
        return Ok("已是最新版本。".into());
    };
    update
        .download_and_install(|_chunk, _total| {}, || {})
        .await
        .map_err(|e| format!("下载/安装失败：{e}"))?;
    // 重启到新版本（app.restart() 不返回）。
    app.restart();
}

// ============================================================================
// 多隧道：项目列表 / 启停 / 状态
// ============================================================================

/// 前端展示用的项目摘要（不含密钥）。
#[derive(Debug, Clone, Serialize)]
struct ProjectSummary {
    name: String,
    display_name: String,
    enabled: bool,
    server_url: String,
    local_port: u16,
    cipher: String,
    running: bool,
}

/// 配置错误条目（带文件名）。
#[derive(Debug, Clone, Serialize)]
struct ConfigErrorItem {
    file: String,
    message: String,
}

/// 项目列表 + 配置错误。
#[derive(Debug, Clone, Serialize)]
struct ProjectList {
    projects: Vec<ProjectSummary>,
    errors: Vec<ConfigErrorItem>,
    config_dir: String,
}

/// 加载配置目录，返回项目列表（含运行状态）与错误。
#[tauri::command]
fn list_projects(app: tauri::AppHandle, mgr: tauri::State<'_, TunnelManager>) -> ProjectList {
    let config_dir = tunnel_state::resolve_config_dir(&app);
    let result = cryptunnel_tunnel::load_config_dir(&config_dir);
    mgr.set_configs(result.configs.clone());
    let projects = result
        .configs
        .iter()
        .map(|c| ProjectSummary {
            name: c.name.clone(),
            display_name: c.display_name.clone(),
            enabled: c.enabled,
            server_url: c.server_url.clone(),
            local_port: c.local_port,
            cipher: c.cipher.clone(),
            running: mgr.is_running(&c.name),
        })
        .collect();
    ProjectList {
        projects,
        errors: result
            .errors
            .iter()
            .map(|e| ConfigErrorItem {
                file: e.file.clone(),
                message: e.message.clone(),
            })
            .collect(),
        config_dir: config_dir.display().to_string(),
    }
}

/// 按项目名启动隧道（配置来自最近一次 list_projects 加载）。
#[tauri::command]
fn start_project(
    app: tauri::AppHandle,
    mgr: tauri::State<'_, TunnelManager>,
    name: String,
) -> Result<String, String> {
    let cfg = mgr
        .get_config(&name)
        .ok_or_else(|| format!("找不到项目「{name}」，请先刷新列表。"))?;
    tunnel_state::start_with_config(&app, &mgr, cfg)
}

/// 按项目名停止隧道。
#[tauri::command]
fn stop_project(mgr: tauri::State<'_, TunnelManager>, name: String) -> Result<String, String> {
    if mgr.stop(&name) {
        Ok(format!("隧道「{name}」已发送停止指令。"))
    } else {
        Err(format!("隧道「{name}」未在运行。"))
    }
}

/// 停止所有运行中的隧道。
#[tauri::command]
fn stop_all_projects(mgr: tauri::State<'_, TunnelManager>) -> String {
    mgr.stop_all();
    "已停止所有运行中的隧道。".into()
}

/// 查询某项目运行状态。
#[tauri::command]
fn project_status(mgr: tauri::State<'_, TunnelManager>, name: String) -> String {
    if mgr.is_running(&name) {
        "running".into()
    } else {
        "stopped".into()
    }
}

/// 对某项目执行一次手动健康检查（真实链路探活）。
///
/// 探活走与真实隧道相同的路径：WS 连接 → 加密认证 → 等 MySQL 握手包。
/// 每次探活会让服务端真实建立并断开一条 MySQL 连接，这是诊断动作而非业务连接。
/// 报告同时写入滚动文件日志（.NET 老版 `Controller.LogHealth` 的对应行为）。
#[tauri::command]
async fn health_check(mgr: tauri::State<'_, TunnelManager>, name: String) -> Result<String, String> {
    let cfg = mgr
        .get_config(&name)
        .ok_or_else(|| format!("找不到项目「{name}」的配置，请先刷新列表。"))?;
    let report = cryptunnel_tunnel::health_probe(&cfg).await;
    let text = report.to_display_text();
    if let Some(logger) = mgr.logger() {
        let level = if report.healthy {
            cryptunnel_tunnel::LogLevel::Info
        } else {
            cryptunnel_tunnel::LogLevel::Warn
        };
        logger.log(level, Some(&name), &format!("健康检查：\n{text}"));
    }
    Ok(text)
}

// ============================================================================
// 项目管理：读 / 写 / 删
// ============================================================================

/// 返回配置目录路径（供设置窗展示）。
#[tauri::command]
fn get_config_dir(app: tauri::AppHandle) -> String {
    tunnel_state::resolve_config_dir(&app).display().to_string()
}

/// 返回日志目录路径（设置窗展示 + 「打开日志目录」用；与老版同路径）。
///
/// 优先返回正在使用的 logger 的实际目录（与 resolve 结果理论上相同，
/// 但以 logger 为准可以避免「显示的目录」和「真正写入的目录」漂移）。
#[tauri::command]
fn get_log_dir(app: tauri::AppHandle, mgr: tauri::State<'_, TunnelManager>) -> String {
    if let Some(logger) = mgr.logger() {
        return logger.dir().display().to_string();
    }
    file_log::resolve_log_dir(&app).display().to_string()
}

/// 在系统文件管理器中显示当前日志文件（proxy.log）。
#[tauri::command]
fn reveal_log_file(app: tauri::AppHandle, mgr: tauri::State<'_, TunnelManager>) -> Result<String, String> {
    use tauri_plugin_opener::OpenerExt;
    let logger = mgr.logger().ok_or("日志器未初始化。")?;
    let file = logger.dir().join("proxy.log");
    // 文件可能还没写过任何一行：先确保存在，否则系统管理器打开一个不存在的路径。
    if !file.exists() {
        logger.log(cryptunnel_tunnel::LogLevel::Info, None, "（打开日志目录）");
    }
    app.opener()
        .reveal_item_in_dir(&file)
        .map_err(|e| format!("打开失败：{e}"))?;
    Ok(file.display().to_string())
}

/// 读取单个项目原始文件（编辑器回填）。
#[tauri::command]
fn read_project(app: tauri::AppHandle, name: String) -> Result<serde_json::Value, String> {
    let dir = tunnel_state::resolve_config_dir(&app);
    let path = tunnel_state::project_file_path(&dir, &name);
    let pf = cryptunnel_tunnel::try_read_file(&path)?;
    serde_json::to_value(pf).map_err(|e| e.to_string())
}

/// 保存项目配置（新建或覆盖）。
#[tauri::command]
fn save_project(app: tauri::AppHandle, pf: serde_json::Value) -> Result<String, String> {
    let project: cryptunnel_tunnel::ProjectFile =
        serde_json::from_value(pf).map_err(|e| format!("参数解析失败：{e}"))?;
    let dir = tunnel_state::resolve_config_dir(&app);
    let path = tunnel_state::save_project_file(&dir, &project)?;
    Ok(format!("已保存：{}", path.display()))
}

/// 删除项目配置（先停隧道，再删文件）。
#[tauri::command]
fn delete_project(
    app: tauri::AppHandle,
    mgr: tauri::State<'_, TunnelManager>,
    name: String,
) -> Result<String, String> {
    mgr.stop(&name);
    let dir = tunnel_state::resolve_config_dir(&app);
    let path = tunnel_state::project_file_path(&dir, &name);
    if path.exists() {
        std::fs::remove_file(&path).map_err(|e| format!("删除失败：{e}"))?;
    }
    Ok(format!("已删除项目「{name}」。"))
}

// ============================================================================
// spike 手动模式（兼容保留，后续会被项目模式取代）
// ============================================================================

/// 启动隧道（本地端口 → 服务端加密隧道）。
#[tauri::command]
fn start_tunnel(
    app: tauri::AppHandle,
    mgr: tauri::State<'_, TunnelManager>,
    params: StartParams,
) -> Result<String, String> {
    tunnel_state::start(&app, mgr.inner(), params)
}

/// 停止隧道（spike 默认隧道）。
#[tauri::command]
fn stop_tunnel(mgr: tauri::State<'_, TunnelManager>) -> Result<String, String> {
    if !mgr.is_running("default") {
        return Err("隧道未在运行。".to_string());
    }
    mgr.stop("default");
    Ok("已发送停止指令。".to_string())
}

/// 查询隧道运行状态（spike 默认隧道）。
#[tauri::command]
fn tunnel_status(mgr: tauri::State<'_, TunnelManager>) -> Result<String, String> {
    Ok(if mgr.is_running("default") {
        "running".to_string()
    } else {
        "stopped".to_string()
    })
}
