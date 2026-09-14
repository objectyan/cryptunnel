# groupId 开源化迁移 — 交付概览

> 日期：2026-09-09
> 变更：`com.crm.sunrise` → `io.github.objectyan`
> 状态：**已完成，全量验证通过**

> ⚠ **本文是 v1.3.0 当时的历史记录，其中的名称与路径均为当时的实际状态，有意保留不作更新。**
> v1.6.0 已将产品更名为 **cryptunnel** 并重构目录结构。对照关系：
> | 本文中（迁移当时） | 现在 |
> |---|---|
> | 包 `io.github.objectyan.jdbcproxy.*` | `io.github.objectyan.cryptunnel.*` |
> | 模块 `jdbc-proxy-core` | `java/cryptunnel-core` |
> | 模块 `jdbc-proxy-spring-boot-starter` | `java/cryptunnel-starter-{common,javax,jakarta}` |
> | artifact `jdbc-proxy-starter-javax` | `cryptunnel-starter-javax` |
> | WS 路径 `/ws-jdbc-proxy` | `/ws-cryptunnel`（两端均可配，填旧值即可对接未更名的服务端） |
>
> **故文中的 `mvn deploy` 命令（第 4 节）与验证结果里的 artifact / `~/.m2` 坐标（第 6 节）不可直接照抄**，
> 请按上表换成现名；更名的完整说明见 `CHANGELOG.md` 的「重构与更名」节。

---

## 1. 为什么改

项目要开源到 GitHub（用户名 `objectyan`），公司内部 groupId `com.crm.sunrise` 无法通过 Maven Central 的命名空间验证。

选 `io.github.objectyan` 的理由：Central Portal 对 `io.github.<GitHub用户名>` 命名空间**用 GitHub 登录即自动验证**，不需要购买域名、不需要 DNS TXT 记录。

---

## 2. 改了什么

| 类别 | 数量 | 明细 |
|------|------|------|
| POM `<groupId>` | 6 个 | 根 / core / starter parent / common / javax / jakarta（含内部依赖引用） |
| 根 pom `<mainClass>` | 2 处 | shade-plugin + launch4j 各一处 |
| Java 包名与目录 | 38 个文件 | core(main 15 + test 6) / common 2 / javax 4 / jakarta 4 / 老客户端 2 / dotnet parity 1 |
| META-INF 自动配置 FQN | 6 个文件 | 3 模块 × (`spring.factories` + `AutoConfiguration.imports`) |
| 文档 | 8 份 | 见下表 |
| 打包与配置文件 | 4 个 | `JdbcProxy.cfg`、`.jpackage.xml`、`parity/vectors.txt`、`.idea/workspace.xml` |

### 包名映射

| 旧 | 新 |
|----|-----|
| `com.crm.sunrise.jdbcproxy.core.*` | `io.github.objectyan.jdbcproxy.core.*` |
| `com.crm.sunrise.jdbcproxy.starter.*` | `io.github.objectyan.jdbcproxy.starter.*` |
| `com.crm.sunrise.proxy.*`（老客户端） | `io.github.objectyan.proxy.*` |

### 文档改动

| 文档 | 改动 |
|------|------|
| `docs/central-publish.md` | **全文重写**（OSSRH → Central Portal，见 §4） |
| `docs/sunrise-integration.md` | groupId、.m2 路径、Properties FQN |
| `docs/GLOSSARY.md` | 包名 + 补历史注 |
| `docs/interface-contracts.md` | 6 处 API 契约代码样例 |
| `docs/maven-starter-and-multi-datasource-plan.md` | 坐标 + groupId |
| `docs/module-layout.md` | groupId、目录树、AutoConfiguration FQN |
| `docs/spec.md` | 目录树、AesParityMain 命令、AutoConfiguration FQN |
| `docs/client-upgrade.md` | 3 处源码路径 |

---

## 3. 有意**保留**的 8 处 `com.crm.sunrise`

这些不是漏改 —— 它们指的都是 **sunrise 另一个仓库里需要删除的旧内嵌代码路径**，改了接入指引就错了：

| 位置 | 内容 |
|------|------|
| `docs/adr/0001-...md:5` | 历史 ADR 的组件说明 |
| `docs/GLOSSARY.md:6` | 历史注（starter 化之前的位置） |
| `docs/spec.md:230,231` | `rm -rf src/main/java/com/crm/sunrise/jdbcproxy` |
| `docs/sunrise-integration.md:17,110,113` | 同上 + 编译报错排查提示 |

---

## 4. `docs/central-publish.md` 为什么要全文重写

原文档写的是**已废弃**的 OSSRH JIRA 流程。2026 年的真实情况：

| 项 | 旧（已废弃） | 新（现行） |
|----|-------------|-----------|
| 入口 | `issues.sonatype.org` 提 JIRA 工单 | `central.sonatype.com` 自助注册 |
| 受理 | **2025-06-30 起停止受理新工单** | 立即可用 |
| 命名空间验证 | 人工审核工单 | `io.github.<user>` GitHub 登录自动验证 |
| 发布插件 | `nexus-staging-maven-plugin` | `org.sonatype.central:central-publishing-maven-plugin:0.8.0` |
| `<distributionManagement>` | 必需 | **不再需要** |
| `settings.xml` server id | `ossrh` | **`central`** |
| Release 动作 | 登录 `oss.sonatype.org` 手工点 Close/Release | `autoPublish=true` + `waitUntil=published` 全自动 |

