# 改名映射表：jdbc-proxy → cryptunnel

- 生成日期：2026-09-10
- 状态：**已执行完毕（2026-09-10 晚）**
- 范围：方案 C（全量换、不留兼容，客户端与服务端同时升级）
- 相关：ADR-0004（跨平台选型）

> ⚠ **本文中的旧名是替换规则的左侧，全部有意保留，切勿对本文执行改名脚本。**

## 执行结果（与下方盘点数字的差异说明）

| 批次 | 文件 | 命中 | 保留 | 实际替换 |
|---|---|---|---|---|
| 标识符主批 | 154 | 832 | 10 | 822 |
| `core/target/` 包补漏 | 7 | 9 | 0 | 9 |
| 显示名变体（带空格的 `JDBC Proxy` 等） | 21 | 57 | 0 | 57 |
| **合计** | **182** | **898** | **10** | **888** |

与下方盘点的 1118 处 / 205 文件不一致，原因有三，均非遗漏：

1. **盘点在结构重构之前**。此后已废弃并删除历史遗留的 Java CLI（`src/main/java/`）与从未发版的 v2.0 .NET 目录树，
   这两处的旧名随代码一并消失，无需替换。
2. **本表与 `groupid-migration-overview.md`、`CHANGELOG.md`、`GLOSSARY.md` 等对照类文档不参与批量替换**，
   它们的旧名是规则左侧或历史事实。
3. **有意保留 10 处**：其中 `docs/spec.md:230,231` 与 `docs/sunrise-integration.md:110` 是需要**真实执行**的
   `rm -rf .../com/crm/sunrise/jdbcproxy` 清理命令 —— 若一并替换，路径将指向不存在的目录，
   而 `rm -rf` 删不到东西**不会报错**，会造成「以为清理完了、实际旧类还在」的假成功。

**验证**：两条 Maven profile 均 BUILD SUCCESS、`cryptunnel-core` 单元测试 79 项全过（0 失败 0 跳过）、
文件名与 public 类名不匹配 0 处、package 声明与目录层级不一致 0 处、.NET `FramingVerify` 134 项通过 exit 0。

## 盘点总览

排除 `bin/` `obj/` `target/` `.git/` `.workbuddy/` 后的**真实**数字（区分大小写，避免 `grep -i` 把三种写法算成同一批）：

| 标识符写法 | 出现次数 |
|-----------|---------|
| `JdbcProxy`（Pascal） | **592** |
| `jdbc-proxy`（kebab） | **306** |
| `jdbcproxy`（全小写） | **220** |
| `JDBCProxy` | 0（不存在，不必处理） |
| **合计** | **1118 处** |

> 注：上表**漏了带空格的显示名**（`JDBC Proxy`、`JDBC 代理` 等 58 处）。
> 三条标识符规则完全匹配不到它们，导致首轮替换后「代码全改完、用户看到的仍是旧品牌」，
> 需另用一套显示名规则补替。盘点时容易漏这一类。

受影响文件 **205 个**：

| 类型 | 数量 | 说明 |
|------|------|------|
| `.cs` | 76 | 两棵 .NET 树 + 验证工程 |
| `.java` | 42 | core + starter 三模块 + 老客户端 |
| `.md` | 40 | 文档 |
| `.xml` | 9 | pom.xml 等 |
| `.xaml` | 8 | WPF 界面 |
| `.csproj` | 6 | 项目文件 |
| `.yaml` / `.yml` | 6 | 配置样例 |
| `.html` | 4 | |
| `.imports` / `.factories` | 6 | Spring 自动装配注册 |
| 其他 | 8 | `.json` `.wxs` `.slnx` `.sh` `.py` `.ps1` `.cfg` |

> 上一轮盘点报的 209 文件 / 1724 处含 `bin/obj` 构建产物。本表是排除后的可改数字。

## 标识符映射

### 命名约定

| 原文写法 | 具体模式 | 改后 | 例子 |
|----------|---------|------|------|
| Pascal | `JdbcProxy` | `Cryptunnel` | `JdbcProxy.Core` → `Cryptunnel.Core` |
| kebab | `jdbc-proxy` | `cryptunnel` | `/jdbc-proxy/connect` → `/cryptunnel/connect` |
| 全小写 | `jdbcproxy` | `cryptunnel` | `io.github.objectyan.jdbcproxy` → `io.github.objectyan.cryptunnel` |

