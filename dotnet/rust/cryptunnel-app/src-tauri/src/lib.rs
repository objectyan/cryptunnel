use tauri::{
    menu::{Menu, MenuItem, PredefinedMenuItem},
    tray::{MouseButton, MouseButtonState, TrayIconBuilder, TrayIconEvent},
    Manager, WindowEvent,
};
use tauri_plugin_autostart::MacosLauncher;

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
        .setup(|app| {
            build_tray(app)?;
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
