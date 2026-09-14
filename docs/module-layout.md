# 模块目录与 Maven 布局

> 本文是 spec.md §4 的实施级补充：完整文件树 + 每个文件的一句话用途 + pom 关键 profile 矩阵

## 1. 顶层变化

```
D:\Sunrise\Coding\CRM\cryptunnel-client\                # 现客户端仓库
├── pom.xml                                              # 改造为 packaging=pom + modules
├── dotnet/                                              # 不动
├── config.d/                                            # 不动
├── design/  packaging/  Cryptunnel/  src/  target/      # 不动
├── docs/                                                # 新增若干 .md
├── cryptunnel-core/                                     # 新模块 1
└── cryptunnel-spring-boot-starter/                      # 新模块 2
```

**不动 .git 现状**：本仓库之前没 git 仓库；如要发 Central，必须先 `git init` 并加 LICENSE / README / .gitignore。

## 2. cryptunnel-core/ 完整目录

```
cryptunnel-core/
├── pom.xml                                              # packaging=jar，<source>1.8</source>
└── src/
    ├── main/
    │   ├── java/io/github/objectyan/cryptunnel/core/
    │   │   ├── crypto/
    │   │   │   └── AesUtil.java                         # 字节级搬迁，方法签名不变
    │   │   ├── auth/
    │   │   │   ├── AuthMessage.java                     # 解构后对象（key/ts/nonce/targetId）
    │   │   │   ├── AuthMessageCodec.java                # parse(String) → AuthMessage，4 段时 targetId=null
    │   │   │   └── AuthMessageEncoder.java              # build(authKey, targetId) → String（client 也能用）
    │   │   ├── target/
    │   │   │   ├── TargetDefinition.java                # POJO：name/host/port/aesKey/authKey/maxConnections
    │   │   │   ├── TargetRegistry.java                  # interface：get(name) / names() / register()
    │   │   │   ├── DefaultTargetRegistry.java           # 实现 + 老 yml 兜底包装
    │   │   │   └── TargetRegistryException.java
    │   │   ├── tunnel/
    │   │   │   ├── TcpTunnel.java                       # interface：open(auth) → MySocket
    │   │   │   ├── MySocket.java                        # 包装 java.net.Socket + 流读取
    │   │   │   └── MysqlPacketReader.java               # 现 sunrise readMysqlPacket() 抽取
    │   │   └── util/
    │   │       └── BytesUtil.java                       # concat/subarray
    │   └── resources/
    │       └── (空)
    └── test/
        └── java/io/github/objectyan/cryptunnel/core/
            ├── crypto/AesParityTest.java                # encrypt(decrypt) 自身一致
            ├── crypto/AesParityMain.java                # main 方法，跨语言对照
            ├── auth/AuthMessageCodecTest.java           # 4 段 / 5 段 / 非法格式
            ├── target/DefaultTargetRegistryTest.java    # 老 yml 包装 + 命中 / 缺失
            └── tunnel/MysqlPacketReaderTest.java
```

## 3. cryptunnel-spring-boot-starter/ 完整目录

```
cryptunnel-spring-boot-starter/
├── pom.xml                                              # 两个 profile（boot25 / jakarta），依赖 cryptunnel-core
└── src/
    ├── main/
    │   ├── java/io/github/objectyan/cryptunnel/starter/
    │   │   ├── CryptunnelProperties.java                 # 顶层配置 + Targets 子结构
    │   │   ├── CryptunnelAutoConfiguration.java          # @Configuration + @EnableConfigurationProperties
    │   │   ├── CryptunnelWebSocketHandler.java           # 沿用现 sunrise，构造时按 targetId 拿密钥
    │   │   ├── CryptunnelWebSocketConfigurer.java        # 注册 WS endpoint
    │   │   ├── CryptunnelHttpController.java             # 沿用现 sunrise 三端点
    │   │   └── servlet/
    │   │       ├── ServletApiAdapter.java               # 反射判断 javax/jakarta 是否可用
    │   │       ├── Boot2HandlerFactory.java             # 编译时 import javax.servlet.* 的实现
    │   │       └── Boot3HandlerFactory.java             # 编译时 import jakarta.servlet.* 的实现
    │   └── resources/
    │       └── META-INF/
    │           ├── spring.factories                     # 给 Boot 2.x
    │           │   # org.springframework.boot.autoconfigure.EnableAutoConfiguration=\
    │           │   #   io.github.objectyan.cryptunnel.starter.CryptunnelAutoConfiguration
    │           └── spring/
    │               └── org.springframework.boot.autoconfigure.AutoConfiguration.imports
    │                   # 内容：一行 class FQN
    └── test/
        └── java/io/github/objectyan/cryptunnel/starter/
            ├── Boot25IntegrationTest.java               # 假设 ApplicationContext 起得来
            └── PropertiesBindingTest.java
```