### Java 端（42 个 `.java`）

#### 包名（目录重命名 + 文件内 import/package 替换）

```
io.github.objectyan.jdbcproxy          → io.github.objectyan.cryptunnel
io.github.objectyan.jdbcproxy.core     → io.github.objectyan.cryptunnel.core
io.github.objectyan.jdbcproxy.core.crypto → io.github.objectyan.cryptunnel.core.crypto
io.github.objectyan.jdbcproxy.core.tunnel → io.github.objectyan.cryptunnel.core.tunnel
io.github.objectyan.jdbcproxy.starter  → io.github.objectyan.cryptunnel.starter
```

#### 类名（按模块）

| 旧名 | 新名 | 位置 |
|------|------|------|
| `JdbcProxyClient` | `CryptunnelClient` | `jdbc-proxy-core/.../proxy/JdbcProxyClient.java` |
| `JdbcProxyAutoConfiguration` | `CryptunnelAutoConfiguration` | `jdbc-proxy-starter-common/.../jdbcproxy/starter/` |
| `JdbcProxyProperties` | `CryptunnelProperties` | 同上 |
| `JdbcProxyHttpController` | `CryptunnelHttpController` | `jdbc-proxy-starter-javax/` 与 `jakarta/` 各一份 |
| `JdbcProxyWebSocketHandler` | `CryptunnelWebSocketHandler` | 同上，javax + jakarta |
| `JdbcProxyWebSocketConfigurer` | `CryptunnelWebSocketConfigurer` | 同上 |
| `JdbcProxyStarterJavaxAutoConfiguration` | `CryptunnelStarterJavaxAutoConfiguration` | `jdbc-proxy-starter-javax/` |
| `JdbcProxyStarterJakartaAutoConfiguration` | `CryptunnelStarterJakartaAutoConfiguration` | `jdbc-proxy-starter-jakarta/` |

#### sunrise 端（一个 `.java`）

| 旧名 | 新名 |
|------|------|
| `JdbcProxyConfig` | `CryptunnelConfig` |
| 包 `...jdbcproxy` | `...cryptunnel` |

> sunrise 端还有一个 `TunnelCrypto` 类，名字不含 `jdbc-proxy`，**无需改名**。

### .NET 端（76 个 `.cs`）

#### 命名空间

```
JdbcProxy.Core.*       → Cryptunnel.Core.*
JdbcProxy.App.*        → Cryptunnel.App.*
JdbcProxy.Core         → Cryptunnel.Core
JdbcProxy.App          → Cryptunnel.App
```

#### 项目名

| 旧 | 新 |
|----|----|
| `JdbcProxy.App` | `Cryptunnel.App` |
| `JdbcProxy.Core` | `Cryptunnel.Core` |
| `JdbcProxy`（v2.0，`dotnet/src/`） | `Cryptunnel` |

#### 类名（核心类型）

| 旧 | 新 |
|----|----|
| `JdbcProxyProtocolException` | `CryptunnelProtocolException` |
| `ProtocolJdbcProxyException` | `ProtocolCryptunnelException`（如存在） |

#### 事件枚举 / 配置键中的字面字符串

（这些在 `.cs` 内出现，不是类名）

| 旧值 | 新值 | 出现于 |
|------|------|--------|
| `"JdbcProxy"`（TunnelState 消息前缀） | `"Cryptunnel"` | 多处 |
| `"JdbcProxy 隧道客户端"` | `"Cryptunnel 隧道客户端"` | `AppTray.cs` |
| `defaultWsPath = "/ws-jdbc-proxy"` | `"/ws-cryptunnel"` | `AppController.cs` |
| `+ "/jdbc-proxy/connect"` | `+ "/cryptunnel/connect"` | `Tunnel.cs:469` |
| `+ "/jdbc-proxy/tunnel"` | `+ "/cryptunnel/tunnel"` | `Tunnel.cs:501` |
| `+ "/jdbc-proxy/disconnect"` | `+ "/cryptunnel/disconnect"` | `Tunnel.cs:514` |

#### v2.0 树（`dotnet/src/JdbcProxy/`）

