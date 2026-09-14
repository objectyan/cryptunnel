# ADR-0002: 与 orbien 的关系——只借鉴设计，不做协议兼容

- 状态: 已接受
- 日期: 2026-09-09
- 相关组件: `cryptunnel-core`、`cryptunnel-spring-boot-starter`、`cryptunnel-client`
- 参照项目: [lxien/orbien](https://github.com/lxien/orbien) v0.28.2（Apache-2.0）

## 背景

orbien 是一个基于 Netty 的通用内网穿透平台，功能覆盖面远超本项目。二者体量对比：

| 维度 | orbien v0.28.2 | 本项目 v1.3 |
|------|----------------|-------------|
| 代码量 | 925 个 Java 文件 / ≈10.7 万行 | core 26 文件 + starter 10 文件 |
| 网络层 | Netty | `java.net.Socket` + Spring WebSocket |
| 隧道协议 | 自定义 TMSP（protobuf，38 种消息类型），带多路复用 / Snappy·LZ4·ZSTD 压缩 | `Base64(IV‖HMAC‖AES-CBC)` TextMessage，一 chunk 一帧 |
| 传输通道 | TCP / WebSocket / QUIC | WebSocket + HTTP 长轮询降级 |
| 代理协议 | TCP / UDP / HTTP / HTTPS / SOCKS5 / 文件共享 | 仅 MySQL，且不解析协议（纯字节透传） |
| 认证 | mTLS + Token + IP CIDR + BasicAuth + 时间窗 | AES 加密的 `AUTH:{authKey}:{ts}:{nonce}[:{targetId}]` |
| 运维面 | Web 控制台（后端 462 文件 + Vue3 344 文件）+ ACME 自动证书 + 指标监控 | 无，yml 白名单且改动需重启 |

「兼容 orbien」有四种互斥解读，代价相差两个数量级：

| 方案 | 含义 | 代价 |
|------|------|------|
| A. 线级协议兼容 | 本客户端直连 orbien server，或本 starter 接 orbien 客户端 | 需实现 TMSP（protobuf 38 消息类型 + 帧编解码 + 多路复用握手），近似重写 |
| B. 借鉴设计 | 择优移植具体技术点（二进制帧 / 压缩 / 多路复用 / 配置形态等） | 按点计，每点 0.5~3 天 |
| C. 跑在其上 | 把本隧道降级为 orbien 的一条 TCP 代理规则 | 近似零代码 |
| D. 放弃自研 | 直接采用 orbien，废弃本项目 | 零开发 |

## 决策

**采用 B：只借鉴设计，不做任何线级协议兼容。**

理由：

1. **本项目的核心价值恰在 orbien 没有的地方。** `target` 白名单把 DB 地址与端口锁死在服务端、per-target 独立密钥、客户端**不能在报文中传任意 host:port**——这是 2026-09-08 方案文档 §9 明确的安全红线：*「绝不能让客户端在报文里传任意 host:port，否则服务端会变内网 SSRF 跳板」*。orbien 的 TCP 代理是通用转发，**天然允许客户端指定任意目标**，走 A / C / D 都会让出这条红线。
2. **A 的投入产出极差。** TMSP 是 protobuf 定义的 38 种消息类型 + 帧编解码 + 多路复用状态机。为一个 MySQL 专用隧道实现它，等于把 26 个文件的项目做成 925 个文件的项目，而换来的只是「能连 orbien server」这个本项目并不需要的能力。
3. **协议层零兼容反而是资产。** 本项目已承诺老客户端零改动（4 段 AUTH 报文兼容），线级格式是对**自己历史版本**的契约，不是对第三方的契约。引入 TMSP 会同时破坏两端兼容性。
4. **B 是唯一能保留安全模型又吃到技术红利的路径。** 具体借鉴点逐项另开 ADR 评估，不在本 ADR 一次性拍板。

## 后果

- 正面：安全模型（target 白名单 + per-target 密钥）完整保留；老客户端零改动承诺不变；可按痛点逐点吸收 orbien 的成熟做法，风险可控、可回退。
- 负面：永远无法与 orbien 生态互操作（其 Web 控制台、ACME、多协议代理都用不上）；若未来需求扩张到「通用内网穿透」，应当直接采用 orbien 而非继续扩本项目。
- 约束：借鉴 orbien 的具体实现代码时，须遵守其 Apache-2.0 许可（保留版权声明与许可声明）；仅借鉴设计思路不构成衍生作品。

## 待定（逐项另开 ADR）

候选借鉴点已登记于 `docs/decisions/OPEN-DECISIONS.md`，按实测痛点排序后逐项评估，不做功能对标式收集。

## 备选方案（已否决）

- **A. 实现 TMSP 达成线级兼容**：投入近似重写，且会破坏老客户端 4 段 AUTH 兼容承诺。否决。
- **C. 把 JDBC 隧道降级为 orbien 的一条 TCP 代理规则**：零代码，但 target 白名单、per-target 密钥、AUTH 协议全部废弃，服务端退化为通用转发即内网 SSRF 跳板。否决。
- **D. 放弃自研直接用 orbien**：orbien 无「MySQL 库白名单」这一安全特性，无法满足本项目的核心诉求。否决。
