# Spec — Cryptunnel 多 Target Maven Starter v1.0.0

> 生成日期：2026-09-08
> 基于：现状调研（sunrise 服务端 5 类 + .NET/Java 客户端）+ 用户决策
> 状态：待评审 / **本轮仅设计，不写实现代码**
> 用户拍板项：
> - 模块位置：cryptunnel-client 下新增两个 maven 模块
> - 兼容：JDK 8 + Spring Boot 2.5.3 / 3.5.x / 4.1.x（三套双 baseline 双发布）
> - Target 密钥粒度：per-target 独立 aesKey/authKey
> - 发布：先 `mvn install` 本地仓库；Central 发布脚本与 README 由用户后续自行跑

---

## 1. 产品定义

- **一句话描述**：把现有 sunrise 服务端 Cryptunnel 隧道封装成 Spring Boot Starter，支持一套服务端同时代理多套 MySQL，并兼容 Spring Boot 2.5/3.x/4.x 三条主线。
- **目标用户**：sunrise 团队及后续 MOM / NPR / BI 等使用 Cryptunnel的开发人员
- **核心问题**：
  1. 现有服务端代码内嵌在 sunrise 中，无法被其他项目复用
  2. 一套服务端只能代理一套 MySQL，加库要改 yml + 重启
  3. 现 sunrise 锁 JDK 8 + Boot 2.5，后续项目上 JDK 17 / Boot 3.x / 4.x 时，代理能力无法直接复用

---

## 2. MVP 范围（锁定）

| 优先级 | 功能 | 验收标准摘要 | 风险 |
|--------|------|-------------|------|
| P0 | 拆 `cryptunnel-core` 模块（JDK 8 编译，不依赖 Spring） | AesUtil/TargetDefinition/TargetRegistry/AuthMessageCodec 单测与现 sunrise 字节级一致 | 低 |
| P0 | 拆 `cryptunnel-spring-boot-starter` 模块 | 两个 profile：boot25（javax，Boot 2.5.3）/ jakarta（Boot 3.x / 4.x），每个 `mvn install` 成功 | 中（兼容矩阵） |
| P0 | 多 Target 注册表 + 路由 | 5 段 auth 报文按 targetId 命中；4 段报文走 default target；老 yml 自动包装 | 中（协议兼容） |
| P0 | 自动装配同时支持 spring.factories 与 AutoConfiguration.imports | Boot 2.5 走 spring.factories；Boot 3.4+/4.x 走 .imports；两个机制并行 | 中 |
| P0 | sunrise 接入示范文档 | docs/sunrise-integration.md + yml diff | 低 |
| P1 | 客户端 .NET/Java 协议升级说明（targetId 第 5 段） | docs/client-upgrade.md | 低 |
| P1 | Maven Central 发布配置与脚本 | docs/central-publish.md + ~/.m2/settings.xml 模板 | 低 |
| P2 | `TargetProvider` SPI 动态注册（配置中心热加载） | 留接口，**本期不实现** | — |

---

## 3. 明确不做（Out-of-Scope — 锁定）

| 不做的功能 | 原因 | 何时考虑 |
|------------|------|----------|
| 让客户端在报文中任意指定 host:port | 服务端会变内网 SSRF 跳板，违反红线 | 不做，明确拒绝 |
| 服务端解析 SQL / 改写协议 | 保持纯 TCP 转发，性能最高，攻击面最小 | 不做 |
| 服务端托管 MySQL 账号密码 | 凭据应在客户端，权限由 MySQL 侧账号体系控制 | 不做 |
| TLS / mTLS | 现网已有 WAF + 加密 WS，TLS 在 4 层终止不在本代理职责 | 后续按需 |
| 客户端内置数据库浏览器 | 客户端职责，不在本项目 | 客户端自己 |
| 多协议（PostgreSQL/Oracle/SQLServer） | MySQL 协议特殊（握手包），本期只代理 MySQL | 后续扩展 |
| 高可用（HA / 多服务端实例） | 单实例够用，状态本地 Map；HA 引入分布式锁和共享会话 | 上量后再做 |

---

## 4. 技术架构（锁定 — 含版本锚定）

### 4.1 模块图

