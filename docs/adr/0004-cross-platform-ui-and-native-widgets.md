# ADR-0004：跨平台 UI 框架选型与原生小组件路径

- 状态：已接受
- 日期：2026-09-10（同日修订：目标平台收窄为三桌面）
- 决策者：项目负责人（授权 AI 代为决策）
- 相关：ADR-0003（可插拔加密）

> **目标平台：Windows / Linux / macOS 三个桌面端。移动端（iOS/Android）不在范围内。**
> 这个收窄不是小事 —— 它使「统一的原生小组件」目标**在 Linux 上不成立**，详见「Linux 的真相」一节。

## 背景

客户端要从「仅 Windows 的 WPF 应用」转为多平台，且需求包含两类小组件：

- **A 类：系统级原生小组件** —— macOS 通知中心 / iOS 主屏 / Android 桌面 AppWidget / Windows 11 Widgets Board。
- **B 类：应用内浮窗** —— 无边框置顶的状态小窗，本质是应用自身的二级窗口。

现有代码盘点（2026-09-10 实测）：

| 层 | 规模 | 平台耦合 |
|----|------|---------|
| `src/Cryptunnel.Core` | 37 个 .cs | **Windows 依赖 0**。唯一平台相关是 `PathResolver.cs:19,26,33` 三处 `Environment.SpecialFolder`，在 Linux/macOS 上本就能工作 |
| `src/Cryptunnel.App` | 2373 行 C# + 1774 行 XAML（6 个窗口） | 深度绑 WPF。`Tray/AppTray.cs` 28 处 Win 依赖，`AutoStart.cs` 写 HKCU Run 键 |

Core 零反向依赖 App，对外是事件驱动接口（`StateChanged` / `FatalError` / `StartAll` / `StopOne`），**换 UI 框架可原样复用**。

## 被推翻的三个前提（本次调研的核心产出）

### 误区一：「选 Tauri 就能拿到全平台原生小组件」—— 假的

`tauri-plugin-widgets` v0.3.0 的真实覆盖面：

| 平台 | 该插件的实际行为 |
|------|----------------|
| iOS 17+ / macOS 14+ | 接 WidgetKit，**但 widget 本体要自己写 SwiftUI**，插件只给 App Group 共享容器与 `reloadAllTimelines()` 触发 |
| Android | 接 Jetpack Glance，**widget 本体要自己写 Kotlin** |
| **Windows** | **退化为无边框透明窗口 —— 根本没接 Widgets Board** |
| **Linux** | 同上，退化为普通窗口 |

且插件文档明写 "The developer owns the widget extension target" —— 扩展 target 要自己建。

### 误区二：「Windows 小组件和 macOS 小组件是同一件事」—— 完全不同的技术栈

Windows 11 Widgets Board 的真实要求（Microsoft Learn 实证）：

- 必须 **MSIX 打包**，非打包应用**静默失败**
- 必须实现 **COM 接口 `IWidgetProvider`**（`CreateWidget` / `DeleteWidget` / `OnActionInvoked` / `OnWidgetContextChanged` / `Activate` / `Deactivate`）
- UI **不是你渲染的** —— 你提交 **Adaptive Cards JSON 模板**，由 Widget Host 渲染
- 需在 package manifest 注册 COM CLSID + widget provider extension
- 本地开发必须开 Developer Mode，否则 COM 激活被拒

### 误区三：「Linux 也有桌面小组件，照着做一份就行」—— Linux 根本没有统一的小组件系统

Windows 和 macOS 各有**唯一的、系统级的**小组件宿主（Widgets Board / 通知中心）。Linux 没有对应物，只有互不兼容的桌面环境各自的机制：

| 桌面环境 | 机制 | 技术栈 | 覆盖面 |
|---------|------|-------|-------|
| KDE Plasma | Plasmoid | **QML/Qt** + `kpackagetool` 安装 | 仅 KDE |
| GNOME | Shell Extension | **JavaScript (GJS)** + GNOME Shell 内部 API | 仅 GNOME，且**每个 GNOME 大版本都可能破坏兼容** |
| 任意 WM | Conky | **Lua 配置文件**，自绘窗口 | 不是「小组件」，是系统监视器 |
| Xfce/Cinnamon/... | 各自的 panel applet | 各不相同 | 各自为政 |

