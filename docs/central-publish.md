# Maven Central 发布 README

> 适用：cryptunnel-core / cryptunnel-starter-javax / cryptunnel-starter-jakarta 三个 artifact
> 目标仓库：Maven Central `https://repo1.maven.org/maven2/io/github/objectyan/`
> 当前状态：本仓库未发布过任何 artifact，需要先做 Central Portal + GPG 注册

> ⚠ **2026 流程已变**：老教程让你去 `issues.sonatype.org` 开 JIRA 工单，那条路已在
> **2025-06-30 正式 sunset**。新项目一律走 **Central Portal**（`central.sonatype.com`），
> namespace 自助验证、无需人工审核、上传端点和插件都换了。本文按新流程写。

## 1. 前置：Central Portal 注册 + namespace 验证

### 1.1 注册账号（务必用 GitHub 登录）

- 打开 https://central.sonatype.com/ ，点 Sign In → **用 GitHub 账号（objectyan）授权登录**
- ⚠ **最贵的坑**：Sonatype 把「不同登录方式」视为**不同账户**，哪怕邮箱相同。
  用 GitHub 登录拿到 namespace 后，下次改用邮箱密码登录 → namespace 页面空的、token 也属于另一个人，
  表现为 deploy 时 401/403。**永远用当初拿到 namespace 时的同一种方式登录。**

### 1.2 namespace 验证（`io.github.objectyan`）

- 用 GitHub 登录时，Sonatype **通常已自动完成验证**——因为 GitHub 会给每个用户一个
  `objectyan.github.io` 域名（GitHub Pages），Portal 据此自动放行 `io.github.objectyan`。
- 登录后点右上角用户名 → **View Namespaces**，看是否已有 `io.github.objectyan` 且带绿色 **Verified** 徽章。
  - **有 Verified** → 直接跳到 1.3
  - **没有** → 点 Add Namespace 填 `io.github.objectyan`，会给你一个 **Verification Key**；
    在 GitHub 建一个**公开**仓库，仓库名就叫这个 Verification Key（如 `github.com/objectyan/abc123xyz`），
    回 Portal 点 Verify Namespace。验证通过后这个空仓库可以删掉。
- 相比域名验证的好处：**不需要自有域名、不需要 DNS TXT 记录、不需要等人工审核**。

### 1.3 GPG 密钥

```bash
# 1. 安装 GPG（Windows 用 gpg4win；本机如有 git bash 自带 gpg 命令）
gpg --version

# 2. 生成密钥对
gpg --gen-key
# Real name: 你的名字
# Email: 与 Central Portal 账号一致的邮箱
# Passphrase: 记住，下一步要填进 settings.xml

# 3. 查看指纹
gpg --list-secret-keys --keyid-format=long
# 形如： sec   rsa4096/ABC123DEF456 2026-09-08
#          ^^^^^^^^^^^^^^^^ 这就是 KEY_ID

# 4. 上传公钥到 keyserver（Central 会去这里查）
gpg --keyserver keyserver.ubuntu.com --send-keys ABC123DEF456

# 5. 同步到 pgp.mit.edu（兜底）
gpg --keyserver pgp.mit.edu --send-keys ABC123DEF456
```

**两个不写在官方文档里的坑：**

| 症状 | 真因 | 修法 |
|------|------|------|
| `gpg: agent_genkey failed: Timeout` / `Key generation failed: Timeout` | gpg-agent 无法向你索要 passphrase：既没有 GUI pinentry，也没导出 `GPG_TTY` 让它在终端画输入框 | ① 装 pinentry（Win：gpg4win 自带；mac：`brew install pinentry-mac`）② `~/.gnupg/gpg-agent.conf` 里写 `pinentry-program <路径>` ③ shell 配置里 `export GPG_TTY=$(tty)` ④ `gpgconf --kill gpg-agent` 重启 |
| 上传公钥报 `gpg: sending key ... failed: No route to host`（但 curl 正常） | keyserver 流量走 **dirmngr** 这个独立守护进程，**它不继承 `http_proxy` 环境变量** | 在 `~/.gnupg/dirmngr.conf` 里显式写 `honor-http-proxy` 或 `http-proxy http://host:port`，然后 `gpgconf --kill dirmngr` |