> ⚠ **实际工程做法**：Boot 2.5 与 Boot 3.x/4.x 用的 Handler **类名相同但 import 不同**，maven 没法一份代码同时 import javax.servlet 与 jakarta.servlet。两种解法二选一：
> - **A（推荐）**：建 `cryptunnel-starter-javax` 与 `cryptunnel-starter-jakarta` 两个子模块，分别只 import 对应命名空间
> - **B**：单一 starter，profile 切换时通过 `maven-shade-plugin` 改 import 路径（复杂、风险高）
>
> **本 spec 选 A**。但因为 A 多出两个模块，目录变如下：

```
cryptunnel-spring-boot-starter/                  # 父 POM
├── pom.xml                                       # packaging=pom，两 profile 互斥：boot25 / jakarta
├── cryptunnel-starter-javax/                     # javax.servlet.*（给 Boot 2.5）
│   ├── pom.xml
│   └── src/main/java/...Handler.java             # import javax.servlet.*
├── cryptunnel-starter-jakarta/                   # jakarta.servlet.*（给 Boot 3.x/4.x，**两个 Boot 版本共用一份 jar**）
│   ├── pom.xml
│   └── src/main/java/...Handler.java             # import jakarta.servlet.*
└── cryptunnel-starter-common/                    # 共用：Properties / AutoConfiguration / AuthMessageCodec 调用
    ├── pom.xml
    └── src/main/java/...
```

**最外层命名**：artifactId 二选一暴露给用户引：
- `cryptunnel-starter-javax`（sunrise 现状用，Boot 2.5）
- `cryptunnel-starter-jakarta`（Boot 3.x / 4.x 通用，**两版本共用同一份 jar**）

**最终推荐**：

| artifactId | 适用项目 | 实际示例 |
|------------|---------|---------|
| `cryptunnel-starter-javax` | sunrise（Boot 2.5.3 + JDK 8） | 1.0.0 |
| `cryptunnel-starter-jakarta` | NPR / MOM（Boot 3.5.x + JDK 17） | 1.0.0 |
| `cryptunnel-starter-jakarta` | 新项目（Boot 4.1.x + JDK 17） | 1.0.0 |

> **2026-09-08 追加决策**：之前规划 jakarta 发 3 个版本号（1.0.0-boot3x / 1.0.0-boot4x / 1.0.0-jakarta），实测 jakarta jar 在 Boot 3.5 与 4.1 下
> 编译产物字节级一致，没必要贴两个版本号。统一发 `1.0.0`，消费方 app 自己覆盖 `spring-boot.version` 切到 4.1.x 即可（starter 内部所有 spring-boot 依赖都走该属性）。

## 4. pom.xml profile 矩阵

### 4.1 cryptunnel-core/pom.xml

```xml
<project>
  <modelVersion>4.0.0</modelVersion>
  <parent>
    <groupId>io.github.objectyan</groupId>
    <artifactId>cryptunnel-parent</artifactId>  <!-- 仓库根的 parent pom -->
    <version>1.0.0</version>
  </parent>
  <artifactId>cryptunnel-core</artifactId>
  <properties>
    <maven.compiler.source>1.8</maven.compiler.source>
    <maven.compiler.target>1.8</maven.compiler.target>
  </properties>
  <dependencies>
    <dependency>
      <groupId>junit</groupId>
      <artifactId>junit</artifactId>
      <version>4.13.2</version>
      <scope>test</scope>
    </dependency>
  </dependencies>
</project>
```

### 4.2 cryptunnel-spring-boot-starter/pom.xml（聚合父）

```xml
<project>
  <modelVersion>4.0.0</modelVersion>
  <parent>
    <groupId>io.github.objectyan</groupId>
    <artifactId>cryptunnel-parent</artifactId>
    <version>1.0.0</version>
  </parent>
  <artifactId>cryptunnel-spring-boot-starter-parent</artifactId>
  <packaging>pom</packaging>
  <modules>
    <module>cryptunnel-starter-common</module>
  </modules>
  <profiles>
    <profile>
      <id>boot25</id>
      <modules>
        <module>cryptunnel-starter-common</module>
        <module>cryptunnel-starter-javax</module>
      </modules>
      <properties>
        <spring-boot.version>2.5.3</spring-boot.version>
        <servlet.api>javax</servlet.api>
      </properties>
    </profile>
    <profile>
      <id>jakarta</id>
      <modules>
        <module>cryptunnel-starter-common</module>
        <module>cryptunnel-starter-jakarta</module>
      </modules>
      <properties>
        <spring-boot.version>3.5.0</spring-boot.version>
        <servlet.api>jakarta</servlet.api>
      </properties>
    </profile>
  </profiles>
</project>
```

