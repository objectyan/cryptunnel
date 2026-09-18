# cryptunnel-tunnel（Rust 隧道核心）

Cryptunnel 隧道运行时层的 **Rust 重写**，与 .NET `Cryptunnel.Core.Tunnel` 及 Java 服务端
**逐点对齐**。本地 TCP 监听 → 加密 WebSocket/HTTP 隧道 → 服务端。

加密由同仓库 [`cryptunnel-crypto`](../cryptunnel-crypto) 提供（已字节级双向对齐）。

## 模块职责（单一不重叠）

| 模块 | 职责 | 碰网络 |
|---|---|---|
| `framing` | 帧大小不变量守卫（`MAX_FRAME_PAYLOAD_CHARS=8192`、发送前断言） | 否 |
| `auth` | `AUTH:` 明文生成（4/5 段）+ 加密封装、nonce、密钥遮蔽 | 否（加密调 crypto） |
| `close_reason` | 服务端 close reason → 中文提示 + 是否可重试 | 否（纯查表） |
| `addr_policy` | 本地监听地址安全策略（回环判定 + 非回环启动闸门） | 否 |
| `registry` | `TunnelCipher` 注册表（别名严格对齐 Java：仅 SM4 有 `sm4`） | 否 |
| `config` | 隧道运行配置（默认值与 .NET 一致） | 否 |
| `project_config` | 多项目 YAML 原始结构 + 回写（只写非默认字段，health 段原样保留） | 否 |
| `config_loader` | 配置目录扫描 + defaults 两遍合并 + 逐条校验（对齐 .NET ConfigLoader） | 否 |
| `tunnel` | 网络核心：监听 → 选路 → 认证 → 双向泵 → 清理 | 是 |

## 协议对齐点（与服务端 `CryptunnelWebSocketHandler` 一致）

- **一个 WS 连接 = 一个 MySQL 连接**（非多路复用），每个本地 TCP 连接开独立 WS。
- 首帧必须是**加密后**的认证报文，服务端对明文 `AUTH:` 前缀直接拒绝。
- 认证成功**不发任何 ACK**，直接开 MySQL 连接、由 reader 线程把握手包推过来。
  收到数据 = 认证已过，首帧（MySQL 握手包）必须回传给 DBeaver。
- 只用 **Text** 帧（服务端 `handleBinaryMessage` 仅 warn 丢弃；收到 Binary 即断开）。
- 认证失败表现为服务端 close 帧，reason 经 `close_reason` 映射为中文
  （`Auth decrypt failed` 文案同时提醒 cipher 与 aesKey 两种可能）。
- 帧发送走**唯一入口** `send_frame`，超 8192 字符即中止（防 WS 1009）。
- 解密失败**立即断开**而非跳过——MySQL 协议是有状态字节流，丢帧即错位。

## 安全红线（契约 §5）

- 非回环监听未被 `allow_non_loopback` 显式许可时**拒绝启动**（启动闸门，非告警后继续）。
- 密钥/明文严禁落日志；只打印长度与方向；密钥打印仅前 6 字符。
- nonce 用 `OsRng`（密码学安全），小写 hex；时间戳 Unix 秒。
- 报文零算法标识（ADR-0003 方案 B，防 DPI）。
- TLS 用 rustls（`rustls-tls-native-roots`），严禁注入跳过证书校验的回调。

## HTTP 降级边界

`TransportMode::Auto` 保持「先 WS 后 HTTP」降级。HTTP 通道是严格请求-响应模型，
**服务端无法主动推送**，大结果集可能卡顿、长时间空闲延迟感知断开。降级发生时
记录明确告警文案（不假装与 WS 等价）。

## 架构红线

隧道层只发 `TunnelEvent`（状态/日志/字节流/致命错误），**绝不触碰 UI**。
UI（Tauri 前端）靠 `emit("tunnel-event", ...)` 事件驱动刷新。

## 验证

```bash
cargo build   # 编译
cargo test    # 38 项单元测试：auth/close_reason/framing/addr_policy/registry/config/config_loader/project_config
```

依赖：tokio / tokio-tungstenite(rustls) / reqwest(rustls) / futures-util。
