// 生产模式下禁止出现控制台窗口（托盘后台工具）
#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

fn main() {
    cryptunnel_app_lib::run()
}