```
cryptunnel-client/                          # 现客户端仓库
├── pom.xml                                  # packaging=pom
├── cryptunnel-core/                         # 新模块 1
│   ├── pom.xml                              # packaging=jar，maven.compiler.source=1.8
│   └── src/main/java/io/github/objectyan/cryptunnel/core/
│       ├── crypto/AesUtil.java              # 字节级搬迁
│       ├── auth/AuthMessageCodec.java       # 4段/5段 报文解析
│       ├── target/TargetDefinition.java
│       ├── target/TargetRegistry.java       # 接口
│       ├── target/DefaultTargetRegistry.java# 实现 + 老 yml 兜底
│       └── tunnel/TcpTunnel.java            # MySQL 包读取 + Socket 转发
├── cryptunnel-spring-boot-starter/          # 新模块 2
│   ├── pom.xml                              # 2 个 profile（boot25 / jakarta）
│   └── src/main/
│       ├── java/io/github/objectyan/cryptunnel/starter/
│       │   ├── CryptunnelProperties.java
│       │   ├── CryptunnelAutoConfiguration.java
│       │   ├── CryptunnelWebSocketConfigurer.java
│       │   ├── CryptunnelWebSocketHandler.java
│       │   ├── CryptunnelHttpController.java
│       │   └── servlet/ServletApiAdapter.java  # 反射适配 javax/jakarta
│       └── resources/META-INF/
│           ├── spring.factories                  # Boot 2.x 走这
│           └── spring/org.springframework.boot.autoconfigure.AutoConfiguration.imports  # Boot 3.4+/4.x 走这
└── docs/
    ├── maven-starter-and-multi-datasource-plan.md   # 原方案
    ├── spec.md                                       # 本文件
    ├── sunrise-integration.md                        # 接入指引
    ├── client-upgrade.md                             # 客户端协议升级
    └── central-publish.md                            # Central 发布
```

### 4.2 兼容矩阵（硬约束）

| 维度 | 约束 | 实施手段 |
|------|------|---------|
| JDK 编译下限 | `core` 编译用 **JDK 8 API**（maven-compiler-plugin `<source>1.8</source>`） | 用 `mvn install` 时强制 -Drelease=8 |
| JDK 运行下限 | `core` runtime 仍可在 JDK 8 上跑 | 不引入需 JDK 11+ 的 API（无 `var`、无 `Map.of`、无 `Stream.toList`、无 `Objects.requireNonNullElse`） |
| Spring Boot 主线 | **2.5.3**（sunrise 现状）/ **3.5.x**（NPR/MOM 现状）/ **4.1.x**（最新） | 两个 profile：boot25 编译用 Boot 2.5.3，jakarta 编译用 Boot 3.5.0（消费方如要 Boot 4.1.x，自己覆盖 `spring-boot.version` 即可，starter 内部依赖全部走该属性） |
| 自动装配 | Boot 2.x 看 `META-INF/spring.factories`；Boot 3.4+ 看 `META-INF/spring/...AutoConfiguration.imports` | 两个文件同时输出，**内容指向同一个 AutoConfiguration 类全名** |
| Servlet API | Boot 2.5 = `javax.servlet.*`；Boot 3.x/4.x = `jakarta.servlet.*` | 不在 starter 源码直接 import；用 `ServletApiAdapter` 反射加载 `Handler` 实现类 |
| Lombok | core **不引** Lombok（保持纯 Java，让非 Spring 工程也能用） | 手写 getter/setter |
| 加密算法 | 沿用现 sunrise：AES/CBC/PKCS5Padding + 随机 IV + HMAC-SHA256 | core 的 `AesUtil` 字节级搬迁 |

### 4.3 版本号规则

```
1.0.0  (cryptunnel-starter-javax)   对应 spring-boot 2.5.3，编出 jar 给 sunrise 用
1.0.0  (cryptunnel-starter-jakarta)  对应 spring-boot 3.5.0；消费方 app 可自行覆盖 spring-boot.version 切到 4.1.x
```

> 2026-09-08 追加决策：之前规划的三 profile（boot25 / boot3x / boot4x，发 `1.0.0-boot25` / `1.0.0-boot3x` / `1.0.0-boot4x`）
> 合并为两 profile（boot25 / jakarta，统一发 `1.0.0`）。原因：本 starter 不直接 import servlet API，jakarta jar 在 Boot 3.5 与 4.1 下
> 编译产物字节级一致，没必要贴两个版本号。后续如发现某条 Boot 主线必须单独 bump，再加 profile 也来得及。

### 4.4 pom 关键依赖锁定

| 依赖 | 范围 | 备注 |
|------|------|------|
| `spring-boot-starter-websocket` | starter | 用对应 Boot profile 选版本 |
| `spring-boot-starter-web` | starter | 同上 |
| `spring-boot-configuration-processor` | starter，optional=true | IDE 配置提示 |
| `junit-jupiter` | test | 5.x |

**不引** Spring Security —— 由使用者自己决定是否在 SecurityConfig 里放行 `/ws-cryptunnel` 与 `/cryptunnel/**`。

---

## 5. API 端点清单（锁定）

> WebSocket 端点路径与 HTTP Controller 路径与现 sunrise 完全一致，**调用方零感知**。

