# Cryptunnel改造方案：Maven Starter 化 + 多数据源分离

> 状态：待评审（先给方案，未动代码）
> 日期：2026-09-08
> 涉及：`sunrise`（服务端，Spring Boot 2.5.3 / JDK 8）· `cryptunnel-client`（客户端，.NET + Java）

---

## 一、现状与问题

服务端目前 4 个类全部硬编码在 `sunrise` 主工程里：

| 文件 | 职责 |
|---|---|
| `CryptunnelProperties` | 配置绑定 `cryptunnel.*`，**单例** `mysql-host` / `mysql-port` / `aes-key` / `auth-key` |
| `CryptunnelConfig` | 注册 WS 端点 `/ws-cryptunnel` |
| `CryptunnelWebSocketHandler` | WS ↔ MySQL TCP 双向转发，认证后 `new Socket(mysqlHost, mysqlPort)` |
| `CryptunnelHttpController` | HTTP 降级 `connect/tunnel/disconnect`，同样直连写死的 host/port |
| `AesUtil` | AES-256-CBC + HMAC-SHA256 加解密 |

两个痛点：

1. **复用性差** —— 任何新项目（MOM、NPR、BI）想开隧道，都得把这 5 个类复制一遍，改一处要同步 N 份。
2. **单套数据库** —— `mysql-host` 是全局单例。要连第二套库，现在只能再起一个 sunrise 实例或改配置重启，运维不可接受。

客户端反而已经就绪：`config.d/*.yaml` 天然支持多项目、多本地端口，只差服务端能按 target 路由。

---

## 二、目标

| 编号 | 目标 | 验收标准 |
|---|---|---|
| G1 | 服务端能力封装为 Maven 依赖 | sunrise 删掉本地 `cryptunnel` 包，只留 1 行 dependency + yml 配置，功能不变 |
| G2 | 一套服务端代理 N 套数据库 | 加一套库 = 加一段 yml，无需改代码、无需重启实例 |
| G3 | 老客户端零改动可继续用 | 现有 .NET 客户端 / Java jar 不升级也能连，行为与今天完全一致 |
| G4 | 库间权限隔离 | 拿到 A 库的 authKey 不能连 B 库 |

---

## 三、方案 A：Maven Starter 化

### 3.1 坐标与模块

新仓库（或 `cryptunnel-client` 下新增 maven 模块）：

```
io.github.objectyan:cryptunnel-spring-boot-starter:1.0.0
```

| 模块 | 内容 | 依赖 |
|---|---|---|
| `cryptunnel-core` | `AesUtil`、认证报文编解码、MySQL 包读取、`TargetRegistry` 接口 + 默认实现、`TargetDefinition` 模型 | 仅 `slf4j-api`（不依赖 Spring，便于单测和非 Spring 宿主复用） |
| `cryptunnel-spring-boot-starter` | `CryptunnelAutoConfiguration`、Properties、WS Handler、HTTP Controller、`spring.factories` | `core` + `spring-boot-starter-websocket` + `spring-boot-autoconfigure` |

### 3.2 ⚠ JDK 8 / Boot 2.x 关键约束（务必遵守）

sunrise 是 **Spring Boot 2.5.3 + JDK 8**，所以：

- 自动装配入口必须写 **`META-INF/spring.factories`**（`EnableAutoConfiguration=` 那套），**不能**用 Boot 3 的 `META-INF/spring/org.springframework.boot.autoconfigure.AutoConfiguration.imports`。
- 代码禁止 JDK 9+ API：`Map.of` / `List.of` / `var` / `Stream.toList` / `Objects.requireNonNullElse` 等一律不用。
- 依赖版本锁定：Lombok 1.18.x（与 JDK 8 兼容），`javax.crypto` 而非 `jakarta.*`。
- 建议 `core` 模块单独用 JDK 8 编译验证一次，避免本地 JDK 25 默认编译出的 class 版本过高。

### 3.3 接入方式（改造后 sunrise 的样子）

```xml
<dependency>
    <groupId>io.github.objectyan</groupId>
    <artifactId>cryptunnel-spring-boot-starter</artifactId>
    <version>1.0.0</version>
</dependency>
```

`CryptunnelAutoConfiguration` 用 `@ConditionalOnProperty(prefix="cryptunnel", name="enabled", havingValue="true")` 控制，
sunrise 侧 `WebConfig`/`SecurityConfig` 需要放行 `/ws-cryptunnel` 与 `/cryptunnel/**`（沿用现状即可）。

> 复用方（NPR、MOM）接入成本：1 个 dependency + 一段 yml + 安全放行，约 10 分钟。

---

## 四、方案 B：多数据源分离（核心）

### 4.1 设计思路

把「目标数据库」抽象成 **Target**，服务端维护一张注册表，客户端在**认证时声明要连哪个 target**，服务端据此路由。

关键点：**复用现有认证报文，向后兼容地追加一段**。

```
现在：  AUTH:{authKey}:{timestamp}:{nonce}
改造后：AUTH:{authKey}:{timestamp}:{nonce}:{targetId}
```

