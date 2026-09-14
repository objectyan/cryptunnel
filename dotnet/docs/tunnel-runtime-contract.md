# .NET 客户端隧道运行时层 —— 实现契约

> 状态：**已定稿**，实现层必须严格遵守。
> 依据：亲读服务端 `CryptunnelWebSocketHandler.java`(290 行) / `CryptunnelHttpController.java`(316 行)
> 与老 Java 客户端 `CryptunnelClient.java`(486 行) 得出，非推测。
> 目标：`dotnet/src/Cryptunnel/Tunnel/`（当前完全不存在）。

## 0. 为什么需要这份契约

`Crypto/` `Config/` `Models/` 三层已完成，但 `ProjectConfig.TunnelCipher`（ProjectConfig.cs:91）
**当前零调用方** —— 加密能力齐备却没有任何东西在用它。隧道运行时层是唯一缺口。

硬要求（用户原话）：**功能正常且完整，而且安全**。因此本契约禁止：
- 任何 `// TODO` / `throw new NotImplementedException()`
- "先跑起来的简化版"
- 把已知缺陷伪装成完整功能

验收标准是 **DBeaver 挂一整天不断线、无安全口子**。

## 1. 线级协议事实（服务端权威，不可改）

### 1.1 WebSocket 通道 `/ws-cryptunnel`

| 事实 | 服务端出处 | 对实现的强制约束 |
|---|---|---|
| **一个 WS 连接 = 一个 MySQL 连接**，`Map<String,Socket>` 以 `wsSession.getId()` 为键，**非多路复用** | Handler:41-42 | 每个 DBeaver 本地连接必须开**独立** WS，绝不能共用一条 WS 复用多个 TCP |
| 明文 `AUTH:` 前缀**直接拒绝** | Handler:70-76 | 认证报文必须先加密 |
| 第一条消息即认证，`AesUtil.decrypt(payload, aesKey, hmacKey)` | Handler:83 | 连上后**立刻**发认证，不得先发数据 |
| **认证成功不发任何 ACK**，直接开 MySQL 连接 | Handler:138-141 | 不能等确认帧，只能靠"没被关闭"判定成功 |
| 只处理 `TextMessage`；`handleBinaryMessage` **仅 warn 后丢弃** | Handler:173-176 | 必须用 `WebSocketMessageType.Text`，用 Binary 会**静默失败** |
| 认证失败 → `close(NOT_ACCEPTABLE, reason)` | Handler:143-148 | 靠 close reason 文本区分错误类型 |
| MySQL socket 设 `SoTimeout` + `KeepAlive` + `TcpNoDelay` | Handler:204-206 | 服务端已开 KeepAlive，客户端侧同样要开 |

认证明文格式（老客户端 CryptunnelClient.java:119）：

```
AUTH:{authKey}:{unixSeconds}:{nonce}
```

`nonce` = 16 字节随机数的**小写 hex**（32 字符，CryptunnelClient.java:126-134）。
服务端校验链：`parts.length < 4` 格式错(:95) → authKey 相等(:107) → `|now-ts| > window` 过期(:117)
→ nonce 重放(:126) → 记录 nonce 并清理超 2 倍窗口的旧值(:132-136)。

> 具名 target 支持 5 段格式 `AUTH:{authKey}:{ts}:{nonce}:{targetId}`。
> **现状缺口**：.NET `AesTunnel.BuildAuthMessage`（AesTunnel.cs）只生成 4 段，无 targetId 参数。
> 本轮必须补上可选 targetId，否则 .NET 无法连多数据源白名单中的具名 target。

### 1.2 九种关闭原因 → 中文提示映射

服务端 close reason 是英文短语，必须逐条映射为可行动的中文提示：