| Method | Path | 功能 | 兼容 |
|--------|------|------|------|
| WS | `/ws-cryptunnel` | 主隧道 | 沿用现 sunrise |
| POST | `/cryptunnel/connect` | HTTP 降级 - 建立 | 沿用现 sunrise |
| POST | `/cryptunnel/tunnel` | HTTP 降级 - 转发 | 沿用现 sunrise；header `X-Conn-Id` 保留 |
| POST | `/cryptunnel/disconnect` | HTTP 降级 - 关闭 | 沿用现 sunrise |

**协议变更（仅加，不改）**：
- WebSocket 首条消息格式由 `AUTH:{key}:{ts}:{nonce}` 升级为 `AUTH:{key}:{ts}:{nonce}:{targetId}`；4 段时 targetId 视为 `default`
- HTTP 降级三个端点同理，targetId 在 connect/disconnect 的 body 里

---

## 6. 数据库表清单（无 — 本项目不持久化任何数据）

> target 注册表是 yml 配置，不入 DB；nonce 在内存 ConcurrentHashMap 缓存（沿用现 sunrise），重启失效

---

## 7. 页面清单（无 — 纯服务端库，不含 UI）

---

## 8. 设计 Token（无 — 不含 UI）

---

## 9. 验收标准（EARS 格式 — 锁定）

| 编号 | 功能 | EARS 验收标准 | 优先级 |
|------|------|---------------|--------|
| AC-01 | 模块拆分 | While `mvn -Pboot25 install`，系统**必须**生成 `cryptunnel-core-1.0.0.jar` 与 `cryptunnel-starter-javax-1.0.0.jar`，无编译错误 | P0 |
| AC-02 | 加密兼容 | While 使用同一 aesKey，core 在 JDK 8/17/25 上 `encrypt(decrypt(x))` **必须**与现 sunrise `AesUtil.encrypt/decrypt` 输出字节完全一致 | P0 |
| AC-03 | 跨语言兼容 | While .NET 客户端用 `AesTunnel.Encrypt` 加密并发送，服务端 core **必须**能解密；反之亦然 | P0 |
| AC-04 | 协议兼容老客户端 | While 客户端发送 4 段 AUTH 报文（无 targetId），服务端 **必须** 路由到 `default-target` 配置的 MySQL，行为与改造前完全一致 | P0 |
| AC-05 | 多 Target 路由 | While 客户端发送 5 段 AUTH 报文带 targetId，服务端 **必须** 在注册表内查该 target；target 不存在则拒绝连接 | P0 |
| AC-06 | 库间隔离 | If targetA 客户端拿 targetA 的 authKey 试图连 targetB，服务端 **必须** 拒绝（authKey 必须与该 target 自己的密钥一致） | P0 |
| AC-07 | 老 yml 自动包装 | While 配置文件只写 `cryptunnel.mysql-host/aes-key/auth-key`（无 targets 段），系统启动时 **必须** 自动包装为名为 `default` 的 target；`default-target` 属性指定时使用其值 | P0 |
| AC-08 | 两 profile 编译 | While `mvn -Pboot25 install` / `-Pjakarta install`，两套 jar **均必须** 编译成功，并各自 `mvn dependency:tree` 输出的 Spring Boot 版本号与 profile 声明一致 | P0 |
| AC-09 | 自动装配入口 | While 引入 starter，Boot 2.5 启动日志 **必须** 出现 "CryptunnelAutoConfiguration"，且 WS endpoint `/ws-cryptunnel` 被注册 | P0 |
| AC-10 | sunrise 接入 | While sunrise 删除本地 `cryptunnel` 包并在 pom 引 starter，sunrise `mvn compile -P dev` **必须** 成功；`/ws-cryptunnel` 与 `/cryptunnel/**` 仍可访问 | P0 |
| AC-11 | 文当版本 | 所有 docs/ 文档**必须** 列出 maven 命令、目标仓库 URL（Central https://repo1.maven.org/maven2/）和本机验证步骤 | P1 |

---

## 10. 边界与约束

- **JDK 8 硬约束**：core 模块不能引入需 JDK 11+ 的 API；CI 必须用 Zulu 8 或 Temurin 8 跑 `mvn -Pboot25 install` 一次
- **不解析 SQL**：服务端只看 TCP 字节流；权限只由 MySQL 账号控制
- **状态本地**：nonce 与 mysql socket 都在内存；多实例时各实例独立（不冲突，因为每连接独立会话）
- **配置文件层级**：target 内部字段缺失时启动失败抛 `IllegalStateException`（fail-fast），不静默兜底
- **不内置安全框架**：使用者自己接 Spring Security 时需手动放行，文档里给示例
- **环境变量占位**：target.${name}.aes-key / auth-key 支持 `${ENV_VAR}` 占位，dev/prod 分离时不要把明文密钥写进 yml
- **CHUNK_SIZE = 4096**：保持与现 sunrise 一致（仅在 http controller 分片上传路径上）；WS 端不做分片（依赖 TCP 流）

