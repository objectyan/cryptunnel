# 关键接口契约（核心 API 表面）

> 本文是 spec.md §5 的实现级契约：每个接口的字段、方法签名、不可变约束、异常与默认值。**实现期以本文为唯一编码依据。**

---

## 1. AuthMessage 报文编解码

### 1.1 数据模型

```java
package io.github.objectyan.cryptunnel.core.auth;

import java.util.Objects;

public final class AuthMessage {
    private final String authKey;
    private final long timestamp;        // 秒级
    private final String nonce;          // hex
    private final String targetId;       // 5 段报文才有；4 段为 null

    public AuthMessage(String authKey, long timestamp, String nonce, String targetId) {
        this.authKey = Objects.requireNonNull(authKey);
        this.timestamp = timestamp;
        this.nonce = Objects.requireNonNull(nonce);
        this.targetId = targetId;        // 允许 null
    }
    // 4 个 getter（无 setter；不可变）
}
```

### 1.2 编解码器

```java
public final class AuthMessageCodec {

    /**
     * 解析 AUTH 报文。
     * 4 段：AUTH:{key}:{ts}:{nonce}                  → targetId = null
     * 5 段：AUTH:{key}:{ts}:{nonce}:{targetId}       → targetId = 第 5 段
     * 其它段数或非 AUTH 前缀                       → 抛 IllegalArgumentException
     */
    public static AuthMessage parse(String payload) { ... }

    /**
     * 编码：服务端用不到，但提供 client 端（Java 客户端）使用。
     * targetId == null 时输出 4 段；否则输出 5 段。
     */
    public static String encode(String authKey, String targetId) {
        long ts = System.currentTimeMillis() / 1000L;
        String nonce = randomHexNonce(16);
        if (targetId == null) {
            return "AUTH:" + authKey + ":" + ts + ":" + nonce;
        }
        return "AUTH:" + authKey + ":" + ts + ":" + nonce + ":" + targetId;
    }

    private static String randomHexNonce(int byteLen) { ... }   // SecureRandom → lowercase hex
}
```

### 1.3 不可变约束

- `parse` 永远不返回 null；非法输入抛 `IllegalArgumentException`
- `AuthMessage` 全字段 final
- `targetId` 允许为 null；调用方按 null 走 default 路由

---

## 2. TargetDefinition 数据模型

```java
package io.github.objectyan.cryptunnel.core.target;

import java.util.Objects;

public final class TargetDefinition {
    private final String name;              // 全局唯一，[a-z0-9-]+
    private final String displayName;       // 界面显示用，可空
    private final String mysqlHost;
    private final int mysqlPort;
    private final String aesKey;            // 原始明文（不要记录到日志）
    private final String authKey;           // 原始明文（不要记录到日志）
    private final int maxConnections;
    private final int connectTimeoutMs;
    private final int readTimeoutMs;        // 0 表示不限

    // 构造器：全字段必填校验（除 displayName）
    public TargetDefinition(
        String name, String displayName,
        String mysqlHost, int mysqlPort,
        String aesKey, String authKey,
        int maxConnections, int connectTimeoutMs, int readTimeoutMs
    ) { ... }

    // 9 个 getter（不可变）

    /**
     * 派生 AES 密钥，调用 core.crypto.AesUtil.deriveAesKey(aesKey)
     * 不在本类缓存 SecretKeySpec，避免热加载时密钥不可变
     */
    public byte[] deriveAesKey() { return AesUtil.deriveAesKey(aesKey).getEncoded(); }
    public byte[] deriveHmacKey() { return AesUtil.deriveHmacKey(aesKey).getEncoded(); }

    @Override public String toString() {
        // 不打印 aesKey / authKey
        return "TargetDefinition{" +
            "name='" + name + '\'' +
            ", displayName='" + displayName + '\'' +
            ", mysqlHost='" + mysqlHost + '\'' +
            ", mysqlPort=" + mysqlPort +
            ", maxConnections=" + maxConnections +
            '}';
    }
}
```

### 字段约束表