| 旧 | 新 |
|----|----|
| `BuiltinDefaults.WsPath = "/ws-jdbc-proxy"` | `"/ws-cryptunnel"` |
| `ProjectConfig.ConnectEndpoint`（拼接 `/jdbc-proxy/connect`） | `/cryptunnel/connect` |
| `ProjectConfig.TunnelEndpoint` | `/cryptunnel/tunnel` |
| `ProjectConfig.DisconnectEndpoint` | `/cryptunnel/disconnect` |

## 线级契约（⚠️ 最高风险区）

这三项改了以后，**旧客户端连不上新服务端，新客户端也连不上旧服务端**。方案 C 明确接受这一点，但必须知道代价在哪。

### ① WebSocket 路径 —— 成本低（已可配置）

| | |
|---|---|
| 旧 | `/ws-jdbc-proxy` |
| 新 | `/ws-cryptunnel` |
| 可配置性 | ✅ **已经是配置项** |

出现位置（12 处）：

| 文件 | 行 | 性质 |
|------|-----|------|
| `jdbc-proxy-starter-common/.../JdbcProxyProperties.java` | 23 | 服务端默认值 |
| `config.d/00-defaults.yaml` | 24 | 客户端默认配置 |
| `dotnet/config-samples/00-defaults.yaml` | 24 | v2.0 默认配置 |
| `dotnet/config-samples/crm.yaml` | 37 | 注释示例 |
| `dotnet/src/JdbcProxy/Config/BuiltinDefaults.cs` | 10 | v2.0 内置常量 |
| `dotnet/src/JdbcProxy/Models/ProjectConfig.cs` | 55 | v2.0 默认值 |
| `src/JdbcProxy.App/AppController.cs` | 363 | v1.2.12 内置常量 |
| `src/JdbcProxy.App/ProjectEditorWindow.xaml` | 376 | **UI 输入框默认值** |
| `build/verify/TunnelVerify/FakeServer.cs` | 60 | 测试假服务端 |
| `CONTRIBUTING.md` | 57 | 文档 |
| `README.md` | 76 | 文档 |
| `dotnet/docs/tunnel-runtime-contract.md` | 22 | 文档 |

**因为已经可配置，现存部署可以先改 yml 指向新路径，不必与代码升级严格同步。** 这是当初留可配置性的红利。

### ② HTTP 降级端点 —— 成本高（三端硬编码，不可配）

| | |
|---|---|
| 旧 | `/jdbc-proxy/connect` `/jdbc-proxy/tunnel` `/jdbc-proxy/disconnect` |
| 新 | `/cryptunnel/connect` `/cryptunnel/tunnel` `/cryptunnel/disconnect` |
| 可配置性 | ❌ **硬编码，无配置项** |

出现位置：

| 文件 | 行 | 性质 |
|------|-----|------|
| `jdbc-proxy-starter-javax/.../JdbcProxyHttpController.java` | 51 | `@RequestMapping("/jdbc-proxy")` **服务端路由** |
| `jdbc-proxy-starter-jakarta/.../JdbcProxyHttpController.java` | 51 | 同上，另一份 |
| 上述两文件 | 43-45 | Javadoc 注释中的端点说明 |
| `src/JdbcProxy.Core/Tunnel/Tunnel.cs` | 469, 501, 514 | **v1.2.12 客户端**三处拼接 |
| `dotnet/src/JdbcProxy/Models/ProjectConfig.cs` | 118-120 | **v2.0 客户端**三个属性 |
| `CONTRIBUTING.md` | 58 | 文档 |

**这是方案 C 最贵的一项。** 由于不可配置，服务端与客户端必须**同时升级**，否则 HTTP 降级通道直接失效。

> 顺带提一个可选改进：这次改动时可以把端点前缀提为配置项（如 `cryptunnel.http-path`），这样将来再改名就不必动代码。但这会**扩大本次改动范围**，建议作为独立任务，不混在改名里做。

> ✅ **已采纳（2026-09-10 晚）**：客户端侧已实现为配置项 `httpBasePath`（默认 `/cryptunnel`），
> 三个端点由它拼出，并做归一化（补前导斜杠、去尾部斜杠）以避免拼出 `serverUrlcryptunnel/connect`
> 或 `/cryptunnel//connect` 这类**只表现为 404、报错完全看不出是配置写法问题**的地址。
> 新增 16 项断言覆盖，并做过定点变异验证（移除归一化逻辑后精准 4 项失败、其余不受影响）。
>
> 因此上表「必须同时升级」的结论**对客户端一侧已不再成立**：填
> `httpBasePath: /jdbc-proxy` + `wsPath: /ws-jdbc-proxy` 即可对接尚未更名的服务端，无需改代码。
> 服务端侧的 `@RequestMapping` 仍是注解常量（本轮按决策未改 sunrise），故服务端更名时仍需发版。