文档里也记了几个容易踩的坑：
- Portal 官方文档里的 `${server}` 是**占位符，不是 Maven 变量**，照抄会失败
- 不同登录方式（GitHub / Google / 邮箱）算**不同账号**，命名空间不通用
- Windows 上 GPG 的 pinentry / dirmngr 会被代理卡死，需 `--pinentry-mode loopback` + `--keyserver-options http-proxy=`

发布命令（两个 profile 各 deploy 一次）：
```bash
cd jdbc-proxy-core                 && mvn.cmd clean deploy
cd jdbc-proxy-spring-boot-starter  && mvn.cmd -Pboot25  clean deploy
cd jdbc-proxy-spring-boot-starter  && mvn.cmd -Pjakarta clean deploy
```

---

## 5. 迁移过程中踩的最大一个坑

**`mv` 时吞掉了中间目录层。**

老包名 `com.crm.sunrise.jdbcproxy` 是 **4 段**，新包名 `io.github.objectyan` 是 **3 段**。
执行 `mv com/crm/sunrise/jdbcproxy → io/github/objectyan` 时，`jdbcproxy` 这一层被吞掉了：

```
目录：  io/github/objectyan/core/          （4 段）
声明：  package io.github.objectyan.jdbcproxy.core;  （5 段）
```

**症状极具迷惑性** —— Maven 不报「package 与目录不一致」，而是：
```
maven-javadoc-plugin ... error: No public or protected classes found to document.
```
让人误以为是 javadoc 插件配置问题。

**修法**：`mkdir -p <base>/jdbcproxy && mv <base>/core <base>/jdbcproxy/core`

**检测脚本（迁包名后必跑，比 grep 靠谱）**：
```bash
for f in $(find . -name '*.java' -not -path '*/target/*'); do
  pkg=$(grep -m1 '^package ' "$f" | sed 's/package //;s/;//')
  dir=$(dirname "$f" | sed 's#.*/java/##;s#/#.#g')
  [ "$pkg" != "$dir" ] && echo "MISMATCH: $f  pkg=$pkg dir=$dir"
done
```
跑到**零 MISMATCH** 才算迁完。

另一个坑：`Edit` 在部分 `.md` / `pom.xml` 上**返回成功但内容未落盘**，改完必须 grep 复核；不生效时改用 `sed -i 'Ns#old#new#'` 按行号替换。

---

## 6. 验证结果

本地仓库先清理了旧坐标下的 5 个构件，然后全量重建：

| 构建目标 | 结果 |
|----------|------|
| `jdbc-proxy-core` | BUILD SUCCESS，**38 个测试全过** |
| `-Pboot25`（javax / Boot 2.5.3） | BUILD SUCCESS → `jdbc-proxy-starter-javax-1.0.0.jar` |
| `-Pjakarta`（jakarta / Boot 3.x·4.x） | BUILD SUCCESS → `jdbc-proxy-starter-jakarta-1.0.0.jar` |
| 根 fat jar | BUILD SUCCESS → `jdbc-proxy-client.jar` |

产物级校验：

| 检查项 | 结果 |
|--------|------|
| jar 内 class 路径 | 全部 `io/github/objectyan/jdbcproxy/...` |
| `spring.factories` FQN | 与实际类名完全对应（javax / jakarta 各 2 条） |
| `AutoConfiguration.imports` FQN | 同上 |
| fat jar `Main-Class` | `io.github.objectyan.proxy.JdbcProxyClient` |
| fat jar 内 `crm/sunrise` 残留 | **0** |
| `~/.m2` 新坐标 | `io/github/objectyan/{jdbc-proxy-core, jdbc-proxy-starter-common, jdbc-proxy-starter-javax, jdbc-proxy-starter-jakarta}` |

---

## 7. 下游影响：sunrise 接入方要改什么

sunrise（`D:\Sunrise\Coding\CRM\sunrise`）的 pom 依赖坐标要跟着改：

```xml
<dependency>
    <groupId>io.github.objectyan</groupId>          <!-- 原 com.crm.sunrise -->
    <artifactId>jdbc-proxy-starter-javax</artifactId>
    <version>1.0.0</version>
</dependency>
```

如果 sunrise 代码里有 `import com.crm.sunrise.jdbcproxy.starter.JdbcProxyProperties`，改成 `io.github.objectyan.jdbcproxy.starter.JdbcProxyProperties`。

**协议与线级格式零变化** —— AUTH 报文、AES/HMAC 派生、`CHUNK_SIZE`、WS 路径 `/ws-jdbc-proxy`、HTTP 降级端点全部未动，**老客户端不需要任何改动**。

---

## 8. 待办

- Maven Central 首次 deploy 还没跑（需要 Portal 账号 + GPG 密钥）
- 真实环境联调（sunrise 配 yml + 老客户端连过）仍未做，目前只有单元测试覆盖
- GitHub 仓库还没建（本目录目前不是 git 仓库）
