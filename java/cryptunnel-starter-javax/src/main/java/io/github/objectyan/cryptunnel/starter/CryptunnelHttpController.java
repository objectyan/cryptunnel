package io.github.objectyan.cryptunnel.starter;

import io.github.objectyan.cryptunnel.core.auth.AuthMessage;
import io.github.objectyan.cryptunnel.core.auth.TargetDiscovery;
import io.github.objectyan.cryptunnel.core.crypto.AesUtil;
import io.github.objectyan.cryptunnel.core.target.TargetDefinition;
import io.github.objectyan.cryptunnel.core.target.TargetRegistry;
import io.github.objectyan.cryptunnel.core.tunnel.AuthFailedException;
import io.github.objectyan.cryptunnel.core.tunnel.AuthenticatedSession;
import io.github.objectyan.cryptunnel.core.tunnel.AuthenticatedSessionManager;
import io.github.objectyan.cryptunnel.core.tunnel.DefaultAuthenticatedSessionManager;
import io.github.objectyan.cryptunnel.core.tunnel.MySqlConnection;
import io.github.objectyan.cryptunnel.core.tunnel.MysqlPacketReader;
import io.github.objectyan.cryptunnel.core.tunnel.TargetCapacityExceededException;
import io.github.objectyan.cryptunnel.core.tunnel.TcpTunnel;
import java.io.IOException;
import java.io.OutputStream;
import java.util.Map;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.Executors;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.ThreadFactory;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicLong;
import javax.annotation.PostConstruct;
import javax.annotation.PreDestroy;
import javax.crypto.spec.SecretKeySpec;
import org.slf4j.Logger;
import org.slf4j.LoggerFactory;
import org.springframework.http.HttpStatus;
import org.springframework.http.ResponseEntity;
import org.springframework.web.bind.annotation.PostMapping;
import org.springframework.web.bind.annotation.RequestBody;
import org.springframework.web.bind.annotation.RequestHeader;
import org.springframework.web.bind.annotation.RequestMapping;
import org.springframework.web.bind.annotation.RestController;

/**
 * HTTP 降级 Controller（javax，Boot 2.x）。
 *
 * <p>三个端点：</p>
 * <ul>
 *   <li>POST /cryptunnel/connect   - 鉴权 + 打开 MySQL TCP，返回 connId + 加密握手包</li>
 *   <li>POST /cryptunnel/tunnel    - X-Conn-Id 标识会话，转发加密字节</li>
 *   <li>POST /cryptunnel/disconnect - 关闭会话</li>
 * </ul>
 *
 * <p>target 识别走 {@link TargetDiscovery}（逐 target 试解 + HMAC 校验），与 WS 通道一致。</p>
 */
@RestController
@RequestMapping("/cryptunnel")
public class CryptunnelHttpController {

    private static final Logger log = LoggerFactory.getLogger(CryptunnelHttpController.class);

    private final CryptunnelProperties properties;
    private final AuthenticatedSessionManager sessionManager;
    private final DefaultAuthenticatedSessionManager sessionManagerImpl;
    private final TcpTunnel tunnel;
    private final TargetRegistry targetRegistry;
    private final Map<String, SessionContext> httpSessions = new ConcurrentHashMap<String, SessionContext>();

    /**
     * 陈旧会话清理调度器。
     *
     * <p><b>为什么自建而不用 {@code @Scheduled}</b>：{@code @Scheduled} 需要在配置类上
     * 标注 {@code @EnableScheduling}，而那是一个<b>全局开关</b>——会把整个宿主应用的
     * 定时任务机制打开。使用方只是想要一个 Cryptunnel，不该因此改变自身应用的调度行为
     * （宿主可能已有自己的 {@code @EnableScheduling} 与线程池配置，叠加后行为不可预期）。</p>
     *
     * <p>改为单线程私有调度器，随 Bean 生命周期启停：对外零副作用，对使用方零负担——
     * 清理仍由库自己完成，不需要使用方记得调用任何东西。</p>
     */
    private ScheduledExecutorService cleaner;

    public CryptunnelHttpController(CryptunnelProperties properties,
                                   AuthenticatedSessionManager sessionManager,
                                   TcpTunnel tunnel,
                                   TargetRegistry targetRegistry) {
        this.properties = properties;
        this.sessionManager = sessionManager;
        this.tunnel = tunnel;
        this.targetRegistry = targetRegistry;
        if (sessionManager instanceof DefaultAuthenticatedSessionManager) {
            this.sessionManagerImpl = (DefaultAuthenticatedSessionManager) sessionManager;
        } else {
            this.sessionManagerImpl = null;
        }
    }