---

## 11. 内嵌已知坑（从项目记忆拉取）

| 坑 | 技术栈指纹 | 根因 | 修法 |
|----|------------|------|------|
| Spring Boot 2.5 的 spring.factories 不用改 | spring-boot-autoconfigure 2.5.3 | Boot 2.5 仍以 spring.factories 为准 | starter 写 `META-INF/spring.factories` 时确保 `EnableAutoConfiguration=...` 一行 |
| Boot 3.4+ 用新机制 | spring-boot-autoconfigure 3.4+ | Spring 改用 `AutoConfiguration.imports` 文件 | 两个文件同时输出，指向同一个全限定类名 |
| Spring Boot 4.0 升 jakarta + Jackson 3 | spring-boot 4.0 | Boot 4 已迁移 jakarta，且 Jackson 3 是默认 | starter 不要直接用 Jackson，HTTP controller 只收/返 String |
| JDK 8 不能用 `Map.of/Stream.toList/var` | java 1.8 | language level | IDE 装 "Language Level = 8" 插件；code review 拦截 |
| Lombok 1.18.28 与 JDK 21+ 不兼容 | lombok 1.18.28 + jdk 21 | JCTree$JCImport.qualid 字段被改名 | core 不引 Lombok，避免 |
| `Objects.requireNonNullElse` 是 JDK 9+ | jdk 9+ |  | 用 `obj != null ? obj : defaultValue` 手写 |
| `mvn` 在本机 wrapper 是坏的 | maven 3.9.10 | `/c/tools/...` POSIX 路径被 java.exe 误读 | 用 `mvn.cmd` |
| 沙箱 HTTP_PROXY 死代理 | bash sandbox | `127.0.0.1:7890` 不通 | 命令前 `env -u HTTPS_PROXY -u HTTP_PROXY` |

---

## 12. 端到端验证步骤（Spec 锁定的最后一项）

```bash
# 0. 准备：本机有 JDK 8 / 17 / 25 + Maven 3.9.10（mvn.cmd），已 unset 沙箱代理
env -u HTTPS_PROXY -u HTTP_PROXY

# 1. 编译 core（JDK 8 source）
cd cryptunnel-client/cryptunnel-core
mvn clean install -DskipTests
# 期望：BUILD SUCCESS，生成 target/cryptunnel-core-1.0.0.jar

# 2. 跨语言加密互通（用 JDK 17 跑）
java -cp target/cryptunnel-core-1.0.0.jar io.github.objectyan.cryptunnel.core.crypto.AesParityMain
# 期望：明文 "hello jdbc proxy" 经 core.encrypt → 与 .NET AesTunnel.Encrypt 输出字节级一致

# 3. 编译 starter（boot25 profile）
cd ../cryptunnel-spring-boot-starter
mvn clean install -Pboot25
# 期望：BUILD SUCCESS，target/cryptunnel-starter-javax-1.0.0.jar

# 4. 跑 spring.factories 解析检查
unzip -p target/cryptunnel-starter-javax-1.0.0.jar META-INF/spring.factories
# 期望看到：org.springframework.boot.autoconfigure.EnableAutoConfiguration=\
#   io.github.objectyan.cryptunnel.starter.CryptunnelAutoConfiguration

# 5. sunrise 接入回归（仅做编译验证；sunrise 当前在另一仓库 D:\Sunrise\Coding\CRM\sunrise）
cd D:/Sunrise/Coding/CRM/sunrise
# 删除本地 src/main/java/com/crm/sunrise/jdbcproxy/
rm -rf src/main/java/com/crm/sunrise/jdbcproxy
# pom 加 starter 依赖，application-dev.yml 改 targets 形态
mvn -P dev clean compile
# 期望：BUILD SUCCESS，CryptunnelAutoConfiguration 类被 spring-boot-maven-plugin 重打包进 jar
```

---

## 13. 变更记录

| 日期 | 变更内容 | 原因 | 影响范围 |
|------|----------|------|----------|
| 2026-09-08 | 初版 | 用户决策：放 cryptunnel-client 下、双 baseline 三 profile、per-target 密钥、本地 install + Central | 全模块 |
| 2026-09-08 | 三 profile → 两 profile | boot3x 与 boot4x 编译产物字节级一致，没必要贴两个版本号；统一为 jakarta profile 一份 jar 兼容 Boot 3.x/4.x | 父 pom / 子模块 pom / overview / central-publish |