| 字段 | 类型 | 必填 | 校验 | 默认 |
|------|------|------|------|------|
| name | String | ✅ | `^[a-z0-9-]{1,32}$` 且全局唯一 | — |
| displayName | String | 否 | 长度 ≤ 64 | null |
| mysqlHost | String | ✅ | 长度 1-255 | — |
| mysqlPort | int | ✅ | 1-65535 | — |
| aesKey | String | ✅ | 长度 ≥ 8 | — |
| authKey | String | ✅ | 长度 ≥ 8 | — |
| maxConnections | int | 否 | 1-1000 | 5 |
| connectTimeoutMs | int | 否 | 1000-60000 | 10000 |
| readTimeoutMs | int | 否 | 0 或 1000-600000 | 300000（5min）⚠ 改默认值 |

---

## 3. TargetRegistry 注册表

### 3.1 接口

```java
public interface TargetRegistry {
    /**
     * 根据 targetId 查 target。targetId == null 时返回 default target。
     * @throws TargetNotFoundException 不存在
     */
    TargetDefinition get(String targetId);

    /**
     * 全部 target 名称（用于管理端点 / 调试日志）。
     */
    java.util.Set<String> names();

    /**
     * 全部 target 定义（不可变快照）。用于服务端在收到加密 AUTH 报文时，
     * <b>逐个试用每个 target 的密钥尝试解密</b>（参见 §3.4 TargetDiscovery）。
     */
    java.util.Collection<TargetDefinition> all();

    /**
     * 注册（供 TargetProvider SPI 动态加载用；本期不实现 SPI，方法可保留为 package-private）。
     */
    void register(TargetDefinition target);

    String defaultTargetName();
}
```

### 3.2 默认实现

```java
public final class DefaultTargetRegistry implements TargetRegistry {
    private final ConcurrentMap<String, TargetDefinition> map = new ConcurrentHashMap<>();
    private final String defaultTargetName;

    public DefaultTargetRegistry(List<TargetDefinition> targets, String defaultTargetName) {
        // 1. 入参校验
        // 2. 注册所有 target（重名抛异常）
        // 3. 校验 defaultTargetName 必须存在
    }

    @Override
    public TargetDefinition get(String targetId) {
        if (targetId == null || targetId.isEmpty()) targetId = defaultTargetName;
        TargetDefinition t = map.get(targetId);
        if (t == null) throw new TargetNotFoundException(targetId);
        return t;
    }

    @Override
    public Set<String> names() { return Collections.unmodifiableSet(map.keySet()); }

    @Override
    public Collection<TargetDefinition> all() {
        return Collections.unmodifiableCollection(new ArrayList<>(map.values()));
    }

    @Override
    public void register(TargetDefinition target) {
        map.put(target.getName(), target);
    }

    @Override
    public String defaultTargetName() { return defaultTargetName; }
}
```

### 3.4 TargetDiscovery — 加密 AUTH 报文的 target 定位

**问题**：AUTH 报文是 AES 加密的，server 在解密前不知道属于哪个 target。

**方案**（已实现于 `cryptunnel-core/.../auth/TargetDiscovery.java`）：

```java
public final class TargetDiscovery {
    /**
     * 逐个 target 试解 + HMAC 校验。N 通常为个位数，O(N) 可接受。
     * @return DecryptedAuth{target, authMessage}
     * @throws IllegalArgumentException 全部 target 都解不开
     */
    public static DecryptedAuth discover(TargetRegistry registry, String encryptedPayload) {
        for (TargetDefinition t : registry.all()) {
            try {
                byte[] plain = AesUtil.decrypt(encryptedPayload, t.deriveAesKey(), t.deriveHmacKey());
                AuthMessage m = AuthMessageCodec.parse(new String(plain, UTF_8));
                return new DecryptedAuth(t, m);    // 成功
            } catch (Exception e) {
                // 失败就试下一个；HMAC 校验失败的密文**不会**解出合法 AUTH 文本
            }
        }
        throw new IllegalArgumentException("no target can decrypt the AUTH payload");
    }

    public static final class DecryptedAuth {
        public final TargetDefinition target;
        public final AuthMessage auth;
    }
}
```

