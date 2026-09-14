# 变更日志

本项目遵循 [Semantic Versioning](https://semver.org/lang/zh-CN/)。

## [Unreleased]

### 新增

- **可插拔隧道加密算法**（ADR-0003 方案 B），新增 `TunnelCipher` SPI：`id()` / `aliases()` / `seal()` / `open()`，配套 `TunnelCiphers` 注册表与非受检的 `TunnelCryptoException`
- 三种算法实现，服务端通过 `cryptunnel.cipher` 选择，客户端通过第 6 个命令行参数选择：

  | 标识 | 别名 | 帧结构 | 载荷长度* | 依赖 |
  |---|---|---|---|---|
  | `aes-256-cbc-hmac-sha256`（默认） | `aes-cbc` | `Base64(IV[16] ‖ HMAC[32] ‖ 密文)` | 112 B | JDK 8 原生 |
  | `aes-256-gcm` | `aes-gcm` | `Base64(nonce[12] ‖ 密文 ‖ tag[16])` | 88 B | JDK 8 原生（JEP 115） |
  | `sm4-cbc-hmac-sha256` | `sm4` | `Base64(IV[16] ‖ HMAC[32] ‖ 密文)` | 112 B | BouncyCastle（optional） |

  \* 以同一 32 字节明文实测。AES-GCM 为 AEAD，无需外挂 HMAC，故比另两者短 24 字节。

- 密钥派生沿用「单一 `aesKey` 派生多把子密钥」模型，新增 SM4 子密钥：
  - AES 密钥 = `SHA-256("AES:" + aesKey)`
  - HMAC 密钥 = `SHA-256("HMAC:" + aesKey)`
  - SM4 密钥 = `SHA-256("SM4:" + aesKey)` 取前 16 字节
- .NET 客户端同步支持三算法，新增配置项 `cipher`（`defaults:` 与项目段均可设，支持别名），并在配置加载期校验未知算法、对非默认算法给出「需同步修改服务端」提示
- .NET 侧 **零依赖手写 SM4**（`Sm4Engine`，GB/T 32907-2016），已用国标附录 A.1 官方向量校验，无需引入任何 NuGet 包
- 双向跨语言对齐校验工具 `dotnet/build/parity/run.sh`：C# 自检 52 项断言 + Java 反向解开 C# 载荷 3 项断言，一键执行
- 文档：`docs/replacing-the-cipher.md` 改写为「现状 + 切换步骤 + 扩展新算法」；`docs/adr/0003-pluggable-tunnel-cipher.md` 追加落地记录

### 安全

- **跨算法强制硬失败**：不同算法之间不得互通。其中 CBC ↔ SM4 帧结构完全相同且共用同一把 HMAC 密钥，HMAC 校验会通过，仅靠 PKCS7 去填充失败拦截——已针对全部 6 种组合补充断言，确保抛出异常而非返回垃圾明文
- SM4 采用条件注册：BouncyCastle 不在 classpath 时该算法表现为「不存在」，配置期即给出可读报错，而非运行期 `NoClassDefFoundError`

### 兼容性

- **默认行为零变化**：不配置 `cipher` 时仍为 `aes-256-cbc-hmac-sha256`，且该实现直接委托原 `AesUtil`，构造上保证与既有线级格式字节等价；已部署的老客户端无需升级
- .NET 配置写回时，`cipher` 等于默认值则不落盘，老配置文件不会被改写
- 报文内**不含任何算法标识**（防 DPI 指纹），算法在配置期约定；服务端同一时刻只启用一种算法

### 测试

- `cryptunnel-core` 单元测试 38 → 74 全部通过（新增 `AesGcmCipherTest` 13 项、`Sm4CipherTest` 12 项、`TunnelCipherTest` 11 项）

### 现役 WPF 客户端安全能力移植

此前三算法与安全加固只存在于 `dotnet/`（v2.0 分支，从未发版），而实际发布的产物来自当时的 `src/`。
本轮将这些能力移植进现役代码 —— 躺在没人用的代码里的安全能力，安全价值是零。

> 注：下列路径为**当时**的目录结构。本版本随后完成了结构重构与更名（详见「重构与更名」节），
> 现役代码已迁至 `dotnet/src/Cryptunnel.Core/`，当时的老 Java CLI（`src/`）与 v2.0 树已废弃删除。

- **三算法 cipher SPI** 移植进现役客户端的 `Crypto/`（今为 `dotnet/src/Cryptunnel.Core/Crypto`），`Tunnel` 改为通过 `ITunnelCipher` 收发，
  删除硬编码的 `AesCrypto` 直接调用
- **帧上限守卫**：`TunnelFraming` 收口为唯一发送入口 `SendFrameAsync`，
  杜绝新增发送路径漏掉上限检查（超限会导致对端 WS 1009，用户侧表现为无法与网络故障区分的 `08S01`）
- **认证判定改为基于服务端首个数据帧**，替换原先「发完认证睡 `AuthResponseMs`，连接还开着就算成功」的判据。
  原实现有三个真实失效模式：close 帧晚于超时到达时会把已死连接判为成功、每条连接固定阻塞整个超时时长、
  close reason 被整个丢弃。实测认证耗时 **2000ms → 40ms**
- **close reason 诊断映射**补齐至服务端实际发出的全部 11 种（此前 9 种，且其中一条因字面量与服务端不一致而是死分支）。
  未知 reason 判为可重试，故漏映射会让本该停下让用户改配置的错误变成无休止重连
- **非回环监听告警补进 `StartOne` 路径**（此前只有 `StartAll` 有检查，而单独启动某个项目是更常用的方式）；
  回环判定改用 `IPAddress.IsLoopback` 覆盖 127.0.0.0/8 整段，`0.0.0.0` / `::` / `*` / `+` 一律判为非回环
- **⚠ 破坏性变更 · 非回环监听由「默认放行只告警」改为「默认拒绝 + 显式授权」**。
  新增配置项 `local.allowNonLoopback`（默认 `false`）。监听地址不是回环且未显式授权时，
  配置在**加载阶段**即被拒绝，项目不会启动，界面上直接列出原因与改法。

  为什么要改：这是本客户端唯一一个能让本机之外的人穿透防火墙的配置。监听 `0.0.0.0` 时
  局域网内任意机器都能连上本地端口，而隧道会用本地配置的密钥替对方完成认证 ——
  **对方不需要知道 `aesKey`，也不需要知道 `authKey`**。一行告警不足以对应这个后果，
  何况告警会淹没在启动日志里。

  为什么是两个配置项而不是「填了地址就等于同意」：填地址与承担风险是两件事，
  后果必须被显式承认一次。

  **升级影响**：老配置里写了 `0.0.0.0`（或具体网卡 IP）的用户，升级后该项目会启动失败。
  在对应项目的 `local:` 段下补一行即可恢复：

  ```yaml
  local:
    port: 3307
    address: 0.0.0.0
    allowNonLoopback: true    # 新增这一行
  ```

  没有隐式的「首次升级放行一次」宽限逻辑 —— 行为纯由配置决定，读配置即可预测结果。
  仅使用默认 `127.0.0.1` 的用户（绝大多数）不受任何影响，无需改动配置文件。

  判定逻辑收口在 `ListenAddressPolicy`（加载期闸门与运行期告警共用同一份），
  项目编辑器新增地址下拉框与授权勾选框，保存时同样拦截并给出与加载期一致的说明。
- **`cipher` 配置项贯通** YAML → `ConfigLoader`（解析期校验 + 别名归一）→ `TunnelConfig` → `Tunnel`，
  并接入项目编辑器（下拉选择 + 非默认算法的服务端同步提示）。
  未知算法在**加载阶段**即被拒，不进入运行期 —— 否则用户拿到的只是服务端一句
  `Auth decrypt failed`，而该提示与 aesKey 配错无法区分

#### 验收

| 套件 | 断言数 | 覆盖 |
|---|---|---|
| `build/verify/FramingVerify` | 146 | 帧上限、认证报文、cipher 配置、YAML 写回、close reason 对服务端源码比对、监听地址闸门、健康检查、HTTP 基础路径 |
| `build/verify/TunnelVerify` | 39 | 真实 WebSocket 端到端：认证成功/失败、帧类型、双向转发、安全告警 |
| `build/verify/UiVerify` | 25 | 项目编辑器运行时构造、下拉内容、回填归一、提示文案、监听地址与非回环授权 |
| `dotnet/build/parity` | 52 + 3 | 双向跨语言字节级对齐（改造后仍与 Java 服务端兼容） |
| `cryptunnel-core` 单元测试 | 79 | 认证编解码、target 发现、三算法、帧读取、nonce、容量核算；JDK 8 与 JDK 17 两 profile 同数 |
| `docs/.rename-protect-selftest.py` | 11 | 改名规则的历史字符串保护，含 3 项变异验证（摘掉规则后必须失败） |

> `FramingVerify` 的 close reason 覆盖比对**曾因改名连带事故退化为 SKIP**（134 项 + 1 跳过）：
> 改名脚本把指向 sunrise 仓库的探测路径一起替换了，而服务端并未更名，于是文件找不到、
> 该节静默跳过，**退出码仍是 0**。修复后探测路径同列新旧两个文件名，跳过项转为 12 项实测断言
> （服务端 11 条 reason 全部命中专属映射），总数 **134 → 146，0 跳过**。

### 隧道健康检查（探活）

- 新增三层健康探测 `Transport`（WS 握手）→ `Authentication`（authKey + aesKey + cipher 三合一）→ `Database`（收到 MySQL 握手包），**服务端零改动**。
  可达性与 MySQL 版本号是「白拿」的：服务端认证通过后不回 ACK，直接连 MySQL 并推握手包，故收到首帧即证明认证已过且服务端已连上库。
- 受此协议限制，**认证层与数据库层无法分开计时**（认证成功时刻线路上没有任何信号），认证层耗时记 0 并在代码注释说明原因——不编造会被拿去做性能判断的数字。
- 能力上限即 MySQL 握手包：再深一层需要发登录包，而账密由用户在客户端（如 DBeaver）填写，本程序并不持有。
- 配置 `health.enabled`（默认 **false**，opt-in——每次探活会真实占用服务端一个 `maxConnections` 名额）、
  `health.intervalSec`（默认 300，**下限 30 硬拒绝而非静默夹取**）。
- 报告区分 **跳过 / 通过 / 失败三态**（跳过显示 `—` 而非 `0ms`）；**仅在状态跳变时打印日志**，持续健康完全静默。
- 移除原 `TestConnection`：它只是 `TcpClient` 连本地监听端口，四种真实故障全部报成功，且会被 `AcceptLoop` 收下、真的建立一条服务端 MySQL 连接再断开。假的指示灯比没有指示灯更糟。

### 修复

- **starter 连接计数双向漂移**（javax / jakarta 双份对称）：`activeConnections` 存在多减少增路径导致计数变负，
  使 `maxConnections` 闸门**完全失效**；`connectionCounts` 只增不减导致最终**永久拒绝新连接**。
  两者的表象都是「重启就好」，掩盖了闸门早已形同虚设的事实。
  修法为**单一释放出口**：以 `Set<String> admitted` 作准入凭证，仅 `remove()` 返回 `true` 的那一次递减，
  增减配对由数据结构保证而非依赖调用路径自觉。
- starter 不再使用 `@EnableScheduling`（它是**全局开关**，会连带激活宿主应用中所有 `@Scheduled`，
  库不应产生这种副作用），改为私有 `ScheduledExecutorService` 配合 `@PostConstruct` / `@PreDestroy` 自管生命周期；
  清理任务**捕获 `Throwable`**，否则一次异常会让后续清理静默停止。
- starter 各模块 POM 补齐 `project.build.sourceEncoding` 与 `maven.compiler.encoding`（此前缺失，
  Maven 按平台默认 GBK 读取 UTF-8 源码，中文注释损坏）。
- **`mvn package` 因 javadoc 失败，Maven Central 无法发布**（发布阻塞级）：
  `cryptunnel-core` 的 `maven.compiler.release=8` 会被 javadoc 插件读取并原样透传 release 标记，
  而 **JDK 8 自带的 javadoc 不认识该标记**（JDK 9+ 才有），直接报「无效的标记」。
  Maven 仅转述一句 `Exit code: 1`，真实原因需手动执行 `javadoc @options @packages` 才可见。
  危险之处在于**日常 `mvn test` 一路绿灯** —— 该缺陷只在 `package` 及之后的阶段暴露，
  而 javadoc jar 是 Central 的强制要求，极易拖到发版当天才发现。
  修法为 javadoc 插件侧显式 `<source>8</source>` 并覆盖掉继承来的 release 配置，
  **不改动** core 的 `maven.compiler.release`（那是「用 JDK 17 编译也不许误用 JDK 9+ API」的编译期闸门）。
  修复后 `boot25` 与 `jakarta` 两个 profile 各产出 9 个 jar（main / sources / javadoc × 3 模块）。
- **跨语言校验脚本 `parity/run.sh` 的 Java 侧依赖已永久消失**：它引用
  `target/cryptunnel-client.jar`（老 Java CLI 的 fat jar），而结构重构已将老 CLI 整棵树连同其根 POM 删除。
  脚本固定报「找不到 …，请先跑 mvn package」并退出 —— 提示语指向一个**照做也无法修复**的动作。
  改为直接使用 `cryptunnel-core` 的 jar 加 BouncyCastle。
  同时修正 BC 版本选取方式：原先取本地仓库中最新的一个，本机存在 6 个版本、实际取到 1.84，
  而 POM 声明 1.78.1 —— 即「跨语言一致性已验证」用的并非发布时真正依赖的那份实现。现从 POM 解析版本号。
- **改名脚本会替换指向其它仓库的路径**：`docs/rename.py` 的 PROTECT 列表仅覆盖点号与正斜杠形态，
  漏了 Windows 反斜杠形态（C# 逐字字符串正是该形态）。后果是 `FramingVerify` 中
  close reason 覆盖比对的服务端源码探测路径被改成一个尚不存在的文件名，该节
  **从 PASS 静默退化为 SKIP、退出码仍为 0**，丢失 12 项断言而无任何报警。
  已补齐反斜杠形态与服务端类名保护，并新增 `docs/.rename-protect-selftest.py`（11 项，
  含 3 项**变异验证** —— 摘掉新补的保护规则后样本必须被改坏，否则说明测试本身是空转）
  与 `docs/.rename-idempotence-check.py`（全仓幂等核查，按文件豁免并校验处数，处数漂移即报警）。

### 重构与更名

- **产品更名为 cryptunnel**：`JdbcProxy` → `Cryptunnel`、`jdbc-proxy` → `cryptunnel`、`jdbcproxy` → `cryptunnel`，
  连带 pom `<name>`、日志前缀、文档标题等**带空格的显示名**变体，共替换 888 处 / 182 文件。
  Java 包名 `io.github.objectyan.jdbcproxy.*` → `io.github.objectyan.cryptunnel.*`。
  历史记录中的旧包名（如迁移文档中需要真实执行的清理命令）**有意保留**，不予替换。
- **项目结构重构**，便于发布到 Maven Central 与 GitHub：
  `java/`（父 POM + `cryptunnel-core` + `cryptunnel-starter-{common,javax,jakarta}`）、
  `dotnet/`（`src/Cryptunnel.{App,Core}` + `build/`）、`docs/`、`packaging/`、`.github/`。
- 废弃并移除历史遗留的 Java CLI 客户端与从未发版的 v2.0 .NET 目录树，消除长期双实现漂移。
- **HTTP 降级通道的基础路径改为可配置** `httpBasePath`（默认 `/cryptunnel`）：
  三个端点由它拼出（`/connect`、`/tunnel`、`/disconnect`）。此前两端均为硬编码，一旦更名就只能靠双端同时改代码、同时发版才能连通。
  配置值会做归一化（补前导斜杠、去尾部斜杠），避免拼出 `serverUrlcryptunnel/connect` 或 `/cryptunnel//connect` 这类**只表现为 404、报错完全看不出是配置写法问题**的地址。
  配合同样可配的 `wsPath`，客户端**无需改代码即可对接尚未更名的服务端**。

### 文档

- 修正 `docs/sunrise-integration.md` 中两处与代码实际输出不符的验收标准：其一日志文本与代码不一致，
  其二所期望的日志在代码中**根本不存在**。此类失真不会有人察觉，直到照着它验收时才暴露。

## [1.3.0] - 2026-09-09

### 变更

- **破坏性变更**：groupId 由 `com.crm.sunrise` 迁移至 `io.github.objectyan`，为发布到 Maven Central 做准备
- 包名随之迁移：
  - `com.crm.sunrise.jdbcproxy.*` → `io.github.objectyan.jdbcproxy.*`
  - `com.crm.sunrise.proxy.*`（老客户端）→ `io.github.objectyan.proxy.*`

### 迁移指引

下游应用需同步修改依赖坐标与 import：

```xml
<!-- 旧 -->
<dependency>
    <groupId>com.crm.sunrise</groupId>
    <artifactId>jdbc-proxy-starter-javax</artifactId>
</dependency>

<!-- 新 -->
<dependency>
    <groupId>io.github.objectyan</groupId>
    <artifactId>jdbc-proxy-starter-javax</artifactId>
</dependency>
```

**线级协议未做任何变更**——AUTH 报文格式、AES/HMAC 密钥派生、分片大小、WebSocket 路径、HTTP 降级端点均保持不变，已部署的客户端无需升级。

## [1.2.0] - 2026-09-08

### 新增

- 拆出 `jdbc-proxy-core`（无 Spring 依赖，JDK 8 编译目标）与 `jdbc-proxy-spring-boot-starter`
- **多数据源**：target 白名单机制，AUTH 报文追加第 5 段 `targetId` 实现路由
  - `AUTH:{authKey}:{ts}:{nonce}` — 4 段，路由到 default target（老客户端零改动）
  - `AUTH:{authKey}:{ts}:{nonce}:{targetId}` — 5 段，路由到具名 target
- `TargetDiscovery`：AUTH 报文为密文，服务端在解密前无法得知应使用哪把密钥，故逐 target 试解并以 HMAC 校验定位
- javax / jakarta 双 artifact，分别覆盖 Boot 2.x 与 Boot 3.x / 4.x

### 变更

- 三个构建 profile（`boot25` / `boot3x` / `boot4x`）合并为两个（`boot25` / `jakarta`）
  - 原因：jakarta jar 在 Boot 3.5 与 4.1 下编译产物字节级一致，无需贴两个版本号
- 老平铺 yml 配置自动包装为 `default` target 以保持兼容

## [1.1.0] - 2026-09-01

### 新增

- 桌面客户端项目表单 CRUD（新建 / 编辑 / 删除，删除走回收站）
- 开机自启动（写 `HKCU\...\Run`，免管理员权限）
- 关闭窗口即缩至托盘、隧道继续运行；仅"退出"才真正结束进程

### 变更

- 发布产物默认改为框架依赖（约 552KB），可选 `-SelfContained` 出自包含版本

## [1.0.0]

- 首个可用版本：WebSocket 主模式 + HTTP 长轮询降级，AES 加密隧道
- 确立帧大小契约：客户端分片 4096 字节，使单帧低于 Tomcat 默认 8192 上限（见 ADR-0001）
