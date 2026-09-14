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
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.Socket;
import java.util.Map;
import java.util.Set;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.atomic.AtomicInteger;
import javax.crypto.spec.SecretKeySpec;
import org.slf4j.Logger;
import org.slf4j.LoggerFactory;
import org.springframework.web.socket.BinaryMessage;
import org.springframework.web.socket.CloseStatus;
import org.springframework.web.socket.TextMessage;
import org.springframework.web.socket.WebSocketSession;
import org.springframework.web.socket.handler.AbstractWebSocketHandler;

/**
 * WebSocket Handler（javax.servlet 命名空间，Boot 2.x）。
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
        log.info("Cryptunnel: WS connection established, active={}", current);
    }

    @Override
    protected void handleTextMessage(WebSocketSession wsSession, TextMessage message) throws Exception {
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
        } catch (AuthFailedException | TargetCapacityExceededException e) {
            log.warn("Cryptunnel: auth failed, sessionId={}, reason={}", wsSession.getId(), e.getMessage());
            closeQuietly(wsSession, CloseStatus.NOT_ACCEPTABLE.withReason("Auth failed"));
            cleanup(wsSession);
        } catch (Exception e) {
            log.warn("Cryptunnel: auth decrypt/parse failed, sessionId={}", wsSession.getId(), e);
            closeQuietly(wsSession, CloseStatus.NOT_ACCEPTABLE.withReason("Auth failed"));
            cleanup(wsSession);
        }
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
        log.warn("Cryptunnel: binary message not supported");
    }

    @Override
    public void afterConnectionClosed(WebSocketSession wsSession, CloseStatus status) {
        log.info("Cryptunnel: WS closed, status={}", status);
        cleanup(wsSession);
    }

    @Override
    public void handleTransportError(WebSocketSession wsSession, Throwable exception) {
        log.error("Cryptunnel: WS transport error", exception);
        cleanup(wsSession);
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