### ③ Spring 配置键前缀 —— 成本中

| | |
|---|---|
| 旧 | `jdbc-proxy.*` |
| 新 | `cryptunnel.*` |

出现位置：

| 文件 | 行 | 内容 |
|------|-----|------|
| `jdbc-proxy-starter-common/.../JdbcProxyProperties.java` | 19 | `@ConfigurationProperties(prefix = "jdbc-proxy")` |
| `jdbc-proxy-starter-common/.../JdbcProxyAutoConfiguration.java` | 26 | `@ConditionalOnProperty(name = "jdbc-proxy.enabled", ...)` |
| 同上 | 48 | 异常信息文本 `"jdbc-proxy.cipher 配置无效: "` |
| **sunrise** `.../jdbcproxy/JdbcProxyConfig.java` | 15, 21 | 注释 + `@ConditionalOnProperty` |
| **sunrise** `application-dev.yml` | 157 | `jdbc-proxy:` 配置块 |
| **sunrise** `application-prod.yml` | 203 | 同上 |

**⚠️ 已部署的 dev / prod 环境 yml 必须同步改**，否则 `jdbc-proxy.enabled` 失效 → AutoConfiguration 不装配 → 隧道功能整个消失，且**不会报错**（`matchIfMissing = false` 时静默跳过）。这是最容易在上线时炸的一项。

## 目录重命名（11 个，排除构建产物与 IDE 缓存）

```
jdbc-proxy-core                                         → cryptunnel-core
jdbc-proxy-core/src/main/java/io/github/objectyan/jdbcproxy   → .../cryptunnel
jdbc-proxy-core/src/test/java/io/github/objectyan/jdbcproxy   → .../cryptunnel

jdbc-proxy-spring-boot-starter                          → cryptunnel-spring-boot-starter
jdbc-proxy-spring-boot-starter/jdbc-proxy-starter-common   → .../cryptunnel-starter-common
jdbc-proxy-spring-boot-starter/jdbc-proxy-starter-javax    → .../cryptunnel-starter-javax
jdbc-proxy-spring-boot-starter/jdbc-proxy-starter-jakarta  → .../cryptunnel-starter-jakarta
  （三个 starter 各自的 src/main/java/io/github/objectyan/jdbcproxy → .../cryptunnel）

src/JdbcProxy.App                                       → src/Cryptunnel.App
src/JdbcProxy.Core                                      → src/Cryptunnel.Core
dotnet/src/JdbcProxy                                    → dotnet/src/Cryptunnel
```

**IDE 缓存目录直接删不改**：`src/.vs/JdbcProxy`、`src/.vs/JdbcProxy.slnx`、`dotnet/src/JdbcProxy/.vs/*`。

## 文件重命名（16 个源文件）

### Java（13 个）

```
jdbc-proxy-starter-common/.../starter/JdbcProxyAutoConfiguration.java
  → CryptunnelAutoConfiguration.java
jdbc-proxy-starter-common/.../starter/JdbcProxyProperties.java
  → CryptunnelProperties.java

jdbc-proxy-starter-javax/.../starter/JdbcProxyHttpController.java
  → CryptunnelHttpController.java
jdbc-proxy-starter-javax/.../starter/JdbcProxyStarterJavaxAutoConfiguration.java
  → CryptunnelStarterJavaxAutoConfiguration.java
jdbc-proxy-starter-javax/.../starter/JdbcProxyWebSocketConfigurer.java
  → CryptunnelWebSocketConfigurer.java
jdbc-proxy-starter-javax/.../starter/JdbcProxyWebSocketHandler.java
  → CryptunnelWebSocketHandler.java

jdbc-proxy-starter-jakarta/.../starter/JdbcProxyHttpController.java
  → CryptunnelHttpController.java
jdbc-proxy-starter-jakarta/.../starter/JdbcProxyStarterJakartaAutoConfiguration.java
  → CryptunnelStarterJakartaAutoConfiguration.java
jdbc-proxy-starter-jakarta/.../starter/JdbcProxyWebSocketConfigurer.java
  → CryptunnelWebSocketConfigurer.java
jdbc-proxy-starter-jakarta/.../starter/JdbcProxyWebSocketHandler.java
  → CryptunnelWebSocketHandler.java

src/main/java/io/github/objectyan/proxy/JdbcProxyClient.java
  → CryptunnelClient.java
```

