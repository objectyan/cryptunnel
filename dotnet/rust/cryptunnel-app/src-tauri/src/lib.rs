use tauri::{
    tray::{MouseButton, MouseButtonState, TrayIconBuilder, TrayIconEvent},
    Emitter, Manager, WindowEvent,
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

/// 主窗口是否可见（托盘菜单「显示主面板 / 隐藏主面板」的文案依据）。
fn main_window_visible(app: &tauri::AppHandle) -> bool {
    app.get_webview_window("main")
        .and_then(|w| w.is_visible().ok())
        .unwrap_or(false)
}

/// 切换主面板显示/隐藏（托盘菜单第一项）。
fn toggle_main_window(app: &tauri::AppHandle) {
    if let Some(win) = app.get_webview_window("main") {
        if win.is_visible().unwrap_or(false) {
            let _ = win.hide();
        } else {
            show_main_window(app);
        }
    }
}

/// 托盘菜单窗口的**逻辑**尺寸，须与 tauri.conf.json 中 tray-menu 窗口一致。
/// 仅用作拿不到窗口外框时的兜底（拿得到时必须用真实物理外框，见 show_tray_menu）。
const TRAY_MENU_LOGICAL_W: f64 = 176.0;
const TRAY_MENU_LOGICAL_H: f64 = 136.0;

/// 计算托盘菜单窗口左上角应放置的坐标（**物理像素**）。
///
/// - `cursor`：触发右键的鼠标物理坐标；
/// - `win`：菜单窗口的物理外框尺寸；
/// - `monitor`：光标所在显示器的 `(物理原点, 物理尺寸)`，取不到时传 `None`。
///
/// 默认把菜单摆在光标**左上方**（托盘图标在屏幕右下角，向上向左展开）；
/// 再夹到显示器范围内，避免贴边/多屏时弹到屏幕外。
///
/// 抽成纯函数的原因：常规情况下的错误（差几十像素、贴边越界）肉眼很难发现，
/// 而这里**必须**用同一量纲的坐标——光标是物理像素，配置里的窗口尺寸是逻辑像素，
/// 在 125%/150% 缩放的屏幕上混用会直接把菜单推出屏幕（见下方单测）。
fn tray_menu_origin(
    cursor: (f64, f64),
    win: (f64, f64),
    monitor: Option<((f64, f64), (f64, f64))>,
) -> (f64, f64) {
    let mut x = cursor.0 - win.0;
    let mut y = cursor.1 - win.1;
    match monitor {
        Some(((mx, my), (mw, mh))) => {
            // .max(mx)/.max(my) 保证显示器比菜单还小时下界不反超上界（否则 clamp panic）。
            let max_x = (mx + mw - win.0).max(mx);
            let max_y = (my + mh - win.1).max(my);
            x = x.clamp(mx, max_x);
            y = y.clamp(my, max_y);
        }
        None => {
            x = x.max(0.0);
            y = y.max(0.0);
        }
    }
    (x, y)
}

/// 在光标处弹出自绘托盘菜单窗口。
///
/// 不用系统原生菜单的原因：Windows 原生托盘菜单由系统绘制，样式无法与
/// 应用的深色自绘界面统一（跟随系统主题，浅色系统下是一块白底菜单）。
/// 自绘方案：无边框透明窗口 + 前端用同一套设计 token 渲染。
fn show_tray_menu(app: &tauri::AppHandle, cursor: tauri::PhysicalPosition<f64>) {
    let Some(win) = app.get_webview_window("tray-menu") else {
        return;
    };
    // ⚠ 必须用窗口的**物理**外框尺寸，不能直接用配置里的 176×136：
    // 配置值是逻辑像素，而光标坐标与 monitor_from_point 都是物理像素。
    // 125% 缩放下 176 逻辑 = 220 物理，混用会让菜单右/下边缘越过光标 44px，
    // 贴右下角时还会被 clamp 顶到屏幕外。
    let win_size = win
        .outer_size()
        .map(|s| (f64::from(s.width), f64::from(s.height)))
        .unwrap_or_else(|_| {
            let scale = win.scale_factor().unwrap_or(1.0);
            (
                TRAY_MENU_LOGICAL_W * scale,
                TRAY_MENU_LOGICAL_H * scale,
            )
        });
    let monitor = app
        .monitor_from_point(cursor.x, cursor.y)
        .ok()
        .flatten()
        .map(|m| {
            let p = m.position();
            let s = m.size();
            (
                (f64::from(p.x), f64::from(p.y)),
                (f64::from(s.width), f64::from(s.height)),
            )
        });
    let (x, y) = tray_menu_origin((cursor.x, cursor.y), win_size, monitor);
    let _ = win.set_position(tauri::PhysicalPosition::new(x, y));
    let _ = win.show();
    let _ = win.set_focus();
    // 通知菜单窗口刷新「显示/隐藏主面板」文案（首帧可能早于监听注册，前端另有 focus 兜底）。
    let _ = app.emit_to("tray-menu", "tray-menu-show", main_window_visible(app));
}

/// 构建系统托盘（图标 + 交互；菜单为自绘窗口，见 show_tray_menu）
fn build_tray(app: &tauri::App) -> tauri::Result<()> {
    // 托盘图标用 ICO 里的 128×128 PNG 帧，不用 256 的 default_window_icon()：
    // ① from_bytes 解整 ICO 只取第一帧（16×16，任务栏必模糊）；
    // ② 直接用 256 PNG 缩到 ~16-32px 同样发白失真。
    // 128 帧下采样到托盘尺寸干净锐利，且 RGBA 字节是 256 的 1/4。
    // icon.ico 帧布局（PNG 编码）：128×128 帧在字节区间 [1835..2920)。
    let ico: &[u8] = include_bytes!("../icons/icon.ico");
    let tray_png = ico
        .get(1835..2920)
        .expect("icon.ico 布局变化：128×128 帧区间越界，需重新解析帧偏移");
    // tray_png 已是纯 PNG 字节（非完整 ICO），from_bytes 正常解码。
    let icon = tauri::image::Image::from_bytes(tray_png)
        .expect("icon.ico 128×128 PNG 帧解码失败");

    let _tray = TrayIconBuilder::with_id("cryptunnel-tray")
        .icon(icon)
        .tooltip("Cryptunnel 隧道客户端")
        .show_menu_on_left_click(false)
        .on_tray_icon_event(|tray, event| {
            if let TrayIconEvent::Click {
                button,
                button_state: MouseButtonState::Up,
                position,
                ..
            } = event
            {
                match button {
                    // 左键点击托盘图标 → 显示主面板
                    MouseButton::Left => show_main_window(tray.app_handle()),
                    // 右键 → 在光标处弹出自绘菜单
                    MouseButton::Right => show_tray_menu(tray.app_handle(), position),
                    _ => {}
                }
            }
        })
        .build(app)?;

    Ok(())
}

/// 自绘托盘菜单当前应显示的主面板文案状态（true = 已显示 → 菜单项应为「隐藏主面板」）。
#[tauri::command]
fn tray_menu_state(app: tauri::AppHandle) -> bool {
    main_window_visible(&app)
}

/// 自绘托盘菜单的动作入口；执行前先收起菜单窗口。
#[tauri::command]
fn tray_menu_action(app: tauri::AppHandle, action: String) -> Result<(), String> {
    if let Some(win) = app.get_webview_window("tray-menu") {
        let _ = win.hide();
    }
    match action.as_str() {
        "toggle_main" => toggle_main_window(&app),
        "check_update" => {
            // 复用启动自查同一条更新流程：显示主面板 + 通知前端拉更新
            show_main_window(&app);
            let _ = app.emit("tray-check-update", ());
        }
        "quit" => app.exit(0),
        // 仅收起菜单（Esc）
        "hide" => {}
        other => return Err(format!("未知的托盘菜单动作：{other}")),
    }
    Ok(())
}

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    tauri::Builder::default()
        // 单实例：已有实例时，激活已有窗口并退出新实例
        .plugin(tauri_plugin_single_instance::init(|app, _args, _cwd| {
            show_main_window(app);
        }))
        // 开机自启：追加 --autostart 参数，供 setup 判断「本次是被开机自启拉起的」，
        // 从而只驻留托盘、不弹主窗口（对齐 .NET 老版静默启动行为）。
        .plugin(tauri_plugin_autostart::init(
            MacosLauncher::LaunchAgent,
            Some(vec!["--autostart"]),
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
            // 主窗口默认隐藏（tauri.conf.json visible=false），此处按启动来源决定是否显示：
            // 开机自启（带 --autostart）→ 仅驻留托盘静默启动；手动启动 → 显示主面板。
            // 这样自启时不会突然弹窗（老版 .NET 客户端同款行为）。
            let launched_by_autostart = std::env::args().any(|a| a == "--autostart");
            if !launched_by_autostart {
                show_main_window(&app.handle());
            }
            // 装配会话日志落盘（对齐 .NET 老版 logs/proxy.log 滚动规则）。
            // 目录解析失败/无权限时 logger 仍然可用（写时吞错），绝不影响启动。
            let log_dir = file_log::resolve_log_dir(&app.handle());
            let logger = std::sync::Arc::new(file_log::FileLogger::new(
                log_dir,
                file_log::DEFAULT_MAX_FILE_SIZE_MB,
                file_log::DEFAULT_RETAIN_DAYS,
            ));
            app.state::<TunnelManager>().attach_logger(logger);
            // 启动时自动拉起所有 enabled=true 的隧道（对齐 .NET 老版「启用即随应用启动」）；
            // 单个项目失败只写文件日志，不影响应用启动。
            tunnel_state::autostart_enabled_tunnels(&app.handle(), app.state::<TunnelManager>().inner());
            Ok(())
        })
        // 关窗不退出，只隐藏到托盘（后台常驻）
        .on_window_event(|window, event| match event {
            WindowEvent::CloseRequested { api, .. } => {
                if window.label() == "main" {
                    let _ = window.hide();
                    api.prevent_close();
                }
            }
            // 自绘托盘菜单：失焦即收起（等价于系统菜单点击外部关闭）
            WindowEvent::Focused(false) => {
                if window.label() == "tray-menu" {
                    let _ = window.hide();
                }
            }
            _ => {}
        })
        .invoke_handler(tauri::generate_handler![
            greet,
            crypto_self_check,
            tray_menu_state,
            tray_menu_action,
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
            reveal_config_dir,
            read_project,
            save_project,
            delete_project,
            import_project_yaml,
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
///
/// 启动自查在 App 刚起 ~1s 发起，此刻系统网络栈/代理/TLS 可能还没就绪，
/// rustls 握手容易直接失败（"error sending request"），而稍后手动检查却能成功。
/// 故失败时按 2s/4s 退避重试，掩盖启动瞬间的网络竞争；手动检查同样受益。
#[tauri::command]
async fn check_update(app: tauri::AppHandle) -> Result<UpdateInfo, String> {
    use tauri_plugin_updater::UpdaterExt;
    let current = app.package_info().version.to_string();
    let updater = app.updater().map_err(|e| e.to_string())?;

    let mut last_err = String::new();
    // 最多 3 次：立即、+2s、+4s。
    for attempt in 0..3u8 {
        if attempt > 0 {
            tokio::time::sleep(std::time::Duration::from_secs(2 * u64::from(attempt))).await;
        }
        match updater.check().await {
            Ok(Some(update)) => {
                return Ok(UpdateInfo {
                    available: true,
                    current,
                    latest: update.version.clone(),
                    notes: update.body.clone(),
                })
            }
            Ok(None) => {
                return Ok(UpdateInfo {
                    available: false,
                    current: current.clone(),
                    latest: current,
                    notes: None,
                })
            }
            Err(e) => last_err = e.to_string(),
        }
    }
    Err(format!("{last_err}"))
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

/// 在系统文件管理器中打开配置目录（.NET 老版「配置目录」按钮的对应行为）。
#[tauri::command]
fn reveal_config_dir(app: tauri::AppHandle) -> Result<String, String> {
    use tauri_plugin_opener::OpenerExt;
    let dir = tunnel_state::resolve_config_dir(&app);
    std::fs::create_dir_all(&dir).map_err(|e| format!("创建配置目录失败：{e}"))?;
    app.opener()
        .open_path(dir.display().to_string(), None::<&str>)
        .map_err(|e| format!("打开失败：{e}"))?;
    Ok(dir.display().to_string())
}

/// 读取单个项目原始文件（编辑器回填）。
#[tauri::command]
fn read_project(app: tauri::AppHandle, name: String) -> Result<serde_json::Value, String> {
    let dir = tunnel_state::resolve_config_dir(&app);
    // 文件名未必等于 name（真实案例：crm-localhost.yaml 内容是 `name: crm-local-dev`）。
    // 按 {name}.yaml 猜路径会读失败 → 编辑器回填全走默认值（"编辑界面数据不全"）。
    // 与 delete_project/save_project_file 同一口径：find 优先，{name}.yaml 兜底。
    let path = cryptunnel_tunnel::find_project_file(&dir, &name)
        .or_else(|| {
            let p = tunnel_state::project_file_path(&dir, &name);
            p.exists().then_some(p)
        })
        .ok_or_else(|| format!("未找到项目「{name}」的配置文件。"))?;
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

/// 导入拖拽进来的 YAML 项目文件（.NET 老版拖拽导入的对应行为）。
///
/// 原文落盘（不改写 YAML 结构，与 .NET `ImportProjectFile` 一致），但落盘前
/// 必须过与目录加载同一套字段校验 + 与存量项目的 name/端口冲突检查——
/// 坏文件一旦落盘会在每次刷新列表时反复报错，必须挡在目录之外。
/// 同名项目已存在时拒绝导入（让前端先删除或改名），避免静默覆盖他人配置。
#[tauri::command]
fn import_project_yaml(
    app: tauri::AppHandle,
    filename: String,
    content: String,
) -> Result<String, String> {
    let pf: cryptunnel_tunnel::ProjectFile =
        serde_yaml::from_str(&content).map_err(|e| format!("YAML 解析失败：{e}"))?;
    cryptunnel_tunnel::validate_project_fields(&pf)?;
    let name = pf.name.clone().unwrap_or_default();

    // 与存量项目冲突检查（复用目录加载结果，口径与列表页一致）。
    let dir = tunnel_state::resolve_config_dir(&app);
    let existing = cryptunnel_tunnel::load_config_dir(&dir);
    if existing.configs.iter().any(|c| c.name == name) {
        return Err(format!("项目「{name}」已存在，请先删除或改名后再导入。"));
    }
    let port = pf.local.as_ref().and_then(|l| l.port).unwrap_or(0) as u16;
    if let Some(c) = existing.configs.iter().find(|c| c.local_port == port) {
        return Err(format!(
            "local.port={port} 已被项目「{}」占用。",
            c.name
        ));
    }

    // 原文落盘（原子写），文件名以内容里的 name 为准（与列表刷新口径一致）。
    std::fs::create_dir_all(&dir).map_err(|e| format!("创建配置目录失败：{e}"))?;
    let path = tunnel_state::project_file_path(&dir, &name);
    let tmp = dir.join(format!(".{name}.yaml.tmp"));
    std::fs::write(&tmp, &content).map_err(|e| format!("写入失败：{e}"))?;
    std::fs::rename(&tmp, &path).map_err(|e| format!("落盘失败：{e}"))?;
    Ok(format!("已导入 {filename} → 项目「{name}」。"))
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
    // 不能按 {name}.yaml 猜路径：文件名未必等于 name（真实案例：crm-localhost.yaml
    // 的内容是 `name: crm-local-dev`）。猜错时旧实现会跳过删除却照样返回成功，
    // 界面提示"已删除"、刷新后项目复活。
    let path = cryptunnel_tunnel::find_project_file(&dir, &name)
        .or_else(|| {
            // 兜底：YAML 解析失败（name 读不出）但文件名恰好等于 {name}.yaml
            let p = tunnel_state::project_file_path(&dir, &name);
            p.exists().then_some(p)
        })
        .ok_or_else(|| format!("未找到项目「{name}」的配置文件。"))?;
    std::fs::remove_file(&path).map_err(|e| format!("删除失败：{e}"))?;
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

#[cfg(test)]
mod tests {
    use super::tray_menu_origin;

    /// 常规：菜单贴光标左上方展开（托盘在右下角，向上向左弹）。
    #[test]
    fn origin_sits_left_above_cursor() {
        let p = tray_menu_origin(
            (1000.0, 800.0),
            (176.0, 136.0),
            Some(((0.0, 0.0), (2048.0, 1280.0))),
        );
        assert_eq!(p, (824.0, 664.0));
    }

    /// 真实场景（本机 2560×1600 @125%）：光标在右下角托盘中点的下方，
    /// 菜单 220×170 物理像素必须**完整落在屏内**。
    /// 若误把配置里的逻辑值 176×136 当物理尺寸用，x 会算成 2540-176=2364，
    /// 右边缘 2364+220=2584 > 2560 → 菜单右侧被切掉 24px。
    #[test]
    fn origin_fits_on_screen_at_dpi_125() {
        let win = (220.0, 170.0);
        let mon = ((0.0, 0.0), (2560.0, 1600.0));
        let (x, y) = tray_menu_origin((2540.0, 1580.0), win, Some(mon));
        assert!(
            x + win.0 <= mon.1 .0 && y + win.1 <= mon.1 .1,
            "菜单越出屏幕：({x},{y}) + {win:?} > {mon:?}"
        );
        assert_eq!((x, y), (2320.0, 1410.0));

        // 变异对照：把逻辑尺寸 176×136 当物理尺寸传进去（show_tray_menu 修前的写法），
        // 菜单右边缘必然越界 —— 用这条反向断言证明上面那条「屏内」断言不是摆设。
        let (bad_x, _) = tray_menu_origin((2540.0, 1580.0), (176.0, 136.0), Some(mon));
        assert!(
            bad_x + win.0 > mon.1 .0,
            "用逻辑尺寸摆放本应越界，否则该断言无意义：bad_x={bad_x}"
        );
    }

    /// 光标落在显示器右/下边界之外时，clamp 到能完整放下菜单的位置。
    #[test]
    fn origin_clamps_when_cursor_beyond_monitor_edge() {
        let p = tray_menu_origin(
            (2400.0, 1400.0),
            (176.0, 136.0),
            Some(((0.0, 0.0), (2048.0, 1280.0))),
        );
        assert_eq!(p, (1872.0, 1144.0));
    }

    /// 左侧副屏物理原点是负值：不能把 x/y 简单 max(0) 掉，否则菜单会跑到主屏左上角。
    #[test]
    fn origin_supports_negative_secondary_monitor() {
        let p = tray_menu_origin(
            (-1900.0, 1000.0),
            (176.0, 136.0),
            Some(((-1920.0, 0.0), (1920.0, 1080.0))),
        );
        assert_eq!(p, (-1920.0, 864.0));
    }

    /// 取不到显示器信息时退化为「不越出屏幕原点」。
    #[test]
    fn origin_falls_back_to_origin_without_monitor() {
        let p = tray_menu_origin((10.0, 10.0), (176.0, 136.0), None);
        assert_eq!(p, (0.0, 0.0));
    }

    /// 显示器比菜单还小：下界不得反超上界（否则 f64::clamp panic）。
    #[test]
    fn origin_does_not_panic_on_tiny_monitor() {
        let p = tray_menu_origin(
            (100.0, 100.0),
            (176.0, 136.0),
            Some(((0.0, 0.0), (100.0, 100.0))),
        );
        assert_eq!(p, (0.0, 0.0));
    }
}
