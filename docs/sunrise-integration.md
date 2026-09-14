# sunrise 接入指引（含 yml 改造 diff）

> 适用：sunrise 当前版本（Spring Boot 2.5.3 + JDK 8）。后续 MOM/NPR 接入方式类似，仅 starter artifactId 不同。

## 1. 前置检查

- sunrise 已 `mvn -Pdev compile` 通过
- `~/.m2/repository/io/github/objectyan/cryptunnel-core/1.0.0/` 与
  `~/.m2/repository/io/github/objectyan/cryptunnel-starter-javax/1.0.0/`
  已被本仓库 `mvn install` 写入
- sunrise 项目 Java 编译目标 = 1.8（已确认：pom `<java.version>1.8</java.version>`）

## 2. 文件变更清单

| 操作 | 路径 | 原因 |
|------|------|------|
| 删除 | `src/main/java/com/crm/sunrise/jdbcproxy/`（整个目录，5 个类） | 改为外部依赖 |
| 修改 | `pom.xml` | 加 starter 依赖 |
| 修改 | `src/main/resources/application-dev.yml` | 165 行 cryptunnel 段改为 targets 形态 |
| 修改 | `src/main/resources/application-prod.yml` | 同上 |
| 检查 | 任意 `SecurityConfig` / `WebConfig` | 放行 `/ws-cryptunnel` 与 `/cryptunnel/**`（现状若已放行则不动） |

## 3. pom.xml 改动

在 `dependencies` 段（任意位置）新增：

```xml
<dependency>
    <groupId>io.github.objectyan</groupId>
    <artifactId>cryptunnel-starter-javax</artifactId>
    <version>1.0.0</version>
</dependency>
```

**不要**再用 shade-plugin 把 starter 的类打进 sunrise 的 fat jar；spring-boot-maven-plugin 会在打包阶段自动把 starter 及其传递依赖打进 BOOT-INF/lib/。

## 4. application-dev.yml 改造

### 4.1 改造前（现 sunrise:157-167）

```yaml
cryptunnel:
  enabled: true
  mysql-host: 10.6.10.22   # CloudCC MySQL 内网IP
  mysql-port: 3306
  aes-key: zt75hb7R0Fsrtnt3AMDpah5jgF/jtdmOhZUsmQzDBwU=
  auth-key: Fjevw3QY9ewlQTAwEGxkIfGDT19w4SWkuBlLTGTUqNI=
  auth-time-window: 300
  ws-path: /ws-cryptunnel
  max-connections: 5
  connect-timeout: 10000
```

### 4.2 改造后（推荐形态）

```yaml
cryptunnel:
  enabled: true
  default-target: cloudcc-prod        # 老客户端兜底 targetId
  auth-time-window: 300
  ws-path: /ws-cryptunnel

  targets:
    cloudcc-prod:
      display-name: CloudCC MySQL（dev）
      mysql-host: 10.6.10.22
      mysql-port: 3306
      aes-key: zt75hb7R0Fsrtnt3AMDpah5jgF/jtdmOhZUsmQzDBwU=
      auth-key: Fjevw3QY9ewlQTAwEGxkIfGDT19w4SWkuBlLTGTUqNI=
      max-connections: 5
      connect-timeout: 10000
      read-timeout: 300000             # 5 分钟

    # 示例：第二个 target（暂时注释；启用时取消注释 + 客户端配置 target=mes-test）
    # mes-test:
    #   display-name: MES 测试库
    #   mysql-host: 10.6.10.55
    #   mysql-port: 3306
    #   aes-key: ${JDBC_AES_KEY_MES}
    #   auth-key: ${JDBC_AUTH_KEY_MES}
    #   max-connections: 3
```

### 4.3 最小迁移（不改变行为，零风险）

如果你只想验证 starter 接入正确，**不动 yml** 也行：starter 会自动把平铺配置包装为名为 `default` 的 target，行为与现 sunrise 完全一致。等 starter 跑稳后再改成 targets 形态。

## 5. application-prod.yml 改造