服务端解析规则（这是兼容的核心）：

| 客户端版本 | 报文段数 | 服务端行为 |
|---|---|---|
| 老客户端（.NET 现版 / Java jar） | 4 段 | `targetId` 缺省 → 路由到 `default` target，行为与今天一致 |
| 新客户端 | 5 段 | 按 `targetId` 查注册表路由 |

### 4.2 Target 配置形态（推荐：静态 yml + 全局缺省）

```yaml
cryptunnel:
  enabled: true
  ws-path: /ws-cryptunnel
  connect-timeout: 10000
  auth-time-window: 300

  # 全局缺省值，target 未配置时继承（兼容老配置的关键）
  default-target: cloudcc-prod

  targets:
    cloudcc-prod:
      display-name: CloudCC 生产库
      mysql-host: 10.6.10.12
      mysql-port: 3306
      aes-key: ${JDBC_AES_KEY_PROD}
      auth-key: ${JDBC_AUTH_KEY_PROD}
      max-connections: 5

    mes-test:
      display-name: MES 测试库
      mysql-host: 10.6.10.55
      mysql-port: 3306
      aes-key: ${JDBC_AES_KEY_MES}
      auth-key: ${JDBC_AUTH_KEY_MES}
      max-connections: 3

    bi-slave:
      display-name: BI 只读从库
      mysql-host: 10.6.10.78
      mysql-port: 3306
      aes-key: ${JDBC_AES_KEY_BI}
      auth-key: ${JDBC_AUTH_KEY_BI}
      max-connections: 2
```

**老配置兼容**：若仍是 `cryptunnel.mysql-host` / `aes-key` 平铺写法，自动包装成一个名为 `default` 的 target，
`application-dev.yml` / `application-prod.yml` **一行都不用改**。

### 4.3 密钥粒度：per-target（推荐）

| 方案 | 优点 | 缺点 | 结论 |
|---|---|---|---|
| 全局一把 aesKey/authKey | 客户端配置简单 | 任一库密钥泄露 = 全库失守 | ❌ 不采纳 |
| **per-target 独立密钥** | 库间隔离，G4 直接满足 | 客户端每套要配一对密钥（本来就要配） | ✅ 推荐 |

⚠ **必须做的校验**：服务端不能只查 `targetId` 存在就放行，必须 **用该 target 自己的 authKey 校验**，
否则会出现「拿 MES 的 authKey 去连生产库」的越权。同样，AES 解密也要用该 target 的密钥派生。

### 4.4 备选：动态注册（Phase 3，可选）

若希望「加库不改 yml 不重启」，提供 SPI：

```java
public interface TargetProvider {
    List<TargetDefinition> load();          // 从自家 DB / 配置中心 / Nacos 加载
}
```

starter 启动时合并 yml 配置 + 所有 `TargetProvider` Bean。
配套可选管理端点 `GET /cryptunnel/admin/targets`（需鉴权，默认关闭）。

> **建议 v1 不做**，静态 yml 已覆盖 90% 场景，动态注册引入的攻击面（谁能注册 target）需要额外设计。

### 4.5 另一个思路对比：一端口一库（不推荐）

| 思路 | 说明 | 评价 |
|---|---|---|
| 多 WS path / 多端口映射 | `/ws-cryptunnel-crm`、`/ws-cryptunnel-mes` | ❌ 运维重：每个端口要过防火墙、WAF、nginx；客户端要改 URL |
| **targetId 路由** | 一个端口，认证时声明 | ✅ 端口不变、防火墙不变、老客户端兼容 |

---

## 五、客户端配套改动

| 端 | 改动 |
|---|---|
| .NET 客户端 | `ProjectConfig` 加 `target` 字段；`AesTunnel.BuildAuthMessage` 拼上第 5 段；UI 表单加一个「目标库」输入框。**老配置文件无 `target` 字段 = 不传第 5 段，自动走 `default`** |
| Java 客户端 | 同上，`CryptunnelClient` 认证串追加 targetId |

客户端工作量很小 —— 且**不升级也能用**（走 default）。多套库 = 多个 yaml = 多个本地端口，现有机制直接支持。

---

## 六、安全要点

| 项 | 措施 |
|---|---|
| 越权连库 | targetId 必须在注册表内，且 authKey 用该 target 自己的密钥校验 |
| 重放 | 沿用现有 nonce + 时间窗口；**nonce 缓存建议按 target 分片**，避免某库 nonce 膨胀影响全局 |
| 密钥落盘 | yml 里统一走 `${ENV}` 占位，禁止明文进 git |
| 只读库 | 服务端是纯 TCP 转发不做 SQL 解析，只读靠 **MySQL 侧账号权限**控制，不靠代理 |
| 连接数 | `max-connections` per-target，避免一套库把全局连接打满 |

---

## 七、落地计划（分阶段，每阶段可独立上线）

