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
import io.github.objectyan.cryptunnel.core.tunnel.TargetCapacityExceededException;
import jakarta.annotation.PostConstruct;
import jakarta.annotation.PreDestroy;
import java.io.EOFException;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.Socket;
import java.nio.channels.ClosedChannelException;
import java.util.Locale;
import java.util.Map;
import java.util.Set;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.Executors;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.ThreadFactory;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicInteger;
import javax.crypto.spec.SecretKeySpec;
import org.slf4j.Logger;
import org.slf4j.LoggerFactory;
import org.springframework.web.socket.BinaryMessage;
import org.springframework.web.socket.CloseStatus;
import org.springframework.web.socket.PingMessage;
import org.springframework.web.socket.PongMessage;
import org.springframework.web.socket.TextMessage;
import org.springframework.web.socket.WebSocketSession;
import org.springframework.web.socket.handler.AbstractWebSocketHandler;

/**
 * WebSocket Handler（jakarta 命名空间，Boot 3.x / 4.x）。
 *
 * <p>工作流：</p>
 * <ol>
 *   <li>首条消息：加密的 AUTH 报文 → {@link TargetDiscovery#discover} 逐 target 试解
 *       → 找到对应 target + 解出 AuthMessage → 鉴权</li>
 *   <li>鉴权通过后打开 target MySQL TCP，启动 reader 线程</li>
 *   <li>后续消息：加密的 MySQL 协议字节 → 用该 target 密钥解 → 转发</li>
 *   <li>MySQL → WS：reader 线程读取 → 用该 target 密钥加密 → WS send</li>
 * </ol>
 *
 * <p><b>关于「服务端怎么知道该用哪把 key 解密」</b>：协议不暴露 targetId 明文，
 * 服务端用 try-each-target + HMAC 校验的方式定位 target（O(N)，N 通常为个位数）。</p>
 */
public class CryptunnelWebSocketHandler extends AbstractWebSocketHandler {

    private static final Logger log = LoggerFactory.getLogger(CryptunnelWebSocketHandler.class);

    private final CryptunnelProperties properties;
    private final AuthenticatedSessionManager sessionManager;
    private final DefaultAuthenticatedSessionManager sessionManagerImpl;
    private final TargetRegistry targetRegistry;
    private final AtomicInteger activeConnections = new AtomicInteger(0);
    private final Map<String, SessionContext> sessions = new ConcurrentHashMap<String, SessionContext>();

    /**
     * 已计入 {@link #activeConnections} 的会话 id。
     *
     * <p><b>为什么需要它</b>：释放路径有 4 个入口（写失败 / reader 线程结束 /
     * WS 关闭回调 / 传输错误），且互不排斥——一次断开通常会触发 2～3 次。
     * 若直接在释放方法里无条件 {@code decrementAndGet()}，计数会单调变负，
     * 使 {@code afterConnectionEstablished} 的 {@code current > maxConnections}
     * 判断永远为假，连接数上限彻底失效。</p>
     *
     * <p>这里把「已计数」变成一个可原子摘除的凭证：{@code remove()} 返回 true 的
     * 那一次才递减，从结构上保证增减配对，而不依赖调用方自觉只调一次。</p>
     */
    private final Set<String> admitted = ConcurrentHashMap.newKeySet();

    /**
     * 心跳跟踪表：sessionId → 会话 + 最近入站帧时间。
     *
     * <p>覆盖「已建立但未认证」的合法中间态——这类会话没有 {@link #sessions} 条目，
     * 但同样占着 {@code maxConnections} 槽位，半开连接（连上就消失的扫描器）
     * 必须靠心跳超时回收。</p>
     */
    private final Map<String, HeartbeatEntry> heartbeats = new ConcurrentHashMap<String, HeartbeatEntry>();

    private ScheduledExecutorService heartbeatScheduler;

    public CryptunnelWebSocketHandler(CryptunnelProperties properties,
                                     AuthenticatedSessionManager sessionManager,
                                     TargetRegistry targetRegistry) {
        this.properties = properties;
        this.sessionManager = sessionManager;
        this.targetRegistry = targetRegistry;
        if (sessionManager instanceof DefaultAuthenticatedSessionManager) {
            this.sessionManagerImpl = (DefaultAuthenticatedSessionManager) sessionManager;
        } else {
            this.sessionManagerImpl = null;
        }
    }