### sunrise 仓（1 个）

```
D:\Sunrise\Coding\CRM\sunrise\src\main\java\com\crm\sunrise\jdbcproxy\JdbcProxyConfig.java
  → .../cryptunnel/CryptunnelConfig.java
```

### .NET（4 个）

```
src/JdbcProxy.App/JdbcProxy.App.csproj   → src/Cryptunnel.App/Cryptunnel.App.csproj
src/JdbcProxy.Core/JdbcProxy.Core.csproj → src/Cryptunnel.Core/Cryptunnel.Core.csproj
src/JdbcProxy.slnx                        → src/Cryptunnel.slnx
dotnet/src/JdbcProxy/JdbcProxy.csproj    → dotnet/src/Cryptunnel/Cryptunnel.csproj
```

## 跨仓库影响（⚠️ 容易漏）

改名**不止本仓库**。服务端在另一个仓库，两边必须一起改：

> ⚠ **本节尚未执行**。经决策，本轮改名的边界是**只改本仓库**，sunrise 一个字未动（仅读取核实）。
> 因此下表是 sunrise 侧的**待办清单**，不是已完成记录。
>
> 相关现状（2026-09-10 实测）：sunrise 中原先内嵌的三算法实现已被撤销，
> 服务端目前**只支持默认的 CBC 一种算法**；内嵌的 `crypto/` 目录已空，测试目录亦为空。
> 后续方向是让 sunrise **改为依赖 `cryptunnel-starter-javax`（Boot 2.5.3 + JDK 8，故非 jakarta）**，
> 删除本地内嵌类，而不是继续维护第二份实现——两份实现必然长期漂移。
>
> 一个已知的连带项：本仓库 `FramingVerify` 中有 1 项断言需读取 sunrise 侧的 WebSocket handler
> 源码做覆盖完整性比对，因 sunrise 未更名而处于**跳过**状态（并非通过）。服务端迁移后该项应转为通过。

| 仓库 | 路径 | 受影响内容 |
|------|------|-----------|
| **jdbc-proxy-client**（本仓） | — | 205 个文件 / 1118 处 |
| **sunrise** | `D:\Sunrise\Coding\CRM\sunrise\src\main\java\com\crm\sunrise\jdbcproxy\` | 5 个 `.java`（其中 `JdbcProxyConfig.java` 要改名）+ 包目录 |
| **sunrise** | `src/main/resources/application-dev.yml:157` | `jdbc-proxy:` 配置块 |
| **sunrise** | `src/main/resources/application-prod.yml:203` | 同上 |

### Maven 坐标

| 项 | 旧 | 新 |
|----|----|----|
| groupId | `io.github.objectyan` | **不变** |
| artifactId（core） | `jdbc-proxy-core` | `cryptunnel-core` |
| artifactId（starter） | `jdbc-proxy-spring-boot-starter` | `cryptunnel-spring-boot-starter` |
| artifactId（javax） | `jdbc-proxy-starter-javax` | `cryptunnel-starter-javax` |
| artifactId（jakarta） | `jdbc-proxy-starter-jakarta` | `cryptunnel-starter-jakarta` |

> **坐标未发布到 Maven Central**，所以改 artifactId 不会破坏外部使用者。
> 让 sunrise 改为 Maven 依赖（而非内嵌源码）是长期待办，应在改名之后、用新坐标做。
> 注意：当初在 sunrise 内嵌实现的理由是「避免绑定尚未发布的坐标」，但这个理由**站不住**——
> 四个 artifact 早已 `install` 到本地 `~/.m2/io/github/objectyan/`，本地依赖完全可用。
> 为绕开一点小麻烦而造出长期的双实现漂移，代价远高于收益。

### Spring 自动装配注册文件（6 个）

`spring.factories`（Boot 2.x）与 `AutoConfiguration.imports`（Boot 3.x）里写的是**全限定类名**，改包名和类名后必须同步：

```
io.github.objectyan.jdbcproxy.starter.JdbcProxyStarterJavaxAutoConfiguration
  → io.github.objectyan.cryptunnel.starter.CryptunnelStarterJavaxAutoConfiguration