也就是说，"Linux 原生小组件" 要做**至少两份**（QML + GJS），且 GNOME 扩展需要跟随 GNOME 版本持续维护 —— GNOME Shell 扩展因大版本 API 变动而失效是常态。

**这对一个内网数据库隧道工具是不成比例的投入。** 用户装这个工具是为了让 DBeaver 连上内网 MySQL，不是为了美化桌面。

### 三条误区合起来的结论

**原生小组件根本不存在「跨平台方案」。** 每个平台都是独立的扩展进程、独立的渲染技术（Adaptive Cards / SwiftUI / QML / GJS）、独立的生命周期。任何框架都只能帮你**触发刷新与共享数据**，UI 一定要按平台各写一份 —— 而 Linux 上要写两份还得持续维护。

这条结论把「选哪个框架」和「能不能做小组件」**解耦**了 —— 既然哪个框架都得各写一份，框架选型就回到主应用本身的成本上。

## 决策

**主应用采用 Avalonia 12（.NET 10）；原生系统级小组件放弃（Linux 无统一宿主 + 投入产出比不成比例），改为在 Avalonia 内做跨平台状态浮窗。**

### 「小组件」的重新定义

上一轮我说的「A+B 都有」和调研中发现的「Linux 无统一小组件」有矛盾。这个矛盾必须在这里解决：

| 含义 | 可行性 | 理由 |
|------|--------|------|
| **系统级原生小组件**（macOS WidgetKit / Windows Widgets Board / KDE Plasmoid / GNOME Extension） | ❌ **放弃** | Linux 没有统一宿主，需要维护 Plasmoid（QML）+ GNOME Extension（GJS）至少两套 + 跟随版本升级持续维护。对 DB 隧道工具不成比例 |
| **应用内状态浮窗**（无边框置顶小窗，显示隧道状态、连接数、健康概览） | ✅ 在 Avalonia 内做 | Skia 自绘，Win/macOS/Linux **三端像素一致**，与主应用共享同一个 Core，Widget 生命周期由主应用管理 |

也就是说：你提的 A（系统原生小组件）在当前三平台目标下 **Linux 端走不通**；B（应用内浮窗）在 Avalonia 下**三端都能做**，而且简单得多。

### 为什么是 Avalonia（而非 Tauri）

| 维度 | Avalonia 12 | Tauri 2 |
|------|------------|---------|
| Core 37 个 .cs | ✅ **一行不改** | ❌ 要么全 Rust 重写，要么起 sidecar 进程 |
| 1774 行 XAML | ✅ 语法高度相似，MVVM 直接搬 | ❌ 6 个窗口全部重写为 Web 前端 |
| 语言栈 | **1 种**（C#） | **3 种**（Rust + Web 前端 + 各平台扩展） |
| 应用内浮窗 | ✅ Skia 自绘，三端一致 | ✅ WebView 渲染，三端一致 |
| 安装包 | ~40MB | 2-10MB（但 C# app 省不了） |
| 托盘（重要！） | Avalonia 社区有 `Avalonia.TrayIcon` / `Avalonia.Controls.TrayIcon`，API 类似 WPF 的 `NotifyIcon` | 有 `tauri-plugin-tray`，功能完整但是从 Rust 控 |
| 开机启动 | 写 autostart .desktop（Linux）/ launchd plist（macOS）/ Registry Run（Windows）—— 和 WPF 做法本质一样 | 有 `tauri-plugin-autostart` |

### Avalonia 12 的现状

- 2026-04-07 发布 12.0.0，当前版本 **12.1.0**（2026-07-09）
- net8.0 / net10.0 双 TFM，本项目 net10.0 直接用
- 内置跨平台 WebView（复用系统渲染引擎，不捆 Chromium）
- Linux 原生 Wayland 支持（预览）
- 生产用户：JetBrains、Unity、Schneider Electric、Autodesk
- MIT 许可（核心免费；XPF 兼容层与 Pro 控件收费，本项目不需要）