| 阶段 | 内容 | 风险 | 工作量 |
|---|---|---|---|
| **P0** | 抽 `core` + `starter` 两模块，逻辑原样搬迁；`spring.factories` 装配；打 1.0.0 装到本地/私服 | 低（纯搬家） | 小 |
| **P1** | sunrise 删除本地 `cryptunnel` 包，改引 starter，回归验证 WS + HTTP 双通道 | 低 | 小 |
| **P2** | 引入 `TargetRegistry` + 多 target 路由 + 认证第 5 段（向后兼容） | 中（协议变更，但有兼容兜底） | 中 |
| **P3** | 客户端 .NET / Java 加 `target` 字段，UI 加输入框 | 低 | 小 |
| **P4** | （可选）`TargetProvider` SPI + 管理端点 | 中（鉴权设计） | 中 |

建议 **P0 → P1 一次上线**（功能等价替换，可回滚 = 回退依赖），**P2 → P3 一次上线**（服务端先兼容，客户端后升级）。

---

## 八、需要你拍板的决策点

| # | 决策点 | 我的建议 | 你的选择 |
|---|---|---|---|
| D1 | 新代码放哪：新建独立仓库 `cryptunnel`，还是放 `cryptunnel-client` 下新增 maven 模块？ | 放 `cryptunnel-client`（客户端服务端同仓，版本一起走） | ☐ |
| D2 | 是否要发到内网私服？还是先 `mvn install` 到本地仓库即可 | 先本地，跑通再发私服 | ☐ |
| D3 | Target 的密钥是 per-target 独立，还是全局一把？ | per-target 独立 | ☐ |
| D4 | v1 是否要做动态注册（SPI + 管理端点）？ | 不做，静态 yml 够用 | ☐ |
| D5 | `default` target 的默认行为：老客户端路由到哪套库？ | 路由到 `default-target` 指定的那套（dev=10.6.10.22，prod=10.6.10.12） | ☐ |
| D6 | 这次改造是否顺带把「读超时 0 = 永不超时」改掉？ | 建议改，给个 300s 默认，避免悬挂连接 | ☐ |

---

## 九、附录：数据库 IP/端口配在服务端，合理吗？

**结论：合理，且在本项目的网络拓扑下是唯一可行的选择。真正要改的不是「配在哪」，而是「怎么配」。**

### 9.1 为什么必须在服务端

| 理由 | 说明 |
|---|---|
| 物理拓扑 | 数据库在 `10.6.10.x` 内网，开发者本机**没有到内网的路由**。就算把 IP 配在客户端，TCP 也连不出去。服务端是唯一能同时被外网访问、又能触达内网的一端。 |
| 暴露面 | 内网 DB 拓扑属于基础设施资产，不该下发给每一台开发者机器。客户端只知 `serverUrl`，符合最小信息原则。 |
| 防 SSRF（红线） | 若允许客户端在报文里传任意 `host:port`，服务端立刻变成**内网 SSRF 跳板**——可连未授权 Redis、任意 MySQL 实例、探测其他 TCP 服务。**注册表白名单是安全特性，不是功能限制。** |

### 9.2 配置归属边界（改造后的目标态）

| 配置项 | 归属 | 理由 |
|---|---|---|
| DB `host:port` | **服务端**（target 注册表白名单） | 内网拓扑保密 + 防 SSRF |
| DB 账号密码 | **客户端**（DBeaver 连接串里填） | 服务端是纯 TCP 转发，从不解析、不持有 MySQL 凭据；权限仍由 MySQL 侧账号体系控制 |
| 连哪套库 | **客户端**（传 `targetId`） | 选择权在使用方，但**只能选白名单内已注册的 target** |
| `aesKey` / `authKey` | 双方各持一份，per-target | 隧道加密 + 库间隔离 |
| 服务端地址 `serverUrl` | 客户端 | 只有客户端知道自己要连哪个网关 |

> 现状已经满足第 2 条：MySQL 用户名密码走的是协议握手包，服务端全程不解密、不落盘。这个设计要保留，不要为了"集中管理"把 DB 凭据挪到服务端——那会把明文密码引入服务端配置和内存。

### 9.3 什么情况下「配在服务端」会变得不合理

| 场景 | 判断 | 应对 |
|---|---|---|
| 库数量爆炸（多租户，每客户一套库，几十上百个） | 注册表过长，yml 不可维护 | 上 P4：`TargetProvider` SPI，从配置中心 / 自家 DB 加载；或接 Nacos 热刷新 |
| 拓扑翻转：客户端和 DB 同在内网，服务端在公网 | 此时代理失去意义 | 直接内网直连，不需要这套代理 |
| 使用者需要自助临时连一个未注册的库 | 不能靠"开放任意 host"解决 | 走审批流 → 动态写入注册表（带 TTL），而不是放开白名单 |
| 加库要改 yml 重启，运维觉得重 | 这是**配置形态**问题，不是归属问题 | 静态注册表 + 可选热刷新，别因此去改归属 |

### 9.4 一句话总结

> **地址在服务端（白名单，防 SSRF），凭据在客户端（DBeaver，MySQL 侧控权），选择权在客户端但受白名单约束。**
> 现在的毛病是「一套实例只能代理一套库 + 加库要重启」，由 §4 的 `target` 注册表解决，与「配在哪」无关。