### 4.3 cryptunnel-starter-javax/pom.xml（关键片段）

```xml
<parent>
  <groupId>io.github.objectyan</groupId>
  <artifactId>cryptunnel-spring-boot-starter-parent</artifactId>
  <version>1.0.0</version>
</parent>
<artifactId>cryptunnel-starter-javax</artifactId>
<!-- version 继承自 parent：1.0.0 -->
<dependencies>
  <dependency>
    <groupId>io.github.objectyan</groupId>
    <artifactId>cryptunnel-core</artifactId>
    <version>1.0.0</version>
  </dependency>
  <dependency>
    <groupId>org.springframework.boot</groupId>
    <artifactId>spring-boot-starter-websocket</artifactId>
  </dependency>
  <dependency>
    <groupId>org.springframework.boot</groupId>
    <artifactId>spring-boot-starter-web</artifactId>
  </dependency>
  <!-- 注意：javax.servlet-api 来自 spring-boot-starter-web 传递依赖；不显式声明 -->
</dependencies>
```

### 4.4 cryptunnel-starter-jakarta/pom.xml（关键差异）

```xml
<!-- 完全相同，但 jakarta 模块显式排除 javax.servlet-api，引入 jakarta.servlet-api -->
<dependency>
  <groupId>jakarta.servlet</groupId>
  <artifactId>jakarta.servlet-api</artifactId>
  <version>6.0.0</version>
  <scope>provided</scope>
</dependency>
```

> **关键约束**：jakarta 模块的 Handler 源码**只能** `import jakarta.servlet.*`；javax 模块的 Handler 源码**只能** `import javax.servlet.*`。两份代码通过 mvn profile 互斥编译，绝不能 mix。

## 5. 仓库根 pom.xml（聚合）

```xml
<project>
  <modelVersion>4.0.0</modelVersion>
  <groupId>io.github.objectyan</groupId>
  <artifactId>cryptunnel-parent</artifactId>
  <version>1.0.0</version>
  <packaging>pom</packaging>
  <modules>
    <module>cryptunnel-core</module>
    <module>cryptunnel-spring-boot-starter</module>
  </modules>
  <properties>
    <java.version>1.8</java.version>
  </properties>
  <build>
    <pluginManagement>
      <plugins>
        <plugin>
          <groupId>org.apache.maven.plugins</groupId>
          <artifactId>maven-compiler-plugin</artifactId>
          <version>3.11.0</version>
        </plugin>
        <plugin>
          <groupId>org.apache.maven.plugins</groupId>
          <artifactId>maven-source-plugin</artifactId>
          <version>3.3.0</version>
        </plugin>
        <plugin>
          <groupId>org.apache.maven.plugins</groupId>
          <artifactId>maven-javadoc-plugin</artifactId>
          <version>3.6.0</version>
        </plugin>
        <plugin>
          <groupId>org.apache.maven.plugins</groupId>
          <artifactId>maven-gpg-plugin</artifactId>
          <version>3.1.0</version>
        </plugin>
      </plugins>
    </pluginManagement>
  </build>
</project>
```

## 6. 文件 → 用途速查表

| 文件 | 用途 | 实现优先级 |
|------|------|----------|
| `core/crypto/AesUtil.java` | 加密原样搬迁 | P0 |
| `core/auth/AuthMessageCodec.java` | AUTH 报文解析与构造 | P0 |
| `core/target/TargetDefinition.java` | target POJO | P0 |
| `core/target/TargetRegistry.java` | 注册表接口 | P0 |
| `core/target/DefaultTargetRegistry.java` | 注册表实现 + 老 yml 兜底 | P0 |
| `core/tunnel/MysqlPacketReader.java` | MySQL 协议包读取 | P0 |
| `core/tunnel/TcpTunnel.java` | Socket 转发抽象 | P0 |
| `starter-common/CryptunnelProperties.java` | yml 绑定 | P0 |
| `starter-common/CryptunnelAutoConfiguration.java` | 自动装配入口 | P0 |
| `starter-{javax|jakarta}/CryptunnelWebSocketHandler.java` | WS 端处理 | P0 |
| `starter-{javax|jakarta}/CryptunnelHttpController.java` | HTTP 三端点 | P0 |
| `starter-{javax|jakarta}/CryptunnelWebSocketConfigurer.java` | WS 端点注册 | P0 |
| `resources/META-INF/spring.factories` | Boot 2.5 装配 | P0 |
| `resources/META-INF/spring/...AutoConfiguration.imports` | Boot 3.4+/4.x 装配 | P0 |
| `docs/sunrise-integration.md` | sunrise 接入指引 | P0 |
| `docs/client-upgrade.md` | 客户端协议升级 | P1 |
| `docs/central-publish.md` | Central 发布 README | P1 |
