# cryptunnel

一个内嵌式的 **MySQL 隧道代理**：把本地 DBeaver 的 JDBC 连接，通过加密隧道转发到内网 MySQL。

服务端以 Spring Boot Starter 形式提供，**内嵌进你现有的 Web 应用，复用它已有的端口**，不新增任何监听端口、不需要独立部署穿透服务端。

---

## 它解决什么问题

数据库在内网（`10.6.10.x`），开发者在外网，中间的防火墙只放行了业务 Web 站点。

传统做法（frp / ngrok / orbien 等通用内网穿透）需要**额外部署一个穿透服务端，并开放新端口**。本项目不这么做——它把隧道能力做成 starter，直接挂在你已有的 Spring Boot 应用上：

```
DBeaver ──► 本地代理 ──► WebSocket 隧道 ──► [ 你的 Web 应用 ] ──► 内网 MySQL
          (客户端)     (加密, 复用 443)    (内嵌 starter)        (10.6.10.x)
```

宿主应用本来就能同时被外网访问和触达内网 MySQL，所以它天然就是那个跳板——**不需要再部署任何东西**。

---

## 核心特性

### 安全模型（这是本项目与通用穿透工具最本质的区别）

| 机制 | 说明 |
|------|------|
| **Target 白名单** | 内网 MySQL 的地址与端口**只配在服务端**，客户端**不能在报文里传任意 host:port** |
| **Per-target 独立密钥** | 每个 target 有独立的 `aesKey` / `authKey`，跨库越权在认证阶段即被拒绝 |
| **全链路加密** | 数据流经 AES-256-CBC 加密 + HMAC-SHA256 完整性校验后传输；算法**可插拔** |
| **防重放** | AUTH 报文携带时间戳与随机数，配合 nonce 缓存与时间窗校验 |

> **为什么 target 白名单是安全特性而不是限制？**
>
> 如果允许客户端在认证报文里自由指定目标地址，服务端就成了**内网的 SSRF 跳板**——未授权 Redis、任意 MySQL 实例、任意 TCP 端口探测，全都可打。本项目从协议层就杜绝了这一点：客户端只能说"我要连哪个具名 target"，而 target 指向哪里完全由服务端决定。

### 其它

- **加密算法可插拔**：通过 `TunnelCipher` SPI 替换算法；报文内**不含**任何算法标识（避免向 DPI 暴露特征），详见 [ADR-0003](docs/adr/0003-pluggable-tunnel-cipher.md)
- **复用宿主端口与证书**：宿主走 HTTPS，隧道自动是 WSS，无需额外申请证书
- **老客户端零改动**：AUTH 报文的 4 段格式（不带 targetId）继续有效，自动路由到 default target
- **双 artifact 覆盖 Boot 2 / Boot 3+**：`javax` 与 `jakarta` 两个 starter，源码等价
- **字节流透传**：不解析 MySQL 协议，由客户端 JDBC 驱动自行完成握手

---

## 快速开始

### 1. 服务端：引入 starter

根据宿主应用的 Spring Boot 版本二选一：

```xml
<!-- Spring Boot 2.x（javax.servlet） -->
<dependency>
    <groupId>io.github.objectyan</groupId>
    <artifactId>cryptunnel-starter-javax</artifactId>
    <version>1.0.0</version>
</dependency>

<!-- Spring Boot 3.x / 4.x（jakarta.servlet） -->
<dependency>
    <groupId>io.github.objectyan</groupId>
    <artifactId>cryptunnel-starter-jakarta</artifactId>
    <version>1.0.0</version>
</dependency>
```

### 2. 服务端：配置 target 白名单

```yaml
cryptunnel:
  enabled: true
  ws-path: /ws-cryptunnel          # 默认
  cipher: aes-256-cbc-hmac-sha256  # 默认；算法可插拔，见下文
  auth-time-window: 300            # AUTH 时间戳容差（秒），默认 300
  default-target: dev              # 老客户端（4 段 AUTH）路由到此 target
  targets:
    dev:
      display-name: 开发环境
      mysql-host: 10.6.10.22
      mysql-port: 3306
      aes-key: <每个 target 独立的 AES 密钥>
      auth-key: <每个 target 独立的认证密钥>
      max-connections: 5           # 默认 5
      connect-timeout: 10000       # 默认 10000（毫秒）
      read-timeout: 300000         # 默认 300000（毫秒）
    prod:
      display-name: 生产环境
      mysql-host: 10.6.10.12
      mysql-port: 3306
      aes-key: <另一个密钥，不要与 dev 相同>
      auth-key: <另一个密钥>
```

> `aes-key` / `auth-key` 请务必修改，默认值 `default-key-please-change` 仅用于占位。

**注意**：target 白名单在**应用启动时加载**，修改后需重启宿主应用。

### 3. 客户端

使用桌面客户端（`dotnet/` 目录下的 Windows 托盘应用）或老 Java 客户端，在本地起一个监听端口，DBeaver 连它即可。客户端配置里的 `aesKey` / `authKey` 需与服务端对应 target 一致。

---

## 与通用内网穿透工具的差异

| | frp / ngrok / orbien | 本项目 |
|---|---|---|
| 定位 | 通用内网穿透平台（TCP/UDP/HTTP/SOCKS5…） | **只做 MySQL 隧道** |
| 部署形态 | 独立服务端进程 + 开放新端口 | **内嵌 starter，复用宿主应用端口** |
| 目标地址 | 由客户端配置自由指定 | **服务端白名单锁定，客户端不可指定** |
| 证书 | 需自行申请/续期 | 复用宿主应用 TLS |
| 代码量 | 数万行起 | core 约 26 个类 |

这不是"另一个更全的穿透工具"。它牺牲了通用性，换来的是**部署成本为零**和**不可能被当作内网跳板滥用**。

---

## 构建

```bash
# core（必须 cd 到目录内执行，根 pom 的 reactor 不含它）
cd cryptunnel-core
mvn clean install

# starter 两个 profile
cd cryptunnel-spring-boot-starter
mvn -Pboot25  clean install
mvn -Pjakarta clean install
```

`cryptunnel-core` 编译目标为 **JDK 8**（不引 Lombok、不用 JDK 9+ API）。

---

## 文档

| 文档 | 内容 |
|------|------|
| `docs/spec.md` | 完整规格说明 |
| `docs/module-layout.md` | 模块结构与 pom profile 矩阵 |
| `docs/interface-contracts.md` | 核心接口契约 |
| `docs/adr/` | 架构决策记录（ADR） |
| `docs/replacing-the-cipher.md` | 如何替换加密算法（扩展指南） |
| `docs/decisions/OPEN-DECISIONS.md` | 悬而未决项登记册 |
| `docs/central-publish.md` | Maven Central 发布流程 |
| `docs/sunrise-integration.md` | 存量应用接入指引 |
| `docs/GLOSSARY.md` | 术语表 |

---

## License

[Apache License 2.0](LICENSE)
