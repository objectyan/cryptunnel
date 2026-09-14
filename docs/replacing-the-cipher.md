# 隧道加密算法：现状与扩展指南

> 本项目的隧道加密是**可插拔**的：三种算法已实现并通过三端字节级对齐验证，
> 通过配置即可切换，无需改代码。
>
> 决策背景见 [ADR-0003 加密算法可插拔](adr/0003-pluggable-tunnel-cipher.md)
> 与 [加密方案对比](decisions/crypto-options-comparison.md)。

---

## 1. 内置算法

| 配置值 | 说明 | 帧结构 | 每帧开销 |
|---|---|---|---|
| `aes-256-cbc-hmac-sha256`（默认） | AES-CBC + 外挂 HMAC，encrypt-then-MAC。与所有历史版本字节级兼容 | `Base64( IV[16] \|\| HMAC[32] \|\| 密文 )` | 48 字节 |
| `aes-256-gcm` | AEAD，认证标签内建，无需外挂 HMAC | `Base64( nonce[12] \|\| 密文 \|\| tag[16] )` | 28 字节 |
| `sm4` / `sm4-cbc-hmac-sha256` | 国密 SM4-CBC + HMAC-SHA256。**帧结构与默认算法完全相同**，只换分组算法 | `Base64( IV[16] \|\| HMAC[32] \|\| 密文 )` | 48 字节 |

实测帧长（32 字节明文）：CBC 112 字节 / GCM 88 字节 / SM4 112 字节。
**GCM 比默认算法每帧省 24 字节**——省掉 32 字节 HMAC，换成 16 字节 tag，nonce 也从 16 减到 12。

### 密钥派生

三种算法**共用同一把 `aesKey`**，各自派生独立子密钥，切换算法不需要换密钥：

| 算法 | 加密密钥 | 完整性密钥 |
|---|---|---|
| `aes-256-cbc-hmac-sha256` | `SHA-256("AES:" + aesKey)` | `SHA-256("HMAC:" + aesKey)` |
| `aes-256-gcm` | `SHA-256("AES:" + aesKey)`（与默认算法共用） | 无（GCM 自带 tag） |
| `sm4` | `SHA-256("SM4:" + aesKey)` 前 16 字节 | `SHA-256("HMAC:" + aesKey)`（与默认算法共用） |

SM4 用独立前缀 `"SM4:"`，使同一把 `aesKey` 下的 SM4 密钥与 AES 密钥互不相关。

---

## 2. 怎么切换

### 铁律：两端必须配同一个值

按 ADR-0003，**报文内不含任何算法标识**，两端靠配置约定一致。

```
DBeaver ──► [老 Java / .NET 客户端] ══加密══► [服务端 core] ──► MySQL
              ↑ 必须与服务端同算法 ↑
```

**只改一端 = 认证阶段直接失败。** 客户端会在加载配置时对非默认算法给出提示，
但无法检测服务端配了什么——报文里没这个信息。

客户端是**分发到使用者电脑上的桌面程序**，换算法意味着重新打包分发给所有使用者，排期时要算进去。

### 服务端

```yaml
cryptunnel:
  cipher: sm4        # 留空或不写 = aes-256-cbc-hmac-sha256
```

用 `sm4` 需要额外引入依赖（core 里是 optional，不用 SM4 时零依赖）：

```xml
<dependency>
  <groupId>org.bouncycastle</groupId>
  <artifactId>bcprov-jdk18on</artifactId>
  <version>1.78.1</version>
</dependency>
```

缺依赖时启动即报错并提示要加什么，不会是 `NoClassDefFoundError`。

### .NET 客户端

`config.d/*.yaml` 的 `defaults:` 段或项目段：

```yaml
defaults:
  cipher: sm4
```

未知算法名在**加载配置时**就被拒绝并列出可用值，不会等到连接失败。
.NET 侧的 SM4 是自实现的（见下），**不需要任何额外依赖**。

### 老 Java 客户端

第 6 个命令行参数：

```bash
java -jar cryptunnel-client.jar <serverUrl> <aesKey> <authKey> <localPort> <wsPath> sm4
```

留空则用默认算法。fat jar 已内置 BouncyCastle，三种算法开箱可用。

---

## 3. 跨语言对齐验证（改动加密后不可跳过）

`dotnet/build/parity/` 下有一键脚本，**双向**验证 Java ↔ .NET 字节级一致：

```bash
bash dotnet/build/parity/run.sh
```

三步：

| 步骤 | 内容 | 断言数 |
|---|---|---|
| 正方向 | C# 解开 Java 导出的基准载荷 + 密钥派生 + SM4 官方向量 + round-trip + 篡改 + 跨算法隔离 | 52 |
| 导出 | C# 现场加密三种算法的载荷 | — |
| 反方向 | Java 解开 C# 的载荷 | 3 |