    @PostConstruct
    public void startCleaner() {
        cleaner = Executors.newSingleThreadScheduledExecutor(new ThreadFactory() {
            @Override
            public Thread newThread(Runnable r) {
                Thread t = new Thread(r, "cryptunnel-http-cleaner");
                // 守护线程：清理器绝不能阻止 JVM 退出
                t.setDaemon(true);
                return t;
            }
        });
        long periodMs = 60000L;
        cleaner.scheduleWithFixedDelay(new Runnable() {
            @Override
            public void run() {
                try {
                    cleanupStaleConnections();
                } catch (Throwable t) {
                    // 必须吞掉：scheduleWithFixedDelay 一旦任务抛异常就会永久停止后续执行，
                    // 那会让清理静默失效——正是本次修复要消灭的那类故障。
                    log.error("Cryptunnel HTTP: stale cleanup failed", t);
                }
            }
        }, periodMs, periodMs, TimeUnit.MILLISECONDS);
        log.info("Cryptunnel HTTP: stale session cleaner started, idleTimeout={}ms", idleTimeoutMs());
    }

    @PreDestroy
    public void stopCleaner() {
        if (cleaner != null) {
            cleaner.shutdownNow();
        }
        // 关停时释放所有在途会话，否则容器重启会在服务端遗留 MySQL 连接与计数
        for (String connId : httpSessions.keySet()) {
            closeQuiet(connId);
        }
    }

    @PostMapping("/connect")
    public ResponseEntity<String> connect(@RequestBody String body) {
        try {
            // 1) 逐 target 试解，找 target + 解析 AUTH
            TargetDiscovery.DecryptedAuth decrypted = TargetDiscovery.discover(targetRegistry, body);
            TargetDefinition target = decrypted.target();
            AuthMessage auth = decrypted.auth();

            // 2) 鉴权 + 路由 + 打开 MySQL
            AuthenticatedSession session = sessionManager.authenticate(auth);
            String connId = "HC-" + System.currentTimeMillis() + "-" + Integer.toHexString(session.mysql().hashCode());
            httpSessions.put(connId, new SessionContext(session.mysql(), target));

            // 3) 用该 target 的密钥加密 MySQL 握手包并返回
            SecretKeySpec aes = AesUtil.deriveAesKey(target.getAesKey());
            SecretKeySpec mac = AesUtil.deriveHmacKey(target.getAesKey());
            byte[] buf = new byte[8192];
            int n = MysqlPacketReader.readPacket(session.mysql().in(), buf, 5000);
            if (n <= 0) {
                closeQuiet(connId);
                return ResponseEntity.status(HttpStatus.INTERNAL_SERVER_ERROR).body("No MySQL handshake");
            }
            byte[] handshake = new byte[n];
            System.arraycopy(buf, 0, handshake, 0, n);
            String encryptedHs = AesUtil.encrypt(handshake, aes, mac);
            return ResponseEntity.ok(connId + ":" + encryptedHs);
        } catch (AuthFailedException | TargetCapacityExceededException e) {
            return ResponseEntity.status(HttpStatus.UNAUTHORIZED).body("Auth failed: " + e.getMessage());
        } catch (Exception e) {
            log.error("Cryptunnel HTTP: connect failed", e);
            return ResponseEntity.status(HttpStatus.INTERNAL_SERVER_ERROR).body("Connect failed: " + e.getMessage());
        }
    }

    @PostMapping("/tunnel")
    public ResponseEntity<String> tunnel(@RequestHeader("X-Conn-Id") String connId,
                                        @RequestBody String body) {
        SessionContext ctx = httpSessions.get(connId);
        if (ctx == null || !ctx.mysql.isOpen()) {
            return ResponseEntity.status(HttpStatus.NOT_FOUND).body("Connection not found");
        }
        // 先刷新再处理：慢查询可能挂满 30 秒，若等处理完再刷新，
        // 期间的清理轮次会把这条正在服务的会话当成空闲回收掉。
        ctx.touch();
        try {
            // 用会话绑定的 target 密钥解
            SecretKeySpec aes = AesUtil.deriveAesKey(ctx.target.getAesKey());
            SecretKeySpec mac = AesUtil.deriveHmacKey(ctx.target.getAesKey());
            byte[] clientData = AesUtil.decrypt(body, aes, mac);
            OutputStream out = ctx.mysql.out();
            out.write(clientData);
            out.flush();

            byte[] buf = new byte[65536];
            int n = MysqlPacketReader.readPacket(ctx.mysql.in(), buf, 30000);
            if (n <= 0) {
                closeQuiet(connId);
                return ResponseEntity.status(HttpStatus.GONE).body("MySQL connection closed");
            }
            byte[] resp = new byte[n];
            System.arraycopy(buf, 0, resp, 0, n);
            String encryptedResp = AesUtil.encrypt(resp, aes, mac);
            return ResponseEntity.ok(encryptedResp);
        } catch (IOException e) {
            log.error("Cryptunnel HTTP: tunnel IO error, connId={}", connId, e);
            closeQuiet(connId);
            return ResponseEntity.status(HttpStatus.INTERNAL_SERVER_ERROR).body("IO error");
        } catch (Exception e) {
            log.error("Cryptunnel HTTP: tunnel error, connId={}", connId, e);
            closeQuiet(connId);
            return ResponseEntity.status(HttpStatus.INTERNAL_SERVER_ERROR).body("Error: " + e.getMessage());
        }
    }

