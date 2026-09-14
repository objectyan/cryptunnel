# 项目结构重构 —— 阶段成果

> 2026-09-10 · `cryptunnel-client`
> 用户指令："全部重构，项目结构也要重新构建，便于后期发布到 maven 与 github"

---

## 一句话结论

根目录从 **23 项收敛到 11 项**，`java/` 与 `dotnet/` 两棵树彻底分开，Maven Central 发布所需的全部元数据已就位。
**全部验证项通过，无一为 SKIP。**

---

## 做了什么

### 1. 清掉的东西

| 对象 | 体积 | 性质 | 处置 |
|------|------|------|------|
| `Cryptunnel/` | 167 MB | jpackage 产物（捆的 JDK8 runtime，含 100+ 个 JDK 自带 sample） | 移出仓库 |
| `target/` | 17 MB | 根 POM 的 Maven 产物 | 移出仓库 |
| `pom.xml`（根） | 6 KB | 老 CLI 的 fat-jar POM（**非**聚合 POM） | 移出仓库 |
| `src/main/java/` | 555 行 | 老 Java 命令行客户端 | 移出仓库 |
| `dotnet/src/Cryptunnel/` | — | v2.0 重写分支，**彻底孤儿** | 移出仓库 |
| 各 `bin/ obj/ .vs/ target/` | — | 中间产物 | 直接删除（14 个目录） |

移出目标：`D:\Sunrise\Coding\CRM\_cryptunnel-client-trash-20260910`（可逆）。

> **`packaging/`（761 MB / 9 个安装包）确认无备份 → 全程未碰，已复核完好。**

### 2. 新结构

```
cryptunnel-client/
├── java/                          ← 全部 Maven 模块
│   ├── pom.xml                    ← 父 POM（含 Central 元数据 + 三 profile）
│   ├── cryptunnel-core/
│   ├── cryptunnel-starter-common/
│   ├── cryptunnel-starter-javax/
│   └── cryptunnel-starter-jakarta/
├── dotnet/                        ← 全部 .NET 代码
│   ├── Directory.Build.props      ← 两份合并为一
│   ├── Cryptunnel.slnx
│   ├── src/{Cryptunnel.App, Cryptunnel.Core}
│   └── build/
│       ├── verify/{FramingVerify, TunnelVerify, UiVerify}
│       └── parity/CryptoParity
├── docs/  packaging/  config.d/  deliverables/  design/
└── README.md  LICENSE  CHANGELOG.md  CONTRIBUTING.md
```

starter 三兄弟从嵌套目录**提到 `java/` 平级** —— 父 POM 直管 4 个模块，省掉一层聚合 POM。

### 3. 新父 POM 的要点

- **Central 强制六项**齐备：`name` / `description` / `url` / `licenses`(Apache-2.0) / `developers` / `scm`。
  缺任一项属于「构建全绿、发布才炸」，故统一放父 POM 继承。
- **`release` profile 独立拆出**：GPG 签名在无密钥的机器上必然失败，日常 `mvn test` 不该被它拖住。
- **编码写两处**：只写 `project.build.sourceEncoding` 不够，编译插件读的是 `maven.compiler.encoding`；漏配会用平台 GBK 读 UTF-8 源码，中文注释**静默损坏**且不报错。

---

## 两个险些出事的地方

### ⚠ 项目里有个叫 `target` 的 Java 包

清理 Maven 产物时，`find -name target` 命中了这两条：

```
cryptunnel-core/src/main/java/.../core/target/   ← 5 个源码文件
cryptunnel-core/src/test/java/.../core/target/   ← 2 个测试
```

这是 **Target 白名单安全模型**的核心实现（`TargetRegistry` / `DefaultTargetRegistry` / `LegacyConfigAdapter` 等）。
一条 `find -name target -delete` 会把它们悄无声息地删掉。

**改用精确路径列表清理，不用 `-name` 通配。**

### ⚠ `CryptoParity.csproj` 的通配符断链

迁移后该文件的 `..\..\..\..\src\`（4 层）需改为 3 层。危险在于它是 `<Compile Include="...\*.cs">` **通配符**：
路径写错时 MSBuild **不报错**，只匹配 0 个文件，然后抛出「找不到 Sm4Engine 类型」这类看似无关的错误。

已修正并在文件内留下警示注释。同时发现 `packaging/build.ps1:149` 绕过了已有的 `$SrcDir` 变量硬拼路径 —— 一并收敛。

---

## 验证结果（全部实测）

| 验证项 | 结果 |
|--------|------|
| `-Pboot25 clean compile`（JDK 8） | **4 模块 SUCCESS**，编译 25+2+4 = 31 个源文件 |
| `-Pjakarta clean compile`（JDK 17） | **4 模块 SUCCESS** |
| core 单元测试 | **79 项 / Failures 0 / Errors 0 / Skipped 0** |
| package 声明 vs 目录路径 | **46 个 .java 全部一致，0 个吞目录** |
| 文件计数（迁移前后） | 46 .java / 57 文件 → **完全相同** |
| `Cryptunnel.Core` 编译 | 0 警告 0 错误 |
| `Cryptunnel.App` 编译 | 0 警告 0 错误 |
| `CryptoParity` **运行** | **52 项通过 / 0 失败 / exit 0** |
| `packaging/` 完好性 | 761 MB / 9 个安装包，未碰 |

> `CryptoParity` 特意跑了运行时而不只是编译 —— 通配符链接 0 个文件时也可能编过，
> 只有真跑出 52 项断言才能证明源码确实链接上了。

---

## 下一步

1. **执行 1118 处标识符重命名**（`Cryptunnel` → `Cryptunnel` 等，见 `docs/rename-map-cryptunnel.md`）
   ⚠ 映射表里记录的路径已因本次重构变化，需同步为 `java/` 与 `dotnet/` 下的新路径。
2. **全量验证**，含实际启动 sunrise 确认 WS + HTTP 两条通道都注册
   （防 AutoConfiguration 静默不装配 —— 这类失效不报错、不打日志）。