| close reason | 中文提示 | 是否可重试 |
|---|---|---|
| `Plain-text auth rejected` | 认证报文未加密被服务端拒绝（客户端 bug，请报告） | 否 |
| `First message must be auth` | 首条消息必须是认证报文（客户端 bug，请报告） | 否 |
| `Auth format error` | 认证报文格式错误，通常是 authKey 中含冒号 | 否 |
| `Auth key invalid` | 认证密钥(authKey)不正确 | 否 |
| `Auth timestamp expired` | 认证时间戳超出服务端允许窗口，请校准本机时钟 | 是（校时后） |
| `Auth nonce replay detected` | 认证随机数重放，通常是短时间重复重连 | 是（延迟后） |
| `Auth decrypt failed` | **加密算法(cipher)与服务端不一致，或 AES 密钥(aesKey)错误** —— 两者无法区分，请同时核对 | 否 |
| `MySQL connection lost` | 服务端与 MySQL 的连接断开 | 是 |
| `MySQL connection failed` | 服务端无法连接 MySQL | 是 |

**`Auth decrypt failed` 的歧义是本设计的固有性质**：解密失败发生在解析之前，服务端无从判断
是密钥错还是算法错。提示文案**必须同时提醒两种可能**，否则用户会在错误方向上排查很久。

## 2. 帧大小不变量（代码级，不是配置级）

ADR-0001 用**两轮** WS 1009 CLOSE_TOO_BIG 换来的结论，必须在代码里固化。

不变量：

```
Base64(密文长度) + 协议开销 < 接收端单帧上限
```

ADR-0001 明确**否决**了"把 buffer 调到 4096 刚好塞得下"的魔数做法，要求正确性 100%
落在客户端、绝不依赖服务端配置。因此：

- `ChunkSize`（默认 4096，ProjectConfig.cs:80）是**读缓冲上限**，不是保险丝
- **每次发送前必须断言载荷长度**，超限即抛异常并记日志 —— 这是**客户端 bug**，不是运行时错误
- 阈值常量 `MaxFramePayloadChars = 8192`（Tomcat `maxTextMessageBufferSize` 默认值），
  与 ChunkSize 分离定义，禁止用 `ChunkSize * 2` 之类的隐式耦合表达

三算法膨胀率不同，断言必须对三者都成立：

| 算法 | 帧结构 | 4096 B 明文 → 载荷估算 |
|---|---|---|
| `aes-256-cbc-hmac-sha256` | `Base64(IV[16]‖HMAC[32]‖ct)` | ct 补齐到 4112，(16+32+4112)=4160 B → Base64 ≈ **5548 字符** |
| `sm4-cbc-hmac-sha256` | 同 CBC，逐字节一致 | 同上 ≈ **5548 字符** |
| `aes-256-gcm` | `Base64(nonce[12]‖ct‖tag[16])` | (12+4096+16)=4124 B → Base64 ≈ **5500 字符** |

三者都远低于 8192，安全边界充足。断言用意不是"防止越界"而是**防止未来有人改大
ChunkSize 或换入膨胀率更高的算法时静默踩雷** —— 让它在开发期炸，而不是在用户
DBeaver 上炸成 `08S01 Communications link failure`。

实现要求：断言必须在**发送函数内部唯一入口处**，不能散落在各调用点。

## 3. HTTP 降级通道的本质缺陷与可用性边界

ADR-0001:37 把这个问题挂成了"独立课题"。本轮亲读 `CryptunnelHttpController.java` 坐实，
情况比预想更差。**必须诚实标注，不能让用户以为 HTTP 与 WS 等价。**

### 3.1 缺陷清单（服务端行为，客户端改不了）

| 缺陷 | 出处 | 后果 |
|---|---|---|
| `/tunnel` 是**严格请求-响应配对**：解密 → 写 MySQL → 同步读**一个**响应包 → 加密返回 | Controller:199-221 | 服务端**无法主动推送**。MySQL 主动发的数据（多结果集后续包、服务端主动关闭）只能等客户端下次 POST 时捎带 |
| 客户端无数据可发时，只能发空包去"钓" | 同上 | 要么引入轮询开销，要么丢失服务端推送 |
| `readMysqlPacket` 用 `in.available()` 非阻塞捎带更多数据 | Controller:282-289 | **竞态**：数据还在网络上飞时 `available()` 返回 0，剩余包滞留到下一次请求 → 大结果集卡顿/乱序 |
| `maxWait = 30000` | Controller:256 | 客户端每次 POST 最坏挂 **30 秒** |