    // ============================================================================
    // 心跳与死链路回收
    // ============================================================================

    /**
     * 启动私有心跳调度器（{@code @PostConstruct}，随 Bean 创建自动触发）。
     *
     * <p><b>为什么自建而不用 {@code @Scheduled}</b>：与
     * {@link CryptunnelHttpController#startCleaner()} 同理——{@code @EnableScheduling}
     * 是<b>全局开关</b>，会把整个宿主应用的定时任务机制打开。使用方只是引入一个
     * Cryptunnel starter，不该因此改变自身应用的调度行为。</p>
     */
    @PostConstruct
    public void startHeartbeat() {
        int interval = properties.getHeartbeatInterval();
        if (interval <= 0) {
            log.info("Cryptunnel: WS heartbeat disabled (heartbeatInterval=0)");
            return;
        }
        heartbeatScheduler = Executors.newSingleThreadScheduledExecutor(new ThreadFactory() {
            @Override
            public Thread newThread(Runnable r) {
                Thread t = new Thread(r, "cryptunnel-ws-heartbeat");
                // 守护线程：心跳绝不能阻止 JVM 退出
                t.setDaemon(true);
                return t;
            }
        });
        heartbeatScheduler.scheduleWithFixedDelay(new Runnable() {
            @Override
            public void run() {
                try {
                    heartbeatTick();
                } catch (Throwable t) {
                    // 必须吞掉：scheduleWithFixedDelay 一旦任务抛异常就会永久停止后续执行，
                    // 那会让心跳静默失效——正是本次修复要消灭的那类故障。
                    log.error("Cryptunnel: WS heartbeat tick failed", t);
                }
            }
        }, interval, interval, TimeUnit.MILLISECONDS);
        log.info("Cryptunnel: WS heartbeat started, interval={}ms, timeout={}ms",
                interval, properties.getHeartbeatTimeout());
    }

    /**
     * 停止心跳并释放所有在途会话（{@code @PreDestroy}）。
     *
     * <p>与 {@link CryptunnelHttpController#stopCleaner()} 同样的防御：
     * 容器关停时不能遗留 MySQL 连接与连接计数。</p>
     */
    @PreDestroy
    public void stopHeartbeat() {
        if (heartbeatScheduler != null) {
            heartbeatScheduler.shutdownNow();
        }
        for (String sessionId : heartbeats.keySet()) {
            HeartbeatEntry entry = heartbeats.get(sessionId);
            if (entry != null) {
                closeQuietly(entry.session, CloseStatus.SERVICE_RESTARTED.withReason("Server shutting down"));
            }
        }
        heartbeats.clear();
    }

    /**
     * 单个心跳周期：先回收死链路，再给存活会话发 Ping。
     *
     * <p><b>顺序有讲究</b>：Ping 发送在客户端不读的极端情况下会阻塞（TCP 背压），
     * 先做回收可保证其他会话的回收决策不被这一次阻塞拖掉——心跳线程被挂住时，
     * 后果是所有会话一起失去心跳，而不仅是被挂住的那一个。</p>
     */
    private void heartbeatTick() {
        long now = System.currentTimeMillis();
        long timeout = properties.getHeartbeatTimeout();

        // pass 1：回收静默会话
        if (timeout > 0) {
            for (HeartbeatEntry entry : heartbeats.values()) {
                WebSocketSession wsSession = entry.session;
                long silentMs = now - entry.lastSeen;
                if (silentMs > timeout) {
                    log.warn("Cryptunnel: WS session silent for {}ms > heartbeatTimeout={}ms, reaping, sessionId={}",
                            silentMs, timeout, wsSession.getId());
                    closeQuietly(wsSession, CloseStatus.SESSION_NOT_RELIABLE.withReason("Heartbeat timeout"));
                    cleanup(wsSession);
                }
            }
        }

        // pass 2：给存活会话发 Ping
        for (HeartbeatEntry entry : heartbeats.values()) {
            WebSocketSession wsSession = entry.session;
            if (!wsSession.isOpen()) {
                continue; // 关闭回调会走 cleanup 摘除；这里防呆，避免向已关会话写
            }
            try {
                // 与 reader 线程的发送保持同一把锁：并发写同一会话会帧交错，
                // 直接破坏 WebSocket 帧边界
                synchronized (wsSession) {
                    if (wsSession.isOpen()) {
                        wsSession.sendMessage(new PingMessage());
                    }
                }
            } catch (IOException e) {
                // 发送失败 = 链路已死（FIN 可能还没到，但写已经不通）。
                // 走统一出口释放，不依赖 TCP keepalive 慢速探知。
                log.warn("Cryptunnel: WS heartbeat send failed, sessionId={}", wsSession.getId(), e);
                cleanup(wsSession);
            } catch (RuntimeException e) {
                // 容器在关闭竞态下可能抛 IllegalStateException。单个会话出错不应中断
                // 整轮心跳——否则一个坏会话会让所有会话一起失去保活。
                log.warn("Cryptunnel: WS heartbeat send error, sessionId={}", wsSession.getId(), e);
            }
        }
    }