| 文件 | 用途 |
|---|---|
| `CryptoParity/` | C# 侧自检工程。**直接链接客户端真实源码**，不复制副本，避免两份实现漂移 |
| `ReverseParityCheck.java` | Java 侧反向解密校验 |
| `vectors.txt` | 默认算法的黄金向量 |
| `vectors-sm4-gcm.txt` | SM4 / GCM 向量，含 GB/T 32907-2016 附录 A.1 官方单分组向量 |

**双向交叉验证是硬要求**——单向通过不能证明兼容。

### 自检覆盖的关键场景

- **密钥派生**：确定性输出，最强的跨语言校验项
- **SM4 分组函数**：对齐国家标准官方向量 `681edf34d206965e86b3e94f536e4246`，不依赖任何第三方实现
- **跨算法隔离**：六种组合两两互解必须**硬失败**。其中 CBC↔SM4 最关键——
  两者帧结构相同、HMAC 密钥相同，HMAC 校验会通过，只能靠分组算法不同导致填充失败兜住
- **篡改检测**：改 IV/nonce 首字节、改末字节都必须抛错
- **边界**：空明文、64KB 大报文、正好一个分组长度（验 PKCS7 补满整块）

---

## 4. 加一个新算法

### 服务端（SPI 已就位，新增 1 个类，0 处改动）

```java
public final class MyCipher implements TunnelCipher {
    @Override public String id() { return "my-cipher"; }
    // 可选：配置里能写的短名
    @Override public Collection<String> aliases() { return Collections.singletonList("mine"); }
    @Override public String seal(byte[] plaintext, String rawKey) { ... }
    @Override public byte[] open(String payload, String rawKey) { ... }
}
```

在 `TunnelCiphers` 静态块注册即可。若依赖第三方库，参照 `registerSm4IfAvailable()`
用 `Class.forName` 条件注册——**缺依赖时不注册**，配置该算法会得到「缺少依赖 xxx」的明确报错。

**约束**：`cryptunnel-core` 编译目标 JDK 8，不得使用 JDK 9+ API。

### .NET 客户端

实现 `ITunnelCipher`，在 `CipherRegistry` 静态构造里注册。

### 两端都要做

1. 补单元测试（core 侧参照 `Sm4CipherTest`，含跨算法隔离用例）
2. 在 `vectors-*.txt` 里加基准向量
3. 在 parity 自检里加对应断言
4. 跑 `run.sh` 确认双向通过

---

## 5. 兼容性红线

| 红线 | 说明 |
|---|---|
| 老客户端零改动 | 默认 `aes-256-cbc-hmac-sha256` 必须保持字节级等价，否则已部署客户端全部失效 |
| 报文内不含算法标识 | 新增实现不得把算法名写入载荷（避免向 DPI 暴露特征，见 ADR-0003） |
| 跨算法必须硬失败 | 配置不一致时要抛错，**绝不能解出垃圾数据当成合法明文** |
| 密钥派生方式 | 由各实现自行决定，但两端必须一致；建议用独立前缀避免跨算法密钥复用 |
| 帧大小 | `chunkSize=4096` 经加密+Base64 后须低于服务端单帧上限，见 ADR-0001 |

---

## 6. 实现说明

### 为什么 .NET 侧自己实现 SM4

.NET 侧引入 `BouncyCastle.Cryptography` 会给一个单文件自包含 WPF 客户端增加约 4MB 体积
和一个第三方供应链依赖。而 SM4 是公开国家标准、分组函数约 100 行，
且可用 GB/T 32907-2016 附录 A 的官方测试向量守护正确性。

实现见 `dotnet/src/Cryptunnel/Crypto/Sm4Engine.cs`（分组函数）
与 `Sm4Cipher.cs`（CBC 链接 + PKCS7 填充 + HMAC）。

> Java/BC 对 16 字节分组算法所谓的 `PKCS5Padding` 实际就是 PKCS7，两端填充逐字节相同。

### 为什么 GCM 的 nonce 是 12 字节

GCM 标准 nonce 长度为 96 bit。非 12 字节需要额外 GHASH 派生，各实现支持度不一，
统一用 12 字节保证跨语言互通。

另有一处结构差异需注意：.NET 的 `AesGcm` 把 tag 与密文**分开传参**，
而 Java 的 `Cipher` 把 tag **追加在密文尾部**。.NET 实现按 Java 布局拼装，两端才能互解。

---

## 7. 已知技术债

AUTH 报文 `AUTH:{authKey}:{ts}:{nonce}[:{targetId}]` **没有协议版本号字段**。

若将来需要引入**混合加密**（非对称密钥交换），属于**协议变更**——`TunnelCipher` SPI 帮不上忙，
且必然破坏老客户端兼容。届时需一次性引入版本号 + 能力协商，把破坏性升级收敛到一次。