## 状态浮窗架构（取代原生小组件）

浮窗是主应用的一个 `Window`，不是独立进程。这消除了原生小组件方案的全部复杂度：无 App Group、无 30MB 内存天花板、无系统刷新预算、无跨进程序列化。

```
主应用（Avalonia，C#，单进程）
  ├─ Core.Stats / Core.Health          ← 已有数据源，零改动
  ├─ TunnelManager.StateChanged 事件   ← 已有，直接订阅
  │
  ├─ MainWindow          （主界面，可关闭到托盘）
  ├─ TrayIcon            （常驻，已有逻辑可迁移）
  └─ StatusOverlayWindow ← 【新增】无边框 / 置顶 / 可拖拽 / 半透明
       └─ 绑定同一个 ViewModel，实时更新（无需轮询快照文件）
```

浮窗显示内容（全部来自现有数据源，无需新增采集）：

| 字段 | 来源 |
|------|------|
| 隧道状态 | `TunnelState`：Stopped / Listening / WsConnecting / WsConnected / HttpConnecting / HttpConnected / Error |
| 当前传输方式 | `ProjectStats.Transport` —— **走 WS 还是 HTTP 降级**，这是最值得一瞥的信号 |
| 活跃连接数 | `ProjectStats.ActiveConnections` |
| 健康三层 | `HealthReport`：Transport / Authentication / Database，各自 Pass · Fail · **Skipped** |
| 已连接时长 | `ProjectStats.ConnectedSince` |
| 最近错误 | `ProjectStats.LastError`（截断显示，点击展开到主窗口） |

### 三条硬规则

1. **Skipped 必须保持三态显示。** `HealthReport.cs:7-21` 已经把 Pass / Fail / Skipped 严格三态化并写明了理由。浮窗空间小，最容易被偷懒压成「绿/红」二态 —— 那是在撒谎。置灰 `○` + 耗时显示 `—`（不显示 `0ms`），与现有 `HealthReportWindow` 保持一致。

2. **浮窗不提供控制入口。** 只读。点击一律回主窗口操作。一个能被误触启动内网数据库隧道的置顶小窗是安全隐患，不是便利功能。

3. **三端窗口行为差异必须实测。** 无边框 + 置顶 + 点击穿透在三个平台的行为不同：
   - Windows：`WS_EX_TOOLWINDOW` 语义，不占任务栏
   - macOS：需处理 Space 切换与全屏应用叠加
   - Linux：**Wayland 下无法由应用自行设置窗口绝对位置**（协议限制），X11 可以 —— 浮窗位置记忆功能在 Wayland 上要降级处理

第 3 条是 Linux 特有的坑，Avalonia 12 的 Wayland 支持仍是预览版，需要在实现阶段实测而非假定。

## 安全约束

放弃原生小组件后，**最大的一类安全风险自动消失了** —— 不再有跨进程共享容器，也就不存在「密钥写进低防护存储」的问题。这是选择浮窗方案的一个额外收益，不是次要考虑。

剩余约束：

| 规则 | 理由 |
|------|------|
| **浮窗不显示 `authKey` / `aesKey` / MySQL 账密 / 完整服务端 URL** | 浮窗是置顶常驻的，会出现在屏幕共享、录屏、截图中。演示时误泄露是真实场景 |
| **`lastError` 截断显示，完整内容需回主窗口** | 服务端 close reason 会带 `Auth key invalid` / `Auth nonce replay detected`；这些信息对排查有用，但不该常驻在屏幕上 |
| **浮窗只读，无控制动作** | 见上节规则 2 |
| **只显示项目别名，不显示真实 host:port** | 与 Target 白名单模型一致 —— 客户端本就不该暴露内网拓扑 |

这些与 ADR-0003 的「配置期约定、报文零算法标识」是同一思路：**不在低防护通道里放高价值信息**。屏幕就是一个低防护通道。

## 被否决的方案

