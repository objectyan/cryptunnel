# 客户端协议升级说明（targetId 第 5 段）

> 适用：cryptunnel-client 现有 .NET 客户端 + Java 客户端（`cryptunnel-client/src/main/java/.../CryptunnelClient.java`）
> 状态：v1.0.0 启动后，**老客户端零改动可继续用**（4 段 AUTH 走 default target）；本文讲如何升级到 5 段以连非默认 target

## 1. 协议变化（仅加，不改）

### 1.1 老格式（v0.x，4 段）

```
AUTH:{authKey}:{unixTimestamp}:{nonceHex}
```

### 1.2 新格式（v1.x，5 段，向后兼容）

```
AUTH:{authKey}:{unixTimestamp}:{nonceHex}:{targetId}
```

| 段数 | 服务端行为 |
|------|----------|
| 4 段 | targetId 视为 `null` → 路由到 `default-target`（yml 配置的） |
| 5 段 | targetId 必须出现在服务端注册表，否则 401/CloseStatus.NOT_ACCEPTABLE |
| 其它段数 | 拒绝 |

## 2. .NET 客户端改动

### 2.1 AesTunnel.cs BuildAuthMessage 新增可选 targetId

文件：`dotnet/src/Cryptunnel/Crypto/AesTunnel.cs:30-36`

```csharp
// 改造前
public static string BuildAuthMessage(string authKey) { ... }

// 改造后（重载，向后兼容）
public static string BuildAuthMessage(string authKey)
    => BuildAuthMessage(authKey, null);

public static string BuildAuthMessage(string authKey, string targetId)
{
    var nonce = RandomNumberGenerator.GetBytes(16);
    var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    var basePart = $"AUTH:{authKey}:{timestamp}:{Convert.ToHexString(nonce).ToLowerInvariant()}";
    return string.IsNullOrEmpty(targetId) ? basePart : $"{basePart}:{targetId}";
}
```

### 2.2 ProjectConfig 加 Target 字段

文件：`dotnet/src/Cryptunnel/Config/ProjectConfig.cs`（如没有则新建）

```csharp
public sealed class ProjectConfig
{
    public string Name { get; init; }
    public string ServerUrl { get; init; }
    public string AesKey { get; init; }
    public string AuthKey { get; init; }
    public ushort LocalPort { get; init; }
    public string Target { get; init; }       // 新增：可选 targetId
    public bool Enabled { get; init; } = true;
    public TransportMode Transport { get; init; } = TransportMode.Auto;
}
```

### 2.3 config.yaml 改造

```yaml
# 改造前
projects:
  crm-prod:
    serverUrl: https://your-crm-server:8081
    aesKey: "..."
    authKey: "..."
    local:
      port: 13306
    enabled: true

# 改造后（target 字段可选；不填 = 走服务端 default-target）
projects:
  crm-prod:
    serverUrl: https://your-crm-server:8081
    aesKey: "..."
    authKey: "..."
    target: cloudcc-prod          # 可选
    local:
      port: 13306
    enabled: true

  mes-test:
    serverUrl: https://your-crm-server:8081
    aesKey: "..."
    authKey: "..."                # 注意：必须用 mes-test 在服务端注册时的 key
    target: mes-test
    local:
      port: 13307                 # 不同本地端口
    enabled: true
```

### 2.4 启动隧道时传入 target

文件：`dotnet/src/Cryptunnel/Client/TunnelClient.cs`（按项目实际路径）

```csharp
var auth = AesTunnel.BuildAuthMessage(config.AuthKey, config.Target);
// 后续 ws.send(auth) 即可
```

### 2.5 UI 加输入框

- ProjectEditorWindow 的 form 加一栏"目标库"（combobox，预填服务端注册过的 target 列表）
- 留空 = 走 default
- 校验：只能填 `[a-z0-9-]+`

## 3. Java 客户端改动

### 3.1 AesTunnel.encode 等价方法

文件：`cryptunnel-client/src/main/java/io/github/objectyan/proxy/AesUtil.java`（客户端版，不是服务端的）

