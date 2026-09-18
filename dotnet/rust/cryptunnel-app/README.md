# Cryptunnel App（Rust + Tauri 客户端）

Cryptunnel 隧道客户端的 **Rust + Tauri 重写版**，目标是替代原 WPF（仅 Windows）客户端，实现：

- **多平台**：Windows / macOS / Linux 一套代码
- **托盘 / 后台常驻**：关窗不退出，驻留系统托盘
- **资源占用最少 + 体积小**：系统 webview 复用，无捆绑浏览器内核
- **可玩性高**：前端用 HTML/JS，想怎么改怎么改

## 目录结构

```
cryptunnel-app/
├── src-tauri/              # Rust 后端 + Tauri 配置
│   ├── src/
│   │   ├── lib.rs          # 应用入口：托盘/单实例/自启/通知 + Tauri command
│   │   └── main.rs         # 二进制入口（release 下 windows_subsystem）
│   ├── icons/              # 应用图标（icon.ico / icon.png）
│   ├── capabilities/       # Tauri 能力权限声明
│   ├── build.rs            # tauri-build 构建脚本
│   ├── Cargo.toml
│   └── tauri.conf.json     # 窗口/打包/产物配置
└── ui/                     # 前端（纯 HTML/CSS/JS，无框架，跑在系统 webview）
    ├── index.html
    ├── style.css
    ├── main.js
    └── icon.png
```

## 与加密层的关系

后端依赖同仓库的 [`cryptunnel-crypto`](../cryptunnel-crypto)（纯 Rust crate，AES-CBC-HMAC / AES-GCM / SM4-CBC-HMAC 三算法，已与 Java/.NET **字节级双向对齐**）。两者通过 cargo workspace 关联（workspace 根在 `cryptunnel-crypto/Cargo.toml`）。

## 构建与运行

前置：Rust（≥1.77，建议 stable）+ 对应平台 webview（Windows 自带 WebView2）。

```bash
cd src-tauri

# 开发调试（编译 debug，运行）
cargo run

# 仅编译
cargo build            # debug
cargo build --release  # release（已配 strip+lto+opt-level=s，体积小）

# 打包安装包（需先 cargo install tauri-cli，或 npm i -D @tauri-apps/cli）
cargo tauri build      # 产出 msi / dmg / AppImage / deb
```

## 已实现的骨架能力（spike）

| 能力 | 实现 | 状态 |
|---|---|---|
| 系统托盘图标 + 菜单 | 内置 `tray-icon`（显示主面板 / 退出） | ✅ |
| 关窗隐藏到托盘（后台常驻） | `on_window_event` 拦截 CloseRequested | ✅ |
| 左键点托盘唤起主窗 | `TrayIconEvent::Click` | ✅ |
| 开机自启 | `tauri-plugin-autostart`（UI 可开关） | ✅ |
| 单实例 | `tauri-plugin-single-instance`（重复启动激活已有窗口） | ✅ |
| 系统通知 | `tauri-plugin-notification`（已接插件） | ✅ 接线 |
| 前端 ↔ Rust 命令桥 | `invoke("greet" / "crypto_self_check" / "set_autostart" ...)` | ✅ |
| 加密层依赖接通 | `crypto_self_check` 调用 KDF | ✅ |

## 自动更新

后续接入 `tauri-plugin-updater`（替代原 Velopack，后者仅 Windows）。三平台 CI/打包见 `.github/workflows`（待补）。

## 后续里程碑

1. **#75 Rust 隧道核心**：WebSocket 客户端 + MySQL 握手/包编解码 + 「一个 WS = 一条 MySQL 连接」会话管理 + HTTP 降级 + AUTH 帧，对接 `cryptunnel-crypto`，对标 TunnelVerify 39 项。
2. **#76 平台服务**：托盘/自启/单实例/通知/更新完整化。
3. **#77 前端 5 个窗口**：主面板 / 连接配置 / 日志 / 关于 / 设置。
4. **#78 三平台 CI/发布**：windows/macos/linux 三 runner + tauri-action 打包。