```

**这两个文件改错不会编译报错，只会在运行时静默不装配** —— 与配置键前缀是同一类陷阱。

## 直接删除，不改名（构建产物）

这些是编译输出，改名毫无意义，应在改名前删掉以免污染 grep 结果：

```
JdbcProxy/                                  ← 整个目录（打包输出）
  JdbcProxy.exe
  app/JdbcProxy.cfg
  app/jdbc-proxy-client.jar
packaging/dist/                             ← 整个目录
  JdbcProxy-portable-1.2.12-self.zip
  JdbcProxy-portable-win-x64-self.zip
  JdbcProxy-portable-win-x64.zip
  JdbcProxy-setup-1.2.12-self.msi
  JdbcProxy-setup-1.2.12-self.wixpdb
  JdbcProxy-setup-win-x64-self.msi
  JdbcProxy-setup-win-x64-self.wixpdb
  JdbcProxy-setup-win-x64.msi
  JdbcProxy-setup-win-x64.wixpdb
  self-contained-1.2.12/JdbcProxy.Core.pdb
  self-contained-1.2.12/JdbcProxy.exe
  staging-portable-1.2.12/JdbcProxy.exe
  staging-portable/JdbcProxy.exe
src/.vs/                                    ← IDE 缓存
dotnet/src/JdbcProxy/.vs/                   ← IDE 缓存
```

> ⚠️ `packaging/dist/` 里是**已发布的 v1.2.12 安装包**。删之前确认是否需要留档 —— 如果这是唯一副本，先备份到仓库外。

## 保留不动（有意为之）

| 内容 | 位置 | 理由 |
|------|------|------|
| `com.crm.sunrise` 8 处 | `docs/adr/0001`、`GLOSSARY.md:6`、`spec.md:230-231`、`sunrise-integration.md:17,110,113` | 记录集成关系的历史文档，**不是待清理的残留** |
| `TunnelCipher` / `TunnelCiphers` / `TunnelCrypto` | core + sunrise | 名字不含 `jdbc-proxy`，语义正确 |
| `AesUtil` | sunrise | **兼容性基准，不可改动** |
| `groupId io.github.objectyan` | 所有 pom | 与项目名无关 |
| `X-Conn-Id` 头 | HTTP 降级 | 不含产品名 |
| `AUTH:{authKey}:{ts}:{nonce}` 报文格式 | 认证 | 不含产品名 |

## 执行顺序（建议）

每一步做完就验证，不要攒到最后。

| # | 步骤 | 验证方式 |
|---|------|---------|
| 0 | 备份 `packaging/dist/` 到仓库外，然后删构建产物与 `.vs/` | `find` 复核已清空 |
| 1 | **决定两棵 .NET 树的去留**（见下） | — |
| 2 | Java：目录改名（包路径 + 模块目录） | 目录树核对 |
| 3 | Java：文件内 `package` / `import` 替换 | `JAVA_HOME=C:\Users\OY\.jdks\corretto-1.8.0_504 mvn.cmd -o compile` |
| 4 | Java：类名 + 文件名改 | 同上编译 |
| 5 | Java：`spring.factories` / `AutoConfiguration.imports` 全限定名 | **grep 逐行核对**（编译不会报错） |
| 6 | Java：配置键前缀 `jdbc-proxy.` → `cryptunnel.` | 同上 |
| 7 | **sunrise 仓**：包目录 + 类名 + 两个 yml 配置块 | `mvn.cmd -o compile` |
| 8 | .NET：目录 + csproj + slnx 改名 | `dotnet build` |
| 9 | .NET：命名空间 + 类名替换 | 同上 |
| 10 | .NET：线级契约字面量（WS 路径、HTTP 三端点） | 与第 6 步的服务端值**逐字比对** |
| 11 | 验证工程：`build/verify/FramingVerify`（130 断言）+ `TunnelVerify` | 退出码 0 |
| 12 | 跨语言互通：`dotnet/build/parity/run.sh` | 退出码 0 |
| 13 | 文档 40 个 `.md` | 人工过一遍，注意保留「保留不动」清单里的内容 |

### 第 1 步的前置决策：两棵 .NET 树

| | `src/JdbcProxy.{App,Core}` | `dotnet/src/JdbcProxy` |
|---|---|---|
| 版本 | **v1.2.12（现役）** | v2.0.0 |
| 框架 | net10.0 | net8.0 |
| .cs 数 | 37 | 24 |
| 最后修改 | 09-10 13:41 | 09-09 21:11 |

**若两棵都改名，等于把双实现漂移固化下来。** 建议在改名前决定：

- **选项 A**：只改 `src/`（现役），`dotnet/` 原地封存或删除 → 工作量减少约 24 个 `.cs`
- **选项 B**：两棵都改 → 保留 v2.0 作为 Avalonia 迁移的参考实现
- **选项 C**：先把 v2.0 有价值的部分（`CipherRegistry` / `ITunnelCipher` 设计）合入 `src/`，再删 `dotnet/`

ADR-0004 选定 Avalonia + net10.0，**v2.0 的 net8.0 分支价值下降**，但其隧道层设计比 v1.2.12 更现代。这个决策需要单独确认。

> ✅ **已决策并执行**：取**选项 A 的彻底版**——现役树迁至 `dotnet/src/Cryptunnel.{App,Core}`，
> 从未发版的 v2.0 树（`dotnet/src/JdbcProxy/`）与历史遗留的老 Java CLI（`src/main/java/`）**一并删除**。
> 双实现漂移就此消除，不再有「改了一棵忘了另一棵」的风险。

## 验证清单（改完必须全绿）

> 下表已按 **2026-09-10 晚实际执行结果**校正。原表有两处失真，均已修正：
> 其一要求 sunrise 侧 `TunnelCipherParityTest` 达到 23/23，**而该测试并不存在**（sunrise 的内嵌三算法实现已被撤销，
> 测试目录为空）——照此清单验收会卡在一个永远无法满足的条件上；
> 其二断言数与残留处数已随本轮改动变化。

| 项 | 命令 | 期望 | 实测 |
|----|------|------|------|
| Java 全模块（javax/JDK8） | `cd java && JAVA_HOME="…corretto-1.8.0_504" mvn.cmd -Pboot25 clean test` | BUILD SUCCESS | ✅ 4 模块 SUCCESS |
| Java 全模块（jakarta/JDK17） | `cd java && JAVA_HOME="…graalvm-ce-17.0.9" mvn.cmd -Pjakarta clean test` | BUILD SUCCESS | ✅ 4 模块 SUCCESS |
| core 单元测试（JDK8） | `… mvn.cmd -Pboot25 test` | 全过，0 跳过 | ✅ **79 项，0 失败 0 错误 0 跳过** |
| core 单元测试（JDK17） | `… mvn.cmd -Pjakarta test` | 与 JDK8 同数 | ✅ **79 项**，两 profile 完全一致 |
| **发布产物完整性** | `mvn.cmd -P{boot25,jakarta} -DskipTests package` | 每模块 3 个 jar | ✅ **9 个 jar**（main/sources/javadoc × 3 模块），两 profile 均通 |
| 文件名 == public 类名 | 脚本校验 | 0 不匹配 | ✅ 0 |
| package 声明 == 目录层级 | 脚本校验 | 0 不一致 | ✅ 0（46 个 `.java`） |
| .NET 编译 | `Cryptunnel.Core` / `Cryptunnel.App` | 0 警告 0 错误 | ✅ 各 0 / 0 |
| .NET 帧与配置验证 | `FramingVerify.dll` | exit 0 | ✅ **146 项通过 / 0 失败 / 0 跳过** |
| .NET 隧道验证 | `TunnelVerify.dll` | exit 0 | ✅ **39 项通过 / 0 失败** |
| .NET UI 验证 | `UiVerify.dll` | exit 0 | ✅ **25 项通过 / 0 失败** |
| 跨语言互通 | `bash dotnet/build/parity/run.sh` | exit 0（52 + 3 断言） | ✅ **52 + 3 项，双向对齐** |
| **改名幂等性** | `python docs/.rename-idempotence-check.py` | exit 0 | ✅ 133 文件，残留全在豁免名单且处数吻合 |
| **PROTECT 规则自检** | `python docs/.rename-protect-selftest.py` | exit 0 | ✅ **11 项**（含 3 项变异验证） |
| **残留检查** | 全仓扫描（排除 `.git` `bin` `obj` `target` `packaging`） | 只剩有意保留项 | ✅ **10 处**，逐条核对 |
| **AutoConfig 装配** | 启动 sunrise，确认 WS 与 HTTP 两通道都注册 | 两条通道都通 | ⏳ 待 sunrise 迁移后做 |

### 本轮跑验证时揪出的三个真问题

清单从「⏳ 未重跑」补到实测值的过程中，暴露了三个**只跑一遍就不会发现**的缺陷：

**① `FramingVerify` 的 close reason 比对从 PASS 静默退化成 SKIP（丢 12 项断言）**
改名脚本把 `CloseReasonChecks.cs` 里指向 **sunrise 仓库**的探测路径也替换了：
`…\com\crm\sunrise\jdbcproxy\JdbcProxyWebSocketHandler.java` → `…\cryptunnel\CryptunnelWebSocketHandler.java`。
但 sunrise 从未更名，于是文件找不到 → 打印一行「未找到」→ 跳过，**退出码仍是 0**。
输出看起来还很像"服务端不在本机"这种正常情况。
根因是 `rename.py` 的 PROTECT 只覆盖了点号与正斜杠形态，**漏了 Windows 反斜杠形态**（C# 逐字字符串用的就是它）——
而脚本自己的注释 #2 正是在讲"必须同时覆盖点号和路径分隔符形态"，当时只补了一半。
修法：探测路径改为**同列新旧两个文件名**（迁移前后都能验，迁移当天不用改代码）；
PROTECT 补齐反斜杠形态与服务端类名。修完 **134/1skip → 146/0skip**，服务端 11 条 reason 全部命中专属映射。

**② `parity/run.sh` 第 3 步依赖一个永远不会再产生的 jar**
它引用 `$REPO/target/cryptunnel-client.jar`（老 Java CLI 的 fat jar）。结构重构把老 CLI 整棵树连根 pom 一并删除后，
该 jar 永久消失，脚本固定报「找不到 …，请先跑 mvn package」并 exit 2 ——
提示语指向一个**照做也修不好**的动作。改为直接用 `cryptunnel-core` 的 jar + BouncyCastle。
附带发现：BC 版本原先用 `ls | tail -1` 挑，本机 `~/.m2` 下有 1.76/1.78.1/1.79/1.80/1.83/1.84 六个版本，
实际挑到 **1.84**，而 pom 声明 **1.78.1** —— "跨语言一致性已验证"用的并不是发布时真正依赖的那份 BC。
已改为从 `java/pom.xml` 解析版本号。

**③ `mvn package` 因 javadoc 失败，Maven Central 根本发不出去**
`cryptunnel-core/pom.xml` 的 `maven.compiler.release=8` 会被 javadoc 插件读取并原样透传 release 标记，
而 **JDK 8 自带的 javadoc 不认识它**（JDK 9+ 才有），直接报「无效的标记」。
Maven 只转述一句 `Exit code: 1`，真实原因要手动跑 `javadoc @options @packages` 才看得到。
危险在于**日常 `mvn test` 一路绿灯**，这个坑只在 `package` 及之后暴露，很容易拖到发版当天才炸。
修法：javadoc 插件侧显式 `<source>8</source>` + `<release combine.self="override"/>`，
不动 core 的 `maven.compiler.release`（那是"JDK 17 编译也不许误用 JDK 9+ API"的闸门，撤掉会失去编译期保护）。
修完两个 profile 各产出 9 个 jar（main/sources/javadoc × 3 模块）。

**残留 10 处**均为有意保留的历史事实，其中 `docs/spec.md:230,231` 与 `docs/sunrise-integration.md:110`
是需要**真实执行**的 `rm -rf .../com/crm/sunrise/jdbcproxy` 清理命令：若一并替换，路径会指向不存在的目录，
而 `rm -rf` 删不到东西**不报错**，将造成「以为清理干净、实际旧类仍在」的假成功，直到编译期才暴露。

### 最后一项最容易被跳过

`jdbc-proxy.enabled` 改成 `cryptunnel.enabled` 后，如果 yml 忘了改，`@ConditionalOnProperty(matchIfMissing = false)` 会**静默跳过装配** —— 编译通过、启动无异常、但隧道端点不存在。必须实际启动并验证两条通道，不能只看编译。

同类陷阱还有一个：`spring.factories` 与 `AutoConfiguration.imports` 里写的是**全限定类名字符串**，
改错**不会有编译错误**，只在运行时表现为「功能整个消失」。改名涉及这两个文件时必须实际启动验证。