    /** 记录入站帧时间。任何入站帧（数据 / Pong）都是存活证据。 */
    private void touch(WebSocketSession wsSession) {
        HeartbeatEntry entry = heartbeats.get(wsSession.getId());
        if (entry != null) {
            entry.lastSeen = System.currentTimeMillis();
        }
    }

    /** 心跳跟踪条目。 */
    private static final class HeartbeatEntry {
        final WebSocketSession session;
        volatile long lastSeen = System.currentTimeMillis();

        HeartbeatEntry(WebSocketSession session) {
            this.session = session;
        }
    }

    @Override
    public void afterConnectionEstablished(WebSocketSession wsSession) {
        int current = activeConnections.incrementAndGet();
        if (current > properties.getMaxConnections()) {
            activeConnections.decrementAndGet();
            log.warn("Cryptunnel: WS connection exceeds maxConnections={}", properties.getMaxConnections());
            closeQuietly(wsSession, CloseStatus.NOT_ACCEPTABLE.withReason("Max connections exceeded"));
            return;
        }
        // 计数成功后立刻登记凭证：此后任意释放路径都靠 admitted.remove() 决定是否递减。
        // 注意顺序——先 increment 再登记，若反过来，超限退出的分支会留下一个孤儿凭证。
        admitted.add(wsSession.getId());
        heartbeats.put(wsSession.getId(), new HeartbeatEntry(wsSession));
        log.info("Cryptunnel: WS connection established, active={}", current);
    }

    @Override
    protected void handleTextMessage(WebSocketSession wsSession, TextMessage message) throws Exception {
        touch(wsSession);
        String payload = message.getPayload();

        SessionContext ctx = sessions.get(wsSession.getId());
        if (ctx == null) {
            handleAuth(wsSession, payload);
            return;
        }

        handleData(wsSession, payload, ctx);
    }

    private void handleAuth(WebSocketSession wsSession, String encryptedAuth) {
        try {
            // 1) 逐 target 试解，找到正确的 target + 解析 AUTH 报文
            TargetDiscovery.DecryptedAuth decrypted = TargetDiscovery.discover(targetRegistry, encryptedAuth);
            TargetDefinition target = decrypted.target();
            AuthMessage auth = decrypted.auth();

            // 2) 鉴权 + 路由 + 打开 MySQL
            AuthenticatedSession session = sessionManager.authenticate(auth);

            // 3) 记录会话绑定的 target（后续数据通道用此 target 的密钥）
            sessions.put(wsSession.getId(), new SessionContext(session.mysql(), target));

            // 4) 启动 reader 线程
            final TargetDefinition boundTarget = target;
            final AuthenticatedSession boundSession = session;
            Thread reader = new Thread(new Runnable() {
                @Override
                public void run() {
                    mysqlToWsReader(wsSession, boundTarget, boundSession);
                }
            }, "cryptunnel-reader-" + wsSession.getId());
            reader.setDaemon(true);
            reader.start();

            log.info("Cryptunnel: auth OK, target={}, sessionId={}", target.getName(), wsSession.getId());
        } catch (Exception e) {
            handleAuthFailure(wsSession, e);
        }
    }