> 用 `%no-protection`（无 passphrase 批量生成）的教程会完全绕过第一个坑，所以少有人提。

## 2. ~/.m2/settings.xml 模板

### 2.1 先生成 Portal user token

Central Portal 右上角用户名 → **View Account** → **Generate User Token**。
拿到的是一对 `<username>` / `<password>`（形如 `AbCdEf12` / `一长串随机串`），
**不是你的 GitHub 账号密码、也不是老 OSSRH 的 JIRA 账密**。

### 2.2 填进 settings.xml

```xml
<?xml version="1.0" encoding="UTF-8"?>
<settings xmlns="http://maven.apache.org/SETTINGS/1.0.0">
  <servers>
    <server>
      <!-- ⚠ 这个 id 必须叫 central，与 central-publishing-maven-plugin
           的 publishingServerId 默认值一致。Portal 文档里给的
           <id>${server}</id> 是「填空占位符」，不是 Maven 变量，
           照抄会报「找不到凭据」且错误信息完全不指向 settings.xml。 -->
      <id>central</id>
      <username>Portal-Token-Username</username>
      <password>Portal-Token-Password</password>
    </server>
  </servers>

  <profiles>
    <profile>
      <id>gpg-sign</id>
      <properties>
        <gpg.keyname>ABC123DEF456</gpg.keyname>
        <gpg.passphrase>your-gpg-passphrase</gpg.passphrase>
        <gpg.executable>gpg</gpg.executable>
      </properties>
    </profile>
  </profiles>

  <activeProfiles>
    <activeProfile>gpg-sign</activeProfile>
  </activeProfiles>
</settings>
```

> ⚠ **不要把这个文件 commit 到 git**。`~/.m2/settings.xml` 默认不会被 commit，但要确认仓库根没 .mvn/ 之类的覆盖。
> 原模板里的 `<gpg.skip>true</gpg.skip>` 已删掉——那会让签名整体跳过，Central 会拒收无签名 artifact。

## 3. pom 公共片段：发布配置

仓库根 `pom.xml` 加：

```xml
<build>
  <plugins>
    <!-- source jar -->
    <plugin>
      <groupId>org.apache.maven.plugins</groupId>
      <artifactId>maven-source-plugin</artifactId>
      <executions>
        <execution>
          <id>attach-sources</id>
          <goals><goal>jar-no-fork</goal></goals>
        </execution>
      </executions>
    </plugin>

    <!-- javadoc jar -->
    <plugin>
      <groupId>org.apache.maven.plugins</groupId>
      <artifactId>maven-javadoc-plugin</artifactId>
      <executions>
        <execution>
          <id>attach-javadocs</id>
          <goals><goal>jar</goal></goals>
        </execution>
      </executions>
      <configuration>
        <doclint>none</doclint>  <!-- JDK 8 警告非阻断；core 用 1.8 source 时常见 warning -->
      </configuration>
    </plugin>

    <!-- GPG 签名 -->
    <plugin>
      <groupId>org.apache.maven.plugins</groupId>
      <artifactId>maven-gpg-plugin</artifactId>
      <executions>
        <execution>
          <id>sign-artifacts</id>
          <phase>verify</phase>
          <goals><goal>sign</goal></goals>
        </execution>
      </executions>
    </plugin>

    <!-- Central Portal 发布插件（替代老的 nexus-staging-maven-plugin） -->
    <plugin>
      <groupId>org.sonatype.central</groupId>
      <artifactId>central-publishing-maven-plugin</artifactId>
      <version>0.8.0</version>
      <extensions>true</extensions>
      <configuration>
        <!-- 必须与 settings.xml 里 <server><id> 一致 -->
        <publishingServerId>central</publishingServerId>
        <!-- 自动发布，不用再去 Portal UI 手点 Publish -->
        <autoPublish>true</autoPublish>
        <!-- 阻塞直到 Portal 确认已上线：绿色构建 == 真的发布成功 -->
        <waitUntil>published</waitUntil>
      </configuration>
    </plugin>
  </plugins>
</build>
```