| 方案 | 否决理由 |
|------|---------|
| **Tauri 2** | Core 是 C#，接 Tauri 要么全量 Rust 重写（丢掉已验证的加密/隧道实现），要么起 sidecar 进程（多一层 IPC 与生命周期管理）。放弃原生小组件后，Tauri 相对 Avalonia 的唯一优势（移动端 + WidgetKit 通路）也不再相关 |
| **Electron 43** | 80-200MB 安装包、100-300MB 内存。一个常驻后台、用户可能挂一整天的隧道工具不该占这个量级 |
| **.NET MAUI** | Linux 桌面要靠第三方 Avalonia 后端（绕一圈还是 Avalonia），且需 .NET 11（2026-11 GA）。绕路且要等 |
| **Flutter** | Core 是 C#，接 Flutter 要全量 Dart 重写。三桌面目标下没有任何能补偿这个代价的收益 |
| **原生系统小组件（三平台各写一份）** | Linux 侧要 Plasmoid（QML）+ GNOME Extension（GJS）**两套**，且 GNOME 扩展需跟随大版本持续维护。对 DB 隧道工具投入产出比不成立 |
| **保留 WPF + Linux/macOS 另写客户端** | 三份隧道实现必然漂移。这正是当前 `src/` 与 `dotnet/` 两棵树已经在犯的错误 |

## 影响

1. **重命名 `cryptunnel` → `cryptunnel` 最好在 Avalonia 迁移之前做。** 改名触达 209 个文件 / 1724 处标识符，如果在**已经迁完的 Avalonia 项目**上再做一遍，成本只会更高（XAML 命名空间 / csproj 引用 / 新目录结构都要再改一遍）。改名映射表是第一产出物。

2. **WPF → Avalonia 的 XAML 迁移量比预想的小。** 1774 行 XAML 中：
   - `MainWindow.xaml` 725 行（主界面 + 隧道列表）
   - `ProjectEditorWindow.xaml` 447 行（项目编辑）
   - `HealthReportWindow.xaml` 207 行（健康检查面板）
   - `AboutWindow.xaml` 232 行（关于页面）
   - `ErrorDetailWindow.xaml` 152 行（错误详情）
   - `App.xaml` 11 行（全局资源与启动逻辑）

   这些 XAML 的语法差距**主要是命名空间**（Avalonia 用 `https://github.com/avaloniaui` 而非 `http://schemas.microsoft.com/winfx/2006/xaml/presentation`），以及少数 WPF 专用特性。已有 WPF 经验的开发者迁移不是从零写。

3. **AppTray.cs（226 行，28 处 Win 依赖）需要完全重新处理。** Avalonia 的托盘方案与 WPF `NotifyIcon` 不兼容。macOS 托盘与 Windows 的语义也不同（macOS 菜单栏有限高、没有 balloon tip）。**Avalonia 社区有 `Avalonia.TrayIcon` 库**，但它在 Linux 上依赖 `libnotify`，需要验证。

4. **Linux 特有的两个坑必须在开发阶段验，不能「部署时再说」：**
   - Wayland 下**无法设置窗口绝对位置**：这是协议层限制（不是 Avalonia 的 bug），浮窗位置记忆功能必须降级处理
   - `libnotify`：托盘通知依赖 `libnotify`，发行版默认可能未装（Arch / Fedora minimal 已遇多次）

5. **两棵 .NET 源码树的去留问题需在此框架下重新评估。** v1.2.12（`src/`）是现役 net10.0，v2.0（`dotnet/`）是 net8.0 重写。Avalonia 方向选择 net10.0，v2.0 的 net8.0 分支意义下降，但其 `CipherRegistry` + `ITunnelCipher` 实现是迁移时的参考，不必立即丢弃。

6. **放弃原生系统级小组件后，架构洁净度显著提升。** 无跨进程数据序列化、无共享容器安全顾虑、无三平台扩展维护。新增代码只有 `StatusOverlayWindow` 这一个 Avalonia Window + ViewModel，估计 ~150-250 行 XAML + 同量级 C#，远少于原生小组件方案的 ~150 行共享层 + 300-600 行各平台扩展。