    /**
     * 认证阶段失败的<b>唯一收口</b>：按异常类型给出可区分的 close reason。
     *
     * <p><b>为什么容量超限必须单独处理</b>：容量超限与密钥错误在客户端是两种相反的处置——
     * 前者「稍后重试 / 关掉空闲会话 / 调大 maxConnections」，后者「去改配置」。
     * 原先两者共用 {@code Auth failed}，用户看到「认证失败」就去核对密钥，
     * 而真正原因是服务端槽位已满。这也是契约 §6 的规定：容量超限应为
     * {@code SERVICE_UNAVAILABLE}（1013）——Spring 的常量名是
     * {@link CloseStatus#SERVICE_OVERLOAD}，两者同码。</p>
     */
    void handleAuthFailure(WebSocketSession wsSession, Exception e) {
        if (e instanceof TargetCapacityExceededException) {
            TargetCapacityExceededException cap = (TargetCapacityExceededException) e;
            log.warn("Cryptunnel: target capacity exceeded, target={}, sessionId={}",
                    cap.getTargetId(), wsSession.getId());
            // 复用全局上限的 reason：客户端已把它映射为「连接数已达上限，可重试」的中文提示
            closeQuietly(wsSession, CloseStatus.SERVICE_OVERLOAD.withReason("Max connections exceeded"));
        } else if (e instanceof AuthFailedException) {
            log.warn("Cryptunnel: auth failed, sessionId={}, reason={}",
                    wsSession.getId(), ((AuthFailedException) e).getReason());
            closeQuietly(wsSession, CloseStatus.NOT_ACCEPTABLE.withReason("Auth failed"));
        } else {
            log.warn("Cryptunnel: auth decrypt/parse failed, sessionId={}", wsSession.getId(), e);
            closeQuietly(wsSession, CloseStatus.NOT_ACCEPTABLE.withReason("Auth failed"));
        }
        cleanup(wsSession);
    }

    private void handleData(WebSocketSession wsSession, String payload, SessionContext ctx) {
        try {
            SecretKeySpec aes = AesUtil.deriveAesKey(ctx.target.getAesKey());
            SecretKeySpec mac = AesUtil.deriveHmacKey(ctx.target.getAesKey());
            byte[] plain = AesUtil.decrypt(payload, aes, mac);
            OutputStream out = ctx.mysql.out();
            out.write(plain);
            out.flush();
        } catch (Exception e) {
            log.error("Cryptunnel: write to MySQL failed, sessionId={}", wsSession.getId(), e);
            cleanup(wsSession);
        }
    }

    private void mysqlToWsReader(WebSocketSession wsSession, TargetDefinition target, AuthenticatedSession session) {
        Socket socket = session.mysql().socket();
        try {
            InputStream in = socket.getInputStream();
            byte[] buffer = new byte[8192];
            while (!Thread.currentThread().isInterrupted()
                    && socket.isConnected()
                    && !socket.isInputShutdown()
                    && wsSession.isOpen()) {
                int n = in.read(buffer);
                if (n == -1) {
                    break;
                }
                if (n > 0) {
                    byte[] data = new byte[n];
                    System.arraycopy(buffer, 0, data, 0, n);
                    SecretKeySpec aes = AesUtil.deriveAesKey(target.getAesKey());
                    SecretKeySpec mac = AesUtil.deriveHmacKey(target.getAesKey());
                    String enc = AesUtil.encrypt(data, aes, mac);
                    synchronized (wsSession) {
                        if (wsSession.isOpen()) {
                            wsSession.sendMessage(new TextMessage(enc));
                        }
                    }
                }
            }
        } catch (Exception e) {
            if (!Thread.currentThread().isInterrupted()) {
                log.error("Cryptunnel: reader error, sessionId={}", wsSession.getId(), e);
            }
        } finally {
            closeQuietly(wsSession, CloseStatus.SERVER_ERROR.withReason("MySQL connection closed"));
            cleanup(wsSession);
        }
    }

    @Override
    protected void handleBinaryMessage(WebSocketSession wsSession, BinaryMessage message) {
        touch(wsSession);
        log.warn("Cryptunnel: binary message not supported");
    }

    @Override
    protected void handlePongMessage(WebSocketSession wsSession, PongMessage message) throws Exception {
        // Pong 是客户端对心跳的应答——最重要的存活证据，必须刷新静默计时
        touch(wsSession);
    }

