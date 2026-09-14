# Cryptunnel Maven 化 + 多 Target 改造 — 全交付概览

> 本轮交付物：8 份文档 + cryptunnel-core（11 类 + 38 测试）+ cryptunnel-spring-boot-starter（两 profile）
> 状态：**实现 + 单测 + 两 profile 构建全部通过**
> 日期：2026-09-08

## 本轮做了什么

把设计文档从「合同」落到「可 install 的 jar」：
- `cryptunnel-core` — JDK 8 编译目标，无 Spring 依赖，6 个测试类 38 个 @Test 全过
- `cryptunnel-spring-boot-starter` — 两 profile 互斥编译（boot25 / jakarta），分别产出 javax / jakarta 适配 jar
- 4 份新文档完成（`spec.md` / `module-layout.md` / `interface-contracts.md` / `sunrise-integration.md` / `client-upgrade.md` / `central-publish.md`）

## 关键决策（用户拍板）

1. **模块位置**：`cryptunnel-client` 下新增 `cryptunnel-core` + `cryptunnel-spring-boot-starter-parent`（聚合父）+ 3 个子模块
2. **兼容矩阵**（硬约束）：
   - `core` 编译用 JDK 8 source
   - starter 发 2 个 artifact：`cryptunnel-starter-javax-1.0.0.jar`（Boot 2.5.3，javax.servlet）/ `cryptunnel-starter-jakarta-1.0.0.jar`（Boot 3.x / 4.x，jakarta.servlet）
   - 自动装配入口**双写**：`META-INF/spring.factories`（Boot 2）+ `META-INF/spring/...AutoConfiguration.imports`（Boot 3.4+/4.x）
3. **Target 密钥**：per-target 独立 aesKey/authKey；启动期 `LegacyConfigAdapter.wrap()` 把老平铺 yml 自动包装为 `default` target
4. **协议变更**：AUTH 报文由 4 段加到 5 段（`AUTH:{key}:{ts}:{nonce}:{targetId}`），4 段 = 走 default（向后兼容零客户端改动）
5. **DB 地址归属**：服务端（target 注册表白名单，防 SSRF）
6. **发布**：先 `mvn install` 本地仓库（已完成）；Central 走 sonatype 脚本与 README 已写好但 deploy 还没跑（等 sonatype 账号）
7. **2026-09-08 追加决策**：jakarta jar 兼容 Boot 3.x / 4.x 共用一份 artifact（实测字节级一致），不再分两个版本号；总产物从 3 jar 减为 2 jar

## 关键设计取舍（已修正版）

| 取舍 | 选择 | 原因 |
|------|------|------|
| 客户端 target 字段 | 可选，缺省 = default | 旧客户端零改动 |
| 老 yml 平铺配置 | 自动包装为 default target | 最小迁移路径 |
| Spring servlet 命名空间 | **拆两个子模块**（javax + jakarta） | 不能在同源码里 `import javax.servlet.*` 与 `import jakarta.servlet.*` |
| Boot 3.x 与 4.x | **共用一个 jakarta jar** | 本 starter 不直接 import servlet API，源码与字节码在两条主线下完全一致；切两份是贴两个版本号而已 |
| nonce 缓存 | 全 target 共用一个 | v1 简化；P2 评估 per-target 分片 |
| 旧协议 | 4 段 = 走 default | 服务端先升级，客户端后升级 |
| 服务端定位 target | **逐 target 试解 + HMAC 校验**（`TargetDiscovery`） | 加密报文不暴露 targetId，O(N) 在 N 是个位数时无压力；HMAC 是天然筛选器，乱码不会误接受 |
| ~~明文 `TARGET:xxx\n` 前缀~~ | ❌ 已废弃 | 与 5 段协议冲突，破坏老客户端零改动承诺 |
| ~~3 profile（boot25 / boot3x / boot4x）~~ | ❌ 已合并为 2 profile | boot3x 与 boot4x 字节码一致，贴两个版本号无意义 |
| Central 发布 | 先本地 install，Central 走 sonatype | user 还没提供 sonatype 账号 |

## 两 profile 构建结果

| Profile | Spring Boot | Servlet 命名空间 | 产物 | 状态 |
|---------|-------------|------------------|------|------|
| `boot25` | 2.5.3 | javax | `cryptunnel-starter-javax-1.0.0.jar` | ✅ |
| `jakarta` | 3.x / 4.x | jakarta | `cryptunnel-starter-jakarta-1.0.0.jar` | ✅ |

> jakarta profile 默认编 Spring Boot 3.5.0（当前 LTS）。如果消费方是 Boot 4.1.x，只需在自己 app 的 pom 里覆盖 `spring-boot.version` 属性即可——starter 自己的 dependency 全部走 `${spring-boot.version}`，消费方属性值会胜出。

## 已知坑（实现期已修）

| 坑 | 修复 |
|----|------|
| 子模块 parent 用 `spring-boot-starter-parent` → `${spring-boot.version}` 空 | 改用自家 `cryptunnel-spring-boot-starter-parent` 作 parent + 自己 import `spring-boot-dependencies` BOM |
| `DefaultTargetRegistry.all()` 忘 import `Collection` | 已加 import |
| ~~明文 `TARGET:xxx\n` 前缀方案~~ | 弃用，改用 `TargetDiscovery` 试解 |
| ~~3 profile 拆分（boot3x / boot4x）~~ | 合并为单 `jakarta` profile；boot3x 与 boot4x 字节码一致，没必要贴两个版本号 |

## 已知限制 / 后续 TODO

- **真实环境联调**（sunrise 端配 yml + 老客户端连过）还没做 —— 本地只有单元测试覆盖
- **Maven Central deploy** 没跑（账号信息 user 待提供；`central-publish.md` 已就位）
- **跨语言 AUTH 报文互通**没跑（client-upgrade.md §4 的 `AesParityMain` 还没写）
- **server 端无 reload 能力**：target 白名单改 yml 必须重启。未来库数量爆炸可加 P4 SPI/配置中心

## 下一步

1. sunrise 改引 starter（`sunrise-integration.md` 给出 checklist）
2. 老 .NET/Java 客户端回归（应零改动跑通）
3. 配两套 target yml，跑多 target 联调
4. 若一切 OK，准备 Maven Central 发布