类比 dev，把 `mysql-host: 10.6.10.12` + dev 的 `aes-key` 包装进 `cloudcc-prod` target，`default-target: cloudcc-prod` 保留兜底。

## 6. SecurityConfig / WebConfig 放行

如果 sunrise 启用了 Spring Security，需要放行 WS 端点与 HTTP 三个端点（不在现 sunrise 范围时无需此步）：

```java
// 现 sunrise 没有 SecurityConfig；保留示例如下供其他项目参考
http
  .authorizeRequests()
    .antMatchers("/ws-cryptunnel/**", "/cryptunnel/**").permitAll()
    .anyRequest().authenticated()
  .and()
  .csrf().ignoringAntMatchers("/cryptunnel/**");
```

## 7. 删除本地包

```bash
cd D:/Sunrise/Coding/CRM/sunrise
rm -rf src/main/java/com/crm/sunrise/jdbcproxy
```

删除后如再编译报错 `package com.crm.sunrise.jdbcproxy does not exist`，说明代码里有别处引用了旧类名（搜索全工程 `grep -r com.crm.sunrise.jdbcproxy` 排查）。

## 8. 回归 checklist

| 检查 | 命令 | 期望 |
|------|------|------|
| 编译 | `mvn -Pdev clean compile` | BUILD SUCCESS |
| 打包 | `mvn -Pdev clean package -DskipTests` | BUILD SUCCESS；BOOT-INF/lib/ 下含 cryptunnel-core-1.0.0.jar 与 starter jar |
| 启动 | `java -jar target/sunrise-0.0.1-SNAPSHOT.jar --spring.profiles.active=dev` | 启动日志含 `Cryptunnel: WS endpoint registered, path=/ws-cryptunnel`、`Cryptunnel: cipher=aes-256-cbc-hmac-sha256` 与 `Cryptunnel: registered targets [...]; default=cloudcc-prod` |
| WS 端点注册 | 启动后访问 `http://localhost:8081/actuator/mappings` | 能看到 `/ws-cryptunnel` |
| 客户端连接 | 跑本地 .NET/Java 客户端，配置 `serverUrl=http://localhost:8081`，target 留空 | 客户端能看到现有 CloudCC 库 |
| 新增 target（可选） | yml 加 mes-test target；客户端 yml 加 target: mes-test | 客户端能连到 MES 库，且 DBeaver 看到不同 schema |
| 库间隔离 | 把 A 客户端的 authKey 替换成 B target 的 | 连接被服务端拒绝，错误日志含 `Auth key invalid` |
| 老 yml 兼容 | 删除 yml 里的 `targets` 段，保留平铺 `mysql-host/aes-key/auth-key` | 启动成功，日志含 `Cryptunnel: registered targets [default]; default=default`（平铺配置由 `LegacyConfigAdapter` 包装成单个名为 `default` 的 target） |

## 9. 常见问题

| 现象 | 原因 | 修法 |
|------|------|------|
| 启动报 `No qualifying bean of type 'TargetRegistry'` | starter 没被 spring.factories 扫描到 | 检查 `META-INF/spring.factories` 是否在 jar 内（`unzip -l ... \| grep spring.factories`） |
| 启动报 `Table 'XXX' doesn't exist` | 别的代码引用了 sunrise 旧内嵌类 `com.crm.sunrise.jdbcproxy.JdbcProxyProperties` | 替换为 starter 提供的 `io.github.objectyan.cryptunnel.starter.CryptunnelProperties`，或保留一个空的旧类作为 type alias 过渡 |
| 客户端 4 段 AUTH 被拒 | `default-target` 没设 | yml 加 `default-target: cloudcc-prod` |
| 5 段 AUTH 被拒 | targetId 拼错 / target 名含大写 | 全部 target 名强制 `[a-z0-9-]+`；客户端输入框校验 |
| WS 连得上但 HTTP 降级 404 | Controller 没注册 | 检查 starter 的 `CryptunnelHttpController` 是否含 `@RequestMapping("/cryptunnel")` |