> ⚠ **不要再写 `<distributionManagement>` 指向 `oss.sonatype.org`**。
> 那些端点已经下线；`central-publishing-maven-plugin` 通过
> `https://central.sonatype.com/api/v1/publisher/upload` 直接投递，无需 distributionManagement。

**Central 必需的 POM 元数据**（缺任何一项都会被拒收）：

```xml
<name>Cryptunnel Core</name>
<description>协议无关的加密内网隧道核心库</description>
<url>https://github.com/objectyan/cryptunnel</url>

<licenses>
  <license>
    <name>Apache License, Version 2.0</name>
    <url>https://www.apache.org/licenses/LICENSE-2.0.txt</url>
  </license>
</licenses>

<developers>
  <developer>
    <id>objectyan</id>
    <name>objectyan</name>
    <url>https://github.com/objectyan</url>
  </developer>
</developers>

<scm>
  <connection>scm:git:https://github.com/objectyan/cryptunnel.git</connection>
  <developerConnection>scm:git:ssh://git@github.com/objectyan/cryptunnel.git</developerConnection>
  <url>https://github.com/objectyan/cryptunnel</url>
</scm>
```

## 4. 发布命令

### 4.1 先发 SNAPSHOT 验证链路

```bash
cd cryptunnel-client

# 改 pom.xml 里所有版本号为 1.0.0-SNAPSHOT
mvn versions:set -DnewVersion=1.0.0-SNAPSHOT

env -u HTTPS_PROXY -u HTTP_PROXY \
  mvn.cmd -Pboot25 clean deploy

# SNAPSHOT 走独立仓库（Central Portal 的 snapshot 端点）：
# https://central.sonatype.com/repository/maven-snapshots/io/github/objectyan/
```

> SNAPSHOT 需要在 pom 里额外配 `<distributionManagement><snapshotRepository>`
> 指向 `https://central.sonatype.com/repository/maven-snapshots/`（id 同样是 `central`）。
> 正式版则完全由 `central-publishing-maven-plugin` 接管，不用 distributionManagement。

### 4.2 发正式版本

```bash
# 1. 改版本号为正式号
mvn versions:set -DnewVersion=1.0.0

# 2. 发 javax（Boot 2.5）—— autoPublish=true + waitUntil=published
#    意味着这条命令跑完就是真上线，不需要再去 Portal 手点
env -u HTTPS_PROXY -u HTTP_PROXY \
  mvn.cmd -Pboot25 clean deploy

# 3. 发 jakarta（Boot 3.x / 4.x 共用一份 jar）
env -u HTTPS_PROXY -u HTTP_PROXY \
  mvn.cmd -Pjakarta clean deploy

# 4. 几分钟后 artifact 出现在：
#    https://repo1.maven.org/maven2/io/github/objectyan/
```

> **老流程对比**：OSSRH 时代要先 deploy 到 staging，再登 `oss.sonatype.org` 找
> staging repo → 点 Close → 等校验 → 点 Release。现在 `autoPublish` + `waitUntil=published`
> 把这套「手点仪式」压进构建：命令返回 0 就是已发布，返回非 0 就是没发出去，不存在中间态。

> **2026-09-08 简化**：之前规划分别发 `1.0.0-boot25` / `1.0.0-boot3x` / `1.0.0-boot4x` 三次。jakarta 在 Boot 3.x/4.x 下产物字节级一致，
> 合并为单 `jakarta` profile；整轮只发两个 jar（javax + jakarta），都在 `1.0.0` 版本号下。

### 4.3 在 sunrise 引正式版

`sunrise/pom.xml`：

```xml
<dependency>
  <groupId>io.github.objectyan</groupId>
  <artifactId>cryptunnel-starter-javax</artifactId>
  <version>1.0.0</version>
</dependency>
```

sunrise 跑 `mvn -Pdev clean compile` 验证。

## 5. 常见问题