**为什么安全**：HMAC 校验在解密密文之前/之后立刻验证，错密钥解出的「乱码」HMAC 一定不过，直接抛错进入下一个 target。`AuthMessageCodec.parse` 也会拒绝非 `AUTH:` 开头 / 段数不对的乱码。

**调用方**：`CryptunnelWebSocketHandler.handleAuth()` / `CryptunnelHttpController.connect()` 首条消息都先调 `TargetDiscovery.discover()`，拿到的 `target` 整个会话期间绑定（用于数据通道的加解密）。

### 3.3 老 yml 兜底包装逻辑

当 yml 只配平铺的 `cryptunnel.mysql-host` / `aes-key` / `auth-key`（无 `targets` 段），启动时由 starter 调用：

```java
public static List<TargetDefinition> wrapLegacySingleTarget(CryptunnelProperties props) {
    if (props.getTargets() != null && !props.getTargets().isEmpty()) {
        return props.getTargets();        // 已配 targets，跳过
    }
    // 兜底：把平铺字段包装为名为 "default" 的 target
    TargetDefinition def = new TargetDefinition(
        "default",
        "默认数据库（兼容老配置）",
        props.getMysqlHost(),
        props.getMysqlPort(),
        props.getAesKey(),
        props.getAuthKey(),
        props.getMaxConnections(),
        props.getConnectTimeout(),
        props.getReadTimeout() == 0 ? 300000 : props.getReadTimeout()    // ⚠ 改默认
    );
    return Collections.singletonList(def);
}
```

`CryptunnelAutoConfiguration` 启动顺序：

1. 解析 `CryptunnelProperties`
2. 调 `wrapLegacySingleTarget(props)` 得到 target 列表
3. 解析 `props.getDefaultTarget()`（缺省 `"default"`）
4. 构造 `DefaultTargetRegistry(targets, defaultTargetName)`
5. 注册为 Spring Bean

---

## 4. TcpTunnel 抽象

```java
package io.github.objectyan.cryptunnel.core.tunnel;

import io.github.objectyan.cryptunnel.core.target.TargetDefinition;
import java.io.IOException;

public interface TcpTunnel {
    /**
     * 打开到 target 的 MySQL TCP 连接。
     * 由调用方负责 close。
     */
    MySqlConnection open(TargetDefinition target) throws IOException;
}
```

```java
public final class MySqlConnection implements AutoCloseable {
    private final Socket socket;
    private final InputStream in;
    private final OutputStream out;

    public MySqlConnection(Socket socket) throws IOException {
        this.socket = socket;
        this.in = socket.getInputStream();
        this.out = socket.getOutputStream();
    }

    public InputStream in() { return in; }
    public OutputStream out() { return out; }

    @Override public void close() throws IOException { socket.close(); }
}
```

实现：

```java
public final class DefaultTcpTunnel implements TcpTunnel {
    @Override
    public MySqlConnection open(TargetDefinition target) throws IOException {
        Socket s = new Socket();
        s.connect(new InetSocketAddress(target.getMysqlHost(), target.getMysqlPort()),
                  target.getConnectTimeoutMs());
        s.setTcpNoDelay(true);
        s.setKeepAlive(true);
        s.setSoTimeout(target.getReadTimeoutMs());
        return new MySqlConnection(s);
    }
}
```

---

## 5. WebSocket / HTTP Handler 共享接口

为了让 `CryptunnelWebSocketHandler` 与 `CryptunnelHttpController` 不重复实现 nonce 校验 / 认证校验 / 连接上限逻辑，定义：

```java
package io.github.objectyan.cryptunnel.core.tunnel;

import io.github.objectyan.cryptunnel.core.auth.AuthMessage;

public interface AuthenticatedSessionManager {

    /**
     * 校验 auth 报文，命中 target 注册表，并完成 nonce 校验。
     * @return 解析后的 target + 已打开的 MySQL 连接
     * @throws AuthFailedException 任意一步失败
     */
    AuthenticatedSession authenticate(AuthMessage msg) throws AuthFailedException;
}

public final class AuthenticatedSession {
    public final TargetDefinition target;
    public final MySqlConnection mysql;
    public AuthenticatedSession(TargetDefinition target, MySqlConnection mysql) { ... }
}
```

