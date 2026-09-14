# 贡献指南

感谢你愿意参与。本文档说明如何构建、有哪些硬性约束、以及提交 PR 前需要确认什么。

## 构建

### 前置要求

- JDK 17+（用于构建全部模块；`cryptunnel-core` 的**编译目标**仍是 1.8）
- Maven 3.6+

### 铁律：core 与 starter 必须各自 cd 到目录内构建

根 `pom.xml` 的 reactor **不包含** `cryptunnel-core` 与 `cryptunnel-spring-boot-starter`。
在仓库根目录执行 `mvn install` **不会**构建它们，也不会报错——这是最容易踩的坑。

```bash
# core（含 38 个单元测试）
cd cryptunnel-core
mvn clean install

# starter，两个 profile 各构建一次
cd cryptunnel-spring-boot-starter
mvn -Pboot25  clean install
mvn -Pjakarta clean install
```

## 代码规范

### `cryptunnel-core`

- **编译目标 JDK 8**，禁止使用 JDK 9+ API，包括但不限于：
  `Map.of` / `Map.ofEntries` / `Map.copyOf`、`List.of` / `List.copyOf`、`Set.of`、
  `Stream.toList` / `Stream.ofNullable` / `Stream.iterate`、`var`、
  `Objects.requireNonNullElse` / `requireNonNullElseGet`、`HttpClient`、
  `InputStream.readAllBytes`
- **不引入 Spring 依赖**，core 必须能被非 Spring 环境使用
- 新增代码需配套单元测试

### starter

- `javax` 与 `jakarta` 两个子模块的源码应保持**字节级一致**；差异只允许出现在
  模块名与自动配置类名上
- 自动配置声明需**双写**：
  - `META-INF/spring.factories`（Boot 2.x 通道）
  - `META-INF/spring/org.springframework.boot.autoconfigure.AutoConfiguration.imports`（Boot 3+ 通道）

## 兼容性红线

以下任何一项变更都属于**破坏性变更**，必须在 PR 中显式说明并评估影响：

| 项 | 说明 |
|----|------|
| 线级传输格式 | 当前为 `Base64(IV[16] \|\| HMAC[32] \|\| AES/CBC/PKCS7 密文)`，HMAC 覆盖 `IV \|\| 密文` |
| 密钥派生 | AES 与 HMAC 密钥**均由 `aesKey` 派生**（`SHA-256("AES:"+k)` / `SHA-256("HMAC:"+k)`），不是 `authKey` |
| AUTH 报文格式 | `AUTH:{authKey}:{ts}:{nonce}[:{targetId}]`，4 段必须继续路由到 default target |
| WebSocket 路径 | 默认 `/ws-cryptunnel` |
| HTTP 降级端点 | `/cryptunnel/connect` `/tunnel` `/disconnect`，头部 `X-Conn-Id` |
| 分片大小 | `CHUNK_SIZE = 4096`，受 ADR-0001 帧大小契约约束 |

**已部署的客户端必须能连新版服务端**，这是项目的核心承诺。

## 提交 PR 前请确认

- [ ] `mvn clean install` 在 core 与两个 starter profile 下均通过
- [ ] core 的 38 个单元测试全部通过，且新增了覆盖本次改动的测试
- [ ] 未触碰上表中的兼容性红线；若触碰，已在 PR 描述中说明原因与影响
- [ ] 涉及协议或架构的改动，已新增或更新 `docs/adr/` 下的 ADR

## 架构决策

重要的设计取舍记录在 `docs/adr/`。如果你的改动涉及架构方向，请先开 Issue 讨论，
再动手实现——本项目对"引入新概念"比较谨慎。