    @PostMapping("/disconnect")
    public ResponseEntity<String> disconnect(@RequestHeader("X-Conn-Id") String connId,
                                              @RequestBody String body) {
        try {
            TargetDiscovery.discover(targetRegistry, body);
        } catch (Exception e) {
            return ResponseEntity.status(HttpStatus.UNAUTHORIZED).body("Auth failed");
        }
        closeQuiet(connId);
        return ResponseEntity.ok("Disconnected");
    }

    /**
     * 清理陈旧会话。由私有调度器每分钟触发（见 {@link #startCleaner()}）。
     *
     * <p><b>两类陈旧</b>：</p>
     * <ol>
     *   <li><b>已断开</b>：{@code isOpen()} 为 false —— 原实现只清这一类。</li>
     *   <li><b>空闲超时</b>：socket 仍打开但长时间无请求。原实现完全遗漏，
     *       导致客户端进程被杀 / 网络中断时，服务端这一侧的 MySQL 连接
     *       与 per-target 计数<b>永久驻留</b>，直到应用重启。</li>
     * </ol>
     *
     * <p>HTTP 降级是无状态请求-响应模型，服务端无法感知客户端消失，
     * 空闲超时是唯一可靠的回收手段（WS 通道靠 close 事件，不需要它）。</p>
     */
    void cleanupStaleConnections() {
        long now = System.currentTimeMillis();
        long idleTimeout = idleTimeoutMs();
        for (Map.Entry<String, SessionContext> e : httpSessions.entrySet()) {
            SessionContext ctx = e.getValue();
            boolean closed = !ctx.mysql.isOpen();
            boolean idle = (now - ctx.lastAccess.get()) > idleTimeout;
            if (closed || idle) {
                if (idle && !closed) {
                    log.info("Cryptunnel HTTP: reclaiming idle session, connId={}, idleMs={}",
                            e.getKey(), now - ctx.lastAccess.get());
                }
                closeQuiet(e.getKey());
            }
        }
    }

    /**
     * 空闲回收阈值。取 readTimeout 的 2 倍并以 5 分钟兜底。
     *
     * <p>不直接用 readTimeout：单次 {@code /tunnel} 请求最长可挂 30 秒等 MySQL 响应，
     * 阈值若贴得太近，会在正常的慢查询期间误杀活跃会话。</p>
     */
    private long idleTimeoutMs() {
        long readTimeout = properties.getReadTimeout();
        long candidate = readTimeout * 2L;
        long floor = 300000L;
        return candidate > floor ? candidate : floor;
    }

    private void closeQuiet(String connId) {
        SessionContext ctx = httpSessions.remove(connId);
        if (ctx != null) {
            try {
                ctx.mysql.close();
            } catch (Exception ignored) {
            }
            // 与 WS 通道同一处理：per-target 计数必须与 socket 在同一分支释放。
            // 遗漏它会让 authenticate() 的 incrementAndGet 永远没有对应减法，
            // target 达到 maxConnections 后将永久拒绝新连接（重启才恢复）。
            if (sessionManagerImpl != null) {
                sessionManagerImpl.release(ctx.target.getName());
            }
        }
    }

    /** HTTP 会话绑定的 target 上下文。 */
    private static final class SessionContext {
        final MySqlConnection mysql;
        final TargetDefinition target;
        /** 最近一次请求时间，用于空闲回收。HTTP 无连接事件，只能靠它判活。 */
        final AtomicLong lastAccess = new AtomicLong(System.currentTimeMillis());

        SessionContext(MySqlConnection mysql, TargetDefinition target) {
            this.mysql = mysql;
            this.target = target;
        }

        void touch() {
            lastAccess.set(System.currentTimeMillis());
        }
    }
}