### 3.2 结论与处置（本轮定案）

**客户端无法修复上述任何一条** —— 它们都在服务端的请求-响应模型里。而服务端属于
另一个仓库的生产 web 应用，本轮不动它（ADR-0003 硬约束 C-02：复用已有端口、仅 Java）。

因此定案：

1. **`TransportMode.Auto` 保持"先 WS 后 HTTP"的降级行为不变** —— 这是唯一能在
   WAF 拦截 WS 时保住可用性的路径，删掉它等于砍功能。
2. **降级发生时必须显式告警**：`TunnelRuntime.Status` 置为 `Degraded`（枚举已存在，
   ProjectConfig.cs:26），`LastError` 写入明确文案：
   > `已降级到 HTTP 长轮询：该通道为请求-响应模型，大结果集可能卡顿，长时间空闲可能延迟感知服务端断开。建议排查 WebSocket 为何不可用。`
   `WarnCount` 递增。
3. **不默认关闭、不标 experimental**。理由：它是**兜底**通道，用户遇到它时往往已经
   没有别的选择；默认关掉只会让人在 WAF 环境里彻底连不上，比降级更糟。但**必须**
   通过状态与文案让用户清楚知道自己在降级态运行。
4. **文档必须写明边界**，不能只说"支持 HTTP 降级"。

> 立场：**宁要一个诚实标注边界的功能，也不要一个假装完整的。**

## 4. 目标结构与职责划分

新增 `dotnet/src/Cryptunnel/Tunnel/`，五个文件，职责单一不重叠：

| 文件 | 职责 | 禁止 |
|---|---|---|
| `TunnelFraming.cs` | 帧大小不变量（§2）、`MaxFramePayloadChars` 常量、发送前断言 | 碰网络 |
| `AuthMessageBuilder.cs` | 生成 `AUTH:` 明文（4 段 / 5 段 targetId）、`RandomNumberGenerator` 生成 nonce、小写 hex | 碰网络、碰加密（加密调 `ITunnelCipher.Seal`） |
| `CloseReasonMapper.cs` | 九种 close reason → 中文提示 + 是否可重试（§1.2） | 任何逻辑分支之外的行为 |
| `WebSocketTunnel.cs` | 单条 WS 隧道全生命周期：连接 → 认证 → 双向泵 → 清理 | 触碰任何 UI 控件 |
| `HttpPollingTunnel.cs` | HTTP 降级隧道：`/connect` → `/tunnel` 循环 → `/disconnect` | 同上 |
| `TunnelSupervisor.cs` | 监听本地端口、accept 循环、每连接选路（Auto/WsOnly/HttpOnly）、重连、更新 `TunnelRuntime` | 直接实现协议细节 |

**架构红线**：隧道层只更新 `TunnelRuntime`（Models/Runtime.cs，已实现 `INotifyPropertyChanged`），
**绝不触碰任何 UI 控件**。UI 靠数据绑定自己刷新。

必须使用已有的 API，不得重新发明：
- `ProjectConfig.TunnelCipher` → `ITunnelCipher`（Crypto/ITunnelCipher.cs:23/29 的 `Seal`/`Open`）
- `ProjectConfig.WebSocketUrl` / `ConnectEndpoint` / `TunnelEndpoint` / `DisconnectEndpoint`
- `ProjectConfig.ChunkSize` / `AuthWaitMs` / `WsConnectTimeoutMs` / `HttpConnectTimeoutMs` / `HttpReadTimeoutMs`
- `TunnelRuntime.BytesSent` / `BytesReceived` / `ConnectionCount` / `ReconnectCount` / `ErrorCount` / `WarnCount` / `ActiveConnections` / `ConnectedSince` / `Status` / `CurrentTransport` / `LastError`