| 现象 | 原因 | 修法 |
|------|------|------|
| `gpg: signing failed: No such file or directory` | gpg 不在 PATH | 装 gpg4win，或在 settings.xml 配 `<gpg.executable>C:/Program Files (x86)/GnuPG/bin/gpg.exe</gpg.executable>` |
| `gpg: signing failed: Inappropriate ioctl` | gpg 在 windows 下没有 TTY 输 passphrase | 用 `--pinentry-mode loopback` 加进 gpg.executable 参数，或用 gpg-agent 缓存 |
| `gpg: agent_genkey failed: Timeout` | gpg-agent 无 pinentry 也无 `GPG_TTY`，索要 passphrase 无门可走 | 装 pinentry + 写 `gpg-agent.conf` + `export GPG_TTY=$(tty)` + `gpgconf --kill gpg-agent` |
| `gpg: sending key failed: No route to host`（curl 却正常） | dirmngr 不继承 `http_proxy` | `~/.gnupg/dirmngr.conf` 写 `honor-http-proxy`，`gpgconf --kill dirmngr` |
| deploy 报「找不到凭据」，错误不指向 settings.xml | settings.xml 里 `<id>` 照抄了 Portal 文档的 `${server}` 占位符 | 改成 `<id>central</id>`，与插件 `publishingServerId` 一致 |
| `401 Unauthorized` / `403 Forbidden` | ① 用的不是 Portal user token 而是账号密码；② **登录方式变了**（当初 GitHub 登录拿 namespace，这次用邮箱登录 → 视为另一个账户） | 重新用**当初那种方式**登录 Portal，重新 Generate User Token |
| Portal 上 namespace 列表是空的 | 同上：登录方式不一致 | 先怀疑登录方式，再怀疑 namespace |
| javadoc 报 `error: unknown tag` | doclint 严格化 | `<configuration><doclint>none</doclint></configuration>` |
| deploy 时 `-Dmaven.test.skip=true` 仍跑测试 | 跟 `-DskipTests` 不同 | 显式 `-DskipTests -Dmaven.javadoc.skip=false` |
| 构建绿了但 Central 上没有 | 没配 `waitUntil=published`，构建只是上传成功、Portal 还没 publish | 配 `<autoPublish>true</autoPublish><waitUntil>published</waitUntil>`，或去 Portal Deployments 页手点 Publish |
| 找老教程里的 `nexus-staging-maven-plugin` 不工作 | 它打的是 `oss.sonatype.org` 的 Nexus staging REST API，端点已下线 | 换成 `org.sonatype.central:central-publishing-maven-plugin` |

## 6. 发布前 checklist

- [ ] Central Portal 用 **GitHub 账号（objectyan）** 登录，且记住这是唯一登录方式
- [ ] `io.github.objectyan` namespace 在 View Namespaces 里显示绿色 **Verified**
- [ ] 已 Generate User Token，填进 `~/.m2/settings.xml` 的 `<id>central</id>`
- [ ] GPG 公钥已同步到 keyserver.ubuntu.com（`gpg --keyserver keyserver.ubuntu.com --send-keys <KEY_ID>` 返回成功）
- [ ] pom 含全部 Central 必需元数据：name / description / url / licenses / developers / scm
- [ ] `mvn.cmd -Pboot25 clean install` 在本机跑通，所有单测绿
- [ ] `mvn.cmd -Pjakarta clean install` 在 JDK 17 跑通
- [ ] `-SNAPSHOT` 试发成功，artifact 出现在 Portal snapshot 仓库
- [ ] LICENSE（Apache 2.0 建议）已加到仓库根
- [ ] README.md 含 quick start（artifactId + 1 段 yml 示例）
- [ ] `git tag v1.0.0 && git push --tags` 已完成

## 7. 仓库地址速查

| 用途 | URL |
|------|-----|
| Central Portal（发布管理入口） | `https://central.sonatype.com/` |
| namespace 管理 | `https://central.sonatype.com/publishing/namespaces` |
| 部署记录（看 publish 状态） | `https://central.sonatype.com/publishing/deployments` |
| Snapshot 仓库 | `https://central.sonatype.com/repository/maven-snapshots/io/github/objectyan/` |
| 正式仓库（已发布） | `https://repo1.maven.org/maven2/io/github/objectyan/` |
| 搜索（发布后几小时生效） | `https://central.sonatype.com/search?q=io.github.objectyan` |
| GPG 密钥服务器 | `https://keyserver.ubuntu.com/` |
| ~~sonatype OSSRH~~ | ~~`https://oss.sonatype.org/`~~ **已于 2025-06-30 下线，勿用** |