> 客户端 Java 端是 `CryptunnelClient` 调用的；服务端 Java 端是 sunrise 调用的。两边 AesUtil 类**不共享**，需各自升级。

新增方法：

```java
public static String buildAuthMessage(String authKey, String targetId) {
    long ts = System.currentTimeMillis() / 1000L;
    SecureRandom sr = new SecureRandom();
    byte[] nonceBytes = new byte[16];
    sr.nextBytes(nonceBytes);
    StringBuilder sb = new StringBuilder(64);
    sb.append("AUTH:").append(authKey).append(":").append(ts).append(":");
    for (byte b : nonceBytes) sb.append(String.format("%02x", b & 0xFF));
    if (targetId != null && !targetId.isEmpty()) {
        sb.append(":").append(targetId);
    }
    return sb.toString();
}
```

### 3.2 配置文件解析加 target 字段

文件：`src/main/java/io/github/objectyan/proxy/CryptunnelClient.java`

`Project` 内部类加 `String target` 字段；yaml 解析时读取 `target:` 段（缺省 null）。

## 4. 跨语言互通验证脚本

### 4.1 Java 端生成参考向量

`cryptunnel-core` 提供一个 main 方法：

```java
// 路径：cryptunnel-core/src/main/java/io/github/objectyan/cryptunnel/core/crypto/AesParityMain.java
public static void main(String[] args) {
    String rawKey = "test-aes-key-32-bytes-long-padding";
    String plain  = "Hello, jdbc proxy tunnel!";
    byte[] cipher = AesUtil.encrypt(plain.getBytes(UTF_8),
                    AesUtil.deriveAesKey(rawKey),
                    AesUtil.deriveHmacKey(rawKey));
    System.out.println(Base64.getEncoder().encodeToString(cipher));
    // .NET 端用同一 rawKey 跑 AesTunnel.Encrypt，输出应与上面字节级一致
}
```

### 4.2 .NET 端对照

```csharp
// dotnet/test/Crypto/AesParityTests.cs
[Fact]
public void Encrypt_MatchesJavaReferenceVector()
{
    var key = AesTunnel.DeriveAesKey("test-aes-key-32-bytes-long-padding");
    var mac = AesTunnel.DeriveHmacKey("test-aes-key-32-bytes-long-padding");
    var cipher = AesTunnel.Encrypt(
        Encoding.UTF8.GetBytes("Hello, jdbc proxy tunnel!"),
        key, mac);
    // 用 Java AesParityMain 的输出做对照（每跑一次 IV 都不同，不直接比 base64）
    // 应至少 verify decrypt 回原文
    var roundtrip = AesTunnel.Decrypt(cipher, key, mac);
    Assert.Equal("Hello, jdbc proxy tunnel!", Encoding.UTF8.GetString(roundtrip));
}
```

### 4.3 服务端 4 段 / 5 段对照

启动 starter 后，跑一个最小集成测试（sunrise 仓库下）：

```java
@SpringBootTest
public class AuthMessageCodecTest {
    @Autowired TargetRegistry registry;

    @Test public void fourSegment_GoesToDefault() {
        AuthMessage m = AuthMessageCodec.parse("AUTH:k:1700000000:abc123");
        assertNull(m.getTargetId());
        assertEquals("cloudcc-prod", registry.get(m.getTargetId()).getName());
    }

    @Test public void fiveSegment_RoutesToNamedTarget() {
        AuthMessage m = AuthMessageCodec.parse("AUTH:otherkey:1700000000:abc:mes-test");
        assertEquals("mes-test", m.getTargetId());
        assertEquals("mes-test", registry.get(m.getTargetId()).getName());
    }
}
```

## 5. 升级顺序建议

1. 服务端先升级到 starter，并部署（**老客户端仍可用**，走 default）
2. 客户端按需升级：哪个项目需要连非 default target，哪个项目再加 target 字段
3. 所有客户端升级完之前，不要在 yml 删 default-target 兜底
4. 灰度：客户端先加 target 字段，yml 里保留兼容；全部跑通后清理