    @Override
    public void afterConnectionClosed(WebSocketSession wsSession, CloseStatus status) {
        log.info("Cryptunnel: WS closed, status={}", status);
        cleanup(wsSession);
    }

    @Override
    public void handleTransportError(WebSocketSession wsSession, Throwable exception) {
        if (isPeerDisconnect(exception)) {
            // 客户端进程退出 / 网络中断 / NAT 回收时，TCP 直落而没有 WS close 握手，
            // Tomcat 读线程抛 EOFException。这是断开的正常形态而非故障——
            // 记 ERROR + 全栈会让每一次正常断开都像事故（本次修复要消灭的噪音）。
            log.info("Cryptunnel: WS peer disconnected without close handshake, sessionId={}",
                    wsSession.getId());
        } else {
            log.error("Cryptunnel: WS transport error, sessionId={}", wsSession.getId(), exception);
        }
        cleanup(wsSession);
    }

    /**
     * 判断传输错误是否属于「对端直落 TCP」的正常断开形态。
     *
     * <p>判定依据是 Tomcat NIO 读线程在这类场景下的稳定表现：EOFException（FIN）、
     * ClosedChannelException、以及消息含 connection reset / broken pipe 的
     * IOException（RST）。同时穿透 cause 链——容器可能在外面再包一层。</p>
     */
    static boolean isPeerDisconnect(Throwable t) {
        for (Throwable cur = t; cur != null; cur = cur.getCause()) {
            if (cur instanceof EOFException || cur instanceof ClosedChannelException) {
                return true;
            }
            String msg = cur.getMessage();
            if (msg != null) {
                String lower = msg.toLowerCase(Locale.ROOT);
                if (lower.contains("connection reset")
                        || lower.contains("broken pipe")
                        || lower.contains("connection aborted")) {
                    return true;
                }
            }
            if (cur.getCause() == cur) {
                break; // 防御自引用 cause 的死循环
            }
        }
        return false;
    }

    /**
     * 会话释放的<b>唯一出口</b>。四个调用点（写失败 / reader 线程结束 / WS 关闭回调 /
     * 传输错误）全部收敛到这里，靠两个原子摘除操作保证幂等。
     *
     * <p><b>为什么必须单一出口</b>：此前 socket 关闭、全局计数、per-target 计数
     * 三件事散落在不同位置、判断条件各不相同，导致两个计数器朝相反方向漂移
     * （全局计数变负使连接上限失效；per-target 只增不减使 target 永久不可用）。
     * 收敛后，「计数增减必须配对」由结构保证，而不依赖后续维护者记得配对。</p>
     *
     * <p>新增任何清理路径时，直接调用本方法即可，不要自行递减计数。</p>
     */
    private void cleanup(WebSocketSession wsSession) {
        String sessionId = wsSession.getId();

        // 0) 摘除心跳跟踪：断开后不应再计入静默时长，也不再接收 Ping
        heartbeats.remove(sessionId);

        // 1) 摘除会话：remove 返回非 null 的那一次才真正持有 socket 与 target
        SessionContext ctx = sessions.remove(sessionId);
        if (ctx != null) {
            try {
                ctx.mysql.close();
            } catch (Exception ignored) {
            }
            // per-target 计数与 socket 绑定同一事实，必须在同一分支内释放，
            // 否则 authenticate() 里的 incrementAndGet 将永远没有对应的减法。
            if (sessionManagerImpl != null) {
                sessionManagerImpl.release(ctx.target.getName());
            }
        }

        // 2) 摘除准入凭证：与会话分开判断，因为「已计数但未认证」是合法中间态
        //    （连接已建立、AUTH 报文尚未到达时断开），此时 ctx 为 null 但计数需回退。
        if (admitted.remove(sessionId)) {
            activeConnections.decrementAndGet();
        }
    }

    private void closeQuietly(WebSocketSession wsSession, CloseStatus status) {
        try {
            if (wsSession.isOpen()) {
                wsSession.close(status);
            }
        } catch (IOException ignored) {
        }
    }

    /** WS 会话绑定的 target 上下文（数据通道用此 target 的密钥加解密）。 */
    private static final class SessionContext {
        final MySqlConnection mysql;
        final TargetDefinition target;

        SessionContext(MySqlConnection mysql, TargetDefinition target) {
            this.mysql = mysql;
            this.target = target;
        }
    }
}
