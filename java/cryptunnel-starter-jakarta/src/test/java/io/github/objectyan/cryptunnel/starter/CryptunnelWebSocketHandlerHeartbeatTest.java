package io.github.objectyan.cryptunnel.starter;

import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertFalse;
import static org.junit.Assert.assertTrue;
import static org.junit.Assert.fail;

import java.io.EOFException;
import java.io.IOException;
import java.net.SocketException;
import java.nio.channels.ClosedChannelException;
import java.util.concurrent.TimeUnit;
import java.util.function.BooleanSupplier;

import org.junit.After;
import org.junit.Before;
import org.junit.Test;
import org.springframework.web.socket.CloseStatus;
import org.springframework.web.socket.PingMessage;
import org.springframework.web.socket.PongMessage;

/**
 * WS 心跳 / 死链路回收 / 传输错误降级的测试。
 *
 * <p>背景：客户端进程退出、NAT 空闲回收、网络切换都会让 TCP 在没有 WS close 握手的
 * 情况下直落（服务端读线程抛 {@link EOFException}）。原实现把这种正常断开记成
 * ERROR + 全栈；且静默死链路（NAT 悄悄丢包、FIN 永远不来）会一直占着
 * {@code maxConnections} 槽位与 MySQL 连接，直到 TCP keepalive 超时（约 2 小时）。</p>
 */
public class CryptunnelWebSocketHandlerHeartbeatTest {

    private CryptunnelProperties properties;
    private CryptunnelWebSocketHandler handler;
    private FakeWebSocketSession session;

    @Before
    public void setUp() {
        properties = new CryptunnelProperties();
        // sessionManager / targetRegistry 在本测试中不会被触达（cleanup 对 null 安全）
        handler = new CryptunnelWebSocketHandler(properties, null, null);
        session = new FakeWebSocketSession("s-1");
    }

    @After
    public void tearDown() {
        handler.stopHeartbeat();
    }

    // ---------------------------------------------------------------- 传输错误分类

    @Test
    public void peerDisconnectClassification() {
        // 客户端直落 TCP 的三种典型形态
        assertTrue(CryptunnelWebSocketHandler.isPeerDisconnect(new EOFException()));
        assertTrue(CryptunnelWebSocketHandler.isPeerDisconnect(new SocketException("Connection reset")));
        assertTrue(CryptunnelWebSocketHandler.isPeerDisconnect(new IOException("Broken pipe")));
        assertTrue(CryptunnelWebSocketHandler.isPeerDisconnect(new IOException("Connection reset by peer")));
        assertTrue(CryptunnelWebSocketHandler.isPeerDisconnect(new ClosedChannelException()));
        // 被包装过的也要认出来（Spring/容器可能在外面再包一层）
        assertTrue(CryptunnelWebSocketHandler.isPeerDisconnect(new IOException("read failed", new EOFException())));
        // 真正的异常不能降级
        assertFalse(CryptunnelWebSocketHandler.isPeerDisconnect(new IOException("disk on fire")));
        assertFalse(CryptunnelWebSocketHandler.isPeerDisconnect(new IllegalStateException("boom")));
        assertFalse(CryptunnelWebSocketHandler.isPeerDisconnect(null));
    }

    // ---------------------------------------------------------------- 心跳

    @Test
    public void heartbeatPingsOpenSession() throws Exception {
        properties.setHeartbeatInterval(100);
        properties.setHeartbeatTimeout(0); // 本用例只验证发 Ping，不验证回收
        handler.startHeartbeat();
        handler.afterConnectionEstablished(session);

        await("PingMessage", () -> !session.sent.isEmpty());
        assertTrue("first heartbeat frame must be Ping", session.sent.get(0) instanceof PingMessage);
    }

    @Test
    public void heartbeatDisabledWhenIntervalZero() throws Exception {
        properties.setHeartbeatInterval(0);
        handler.startHeartbeat();
        handler.afterConnectionEstablished(session);

        TimeUnit.MILLISECONDS.sleep(400);
        assertTrue("interval=0 must disable heartbeat entirely", session.sent.isEmpty());
    }

    // ---------------------------------------------------------------- 死链路回收

    @Test
    public void silentSessionIsReaped() throws Exception {
        properties.setHeartbeatInterval(100);
        properties.setHeartbeatTimeout(250);
        handler.startHeartbeat();
        handler.afterConnectionEstablished(session);

        await("reap", () -> !session.closed.isEmpty());
        assertEquals(CloseStatus.SESSION_NOT_RELIABLE.getCode(), session.closed.get(0).getCode());
        assertFalse("reaped session must be closed", session.isOpen());
    }

    @Test
    public void responsiveSessionSurvivesReaping() throws Exception {
        properties.setHeartbeatInterval(100);
        properties.setHeartbeatTimeout(250);
        handler.startHeartbeat();
        handler.afterConnectionEstablished(session);

        // 模拟客户端对 Ping 的自动 Pong（tungstenite 收到 Ping 会自动回 Pong）。
        // 走 public 的 handleMessage 分发——handlePongMessage 是 protected，非同包子类调不到。
        long deadline = System.currentTimeMillis() + 1200;
        while (System.currentTimeMillis() < deadline) {
            handler.handleMessage(session, new PongMessage());
            TimeUnit.MILLISECONDS.sleep(50);
        }
        assertTrue("responsive session must not be reaped", session.closed.isEmpty());
    }

    @Test
    public void sendFailureReleasesSession() throws Exception {
        properties.setHeartbeatInterval(100);
        properties.setHeartbeatTimeout(0);
        session.failSends = true; // 发送即抛 IOException = 链路已死
        handler.startHeartbeat();
        handler.afterConnectionEstablished(session);

        await("first send attempt", () -> session.sendAttempts.get() >= 1);
        // cleanup 后条目被摘除，后续 tick 不应再尝试发送
        TimeUnit.MILLISECONDS.sleep(400);
        assertEquals("failed session must be released after one attempt", 1, session.sendAttempts.get());
    }

    // ---------------------------------------------------------------- 工具

    private static void await(String what, BooleanSupplier condition) throws InterruptedException {
        long deadline = System.currentTimeMillis() + 3000;
        while (System.currentTimeMillis() < deadline) {
            if (condition.getAsBoolean()) {
                return;
            }
            TimeUnit.MILLISECONDS.sleep(20);
        }
        fail("timeout waiting for: " + what);
    }
}