### 4.1 认证时序（关键，易错）

服务端**不发 ACK**（Handler:138-141），老 Java 客户端靠 `Thread.sleep(500)` 判定
（CryptunnelClient.java:222），`AuthWaitMs = 500` 就是这么来的。.NET 侧实现：

1. WS 连上（`ConnectAsync`，超时 `WsConnectTimeoutMs`）
2. **立刻**发认证文本帧（不能先发数据）
3. 等待 `AuthWaitMs`，期间**必须并发监听 close 帧** —— 认证失败表现为服务端关闭连接
4. 若期间收到 close：取 `CloseStatusDescription`，过 `CloseReasonMapper` 映射为中文，
   写 `LastError`，判定失败
5. 若 `AuthWaitMs` 到期仍 open：判定认证成功，启动双向泵

> **不能简单 `await Task.Delay(500)` 后查 `State`** —— 必须在这 500ms 内真正接收，
> 否则 close 帧携带的 reason 文本会丢失，用户只会看到"连接失败"这种无用提示。
> 这是 §1.2 那张映射表能否生效的前提。

## 5. 安全清单（逐条给结论，不适用就写不适用）

| 项 | 结论 |
|---|---|
| **监听地址暴露** | `ProjectConfig.ExposedToNetwork`（:98）识别 `0.0.0.0`/`::`/`*`/`+`。**结论：拒绝启动**。本地端口是**完全无认证**的 MySQL 入口，暴露到网卡等于把内网库开放给整个局域网。必须抛异常、`Status = Error`、`LastError` 写明危险原因，绝不"告警后继续" |
| **密钥落日志** | 严禁。老客户端只打印前 6 字符（CryptunnelClient.java:144-145），.NET 侧沿用。cipher id 可以打印（本地标识，非机密） |
| **明文落日志** | 严禁打印隧道明文字节内容。只允许打印**长度**与方向 |
| **nonce 随机源** | 必须 `RandomNumberGenerator`（密码学安全），**禁用** `Random` |
| **时间戳** | Unix 秒（`DateTimeOffset.UtcNow.ToUnixTimeSeconds()`），与 Java `System.currentTimeMillis()/1000` 对齐 |
| **算法标识上线** | 严禁。ADR-0003 方案 B：报文内零算法标识（防 DPI） |
| **解密失败处置** | 立刻关闭该隧道，**不得**把无法验证完整性的字节写给 DBeaver。HMAC/GCM tag 校验失败即视为攻击或配置错误 |
| **跨算法误配** | 由加密层保证硬失败（core 74 测试已覆盖 6 组合），隧道层只需**不吞异常** |
| **TLS 证书校验** | 用户配 `https://`/`wss://` 时使用 .NET 默认校验，**严禁**注入 `ServerCertificateCustomValidationCallback` 放行 |
| **端口占用** | `SocketException` 捕获后 `Status = Error` + 中文提示，不静默重试 |
| **本地连接来源校验** | **不适用**。监听已限定 `127.0.0.1`，再校验来源 IP 是冗余的 |
| **认证报文重放（客户端侧）** | **不适用**。防重放在服务端（nonce + 时间窗），客户端只需保证 nonce 每次新生成 |

## 6. 完成定义（DoD）

1. `dotnet/src/Cryptunnel/Tunnel/` 六个文件全部实现，**零 TODO、零 NotImplementedException**
2. `dotnet build -c Release` 零警告零错误
3. `ProjectConfig.TunnelCipher` 有真实调用方（当前为零）
4. 三算法均可发送（帧断言对三者成立）
5. 九种 close reason 全部有中文映射，`Auth decrypt failed` 文案同时提醒 cipher 与 aesKey
6. `ExposedToNetwork` 时拒绝启动
7. WS 用 Text 帧（非 Binary）
8. 认证等待期间真正接收 close 帧并取出 reason
9. HTTP 降级时 `Status = Degraded` + 明确告警文案
10. 隧道层零 UI 控件引用