`DefaultAuthenticatedSessionManager` 在 core 里实现（不依赖 servlet API），starter 的 WS/HTTP Handler 都调它。

---

## 6. 异常族

| 类 | 父类 | 触发条件 | HTTP / WS 关闭码 |
|----|------|---------|------------------|
| `AuthFailedException` | RuntimeException | authKey 不匹配 / 时间窗口超 / nonce 重复 | WS: `CloseStatus.NOT_ACCEPTABLE`；HTTP: 401 |
| `TargetNotFoundException` | RuntimeException | targetId 在注册表中不存在 | WS: `NOT_ACCEPTABLE("Target not found")`；HTTP: 400 |
| `TargetCapacityExceededException` | RuntimeException | target.maxConnections 已满 | WS: `SERVICE_UNAVAILABLE`；HTTP: 503 |
| `MysqlConnectionFailedException` | IOException 子类 | `new Socket(host, port)` 失败 | WS: `SERVER_ERROR("MySQL connection failed")`；HTTP: 502 |
| `NonceReplayException` | AuthFailedException 子类 | nonce 已使用 | WS: `NOT_ACCEPTABLE("Auth nonce replay")`；HTTP: 401 |

所有异常在 starter 的 Handler 里被捕获并映射为正确的关闭码 + 日志，**不让异常 stacktrace 泄露到客户端**。

---

## 7. Nonce 缓存策略

```java
public final class NonceCache {
    private final ConcurrentMap<String, Long> used = new ConcurrentHashMap<>();
    private final long windowMs;       // 通常 2 × authTimeWindow × 1000

    public boolean tryConsume(String nonce) {
        long now = System.currentTimeMillis();
        cleanup(now);
        return used.putIfAbsent(nonce, now) == null;
    }

    private void cleanup(long now) {
        long threshold = now - windowMs;
        used.entrySet().removeIf(e -> e.getValue() < threshold);
    }
}
```

**v1 简化**：所有 target 共用一个 nonce 缓存（无需 per-target 分片，nonce 不带 target 信息，攻击面受 authKey 隔离已足够）。P2 评估是否分片。

---

## 8. starter 的 AutoConfiguration 形态

```java
@Configuration
@ConditionalOnProperty(name = "cryptunnel.enabled", havingValue = "true", matchIfMissing = false)
@EnableConfigurationProperties(CryptunnelProperties.class)
public class CryptunnelAutoConfiguration {

    @Bean
    public TargetRegistry targetRegistry(CryptunnelProperties props) {
        List<TargetDefinition> targets = DefaultTargetRegistry.wrapLegacySingleTarget(props);
        return new DefaultTargetRegistry(targets, props.getDefaultTarget());
    }

    @Bean
    public AuthenticatedSessionManager sessionManager(
            TargetRegistry registry, NonceCache nonceCache) {
        return new DefaultAuthenticatedSessionManager(registry, nonceCache);
    }

    @Bean
    public TcpTunnel tcpTunnel() { return new DefaultTcpTunnel(); }

    @Bean
    public CryptunnelWebSocketConfigurer webSocketConfigurer(
            CryptunnelProperties props,
            AuthenticatedSessionManager sessionMgr,
            TcpTunnel tunnel) {
        return new CryptunnelWebSocketConfigurer(props, sessionMgr, tunnel);
    }

    @Bean
    public CryptunnelHttpController httpController(
            CryptunnelProperties props,
            AuthenticatedSessionManager sessionMgr,
            TcpTunnel tunnel) {
        return new CryptunnelHttpController(props, sessionMgr, tunnel);
    }
}
```

javax 与 jakarta 模块**只**包含 `CryptunnelWebSocketHandler` / `CryptunnelHttpController` / `CryptunnelWebSocketConfigurer` 三个 servlet 相关类，其余共用 `cryptunnel-starter-common`。
