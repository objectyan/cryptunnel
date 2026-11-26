package io.github.objectyan.cryptunnel.core.tunnel;

import io.github.objectyan.cryptunnel.core.auth.AuthMessage;
import io.github.objectyan.cryptunnel.core.target.DefaultTargetRegistry;
import io.github.objectyan.cryptunnel.core.target.TargetDefinition;
import io.github.objectyan.cryptunnel.core.target.TargetRegistry;
import java.io.IOException;
import java.net.ServerSocket;
import java.net.Socket;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.Collections;
import java.util.List;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicInteger;
import java.util.concurrent.atomic.AtomicReference;
import org.junit.After;
import org.junit.Before;
import org.junit.Test;

import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertNotNull;
import static org.junit.Assert.fail;

/**
 * 「认证成功 ACK 必须早于目标库连接」的不变量（ADR-0005）。
 *
 * <p>背景：服务端原本不发 ACK，靠目标库首帧当认证信号。补了 ACK 之后如果仍把它
 * 放在 {@code tunnel.open()} 之后，跨网建连一慢，客户端就在认证层 10s 超时，
 * 真实故障（目标库连不上）被完全掩盖。这组测试用「故意卡住 open()」的方式把
 * 顺序钉死，而不是只测「最终有没有 ACK」。</p>
 */
public class EarlyAuthAckTest {

    private ServerSocket stubMysql;

    @Before
    public void setUp() throws IOException {
        stubMysql = new ServerSocket(0);
    }

    @After
    public void tearDown() throws IOException {
        stubMysql.close();
    }

    private TargetDefinition target(String name, int maxConnections) {
        return new TargetDefinition(
                name, name, "127.0.0.1", stubMysql.getLocalPort(),
                "aes-key-for-test-0123456789", "auth-key-for-test-0123456789",
                maxConnections, 10000, 0);
    }

    private DefaultAuthenticatedSessionManager managerFor(TargetDefinition t, TcpTunnel tunnel) {
        List<TargetDefinition> list = new ArrayList<TargetDefinition>();
        list.add(t);
        TargetRegistry registry = new DefaultTargetRegistry(list, t.getName());
        return new DefaultAuthenticatedSessionManager(
                registry, tunnel, new NonceCache(600_000L), 300L);
    }

    private AuthMessage auth(TargetDefinition t, String nonce) {
        return new AuthMessage(
                t.getAuthKey(), System.currentTimeMillis() / 1000L, nonce, t.getName());
    }

    /** open() 卡在 latch 上，直到测试显式放行——让「ACK 在前」成为可判定的事实。 */
    private static final class BlockingTunnel implements TcpTunnel {
        final CountDownLatch openEntered = new CountDownLatch(1);
        final CountDownLatch proceed = new CountDownLatch(1);
        final List<String> events = Collections.synchronizedList(new ArrayList<String>());
        private final int port;
        final AtomicInteger openCalls = new AtomicInteger();

        BlockingTunnel(int port) {
            this.port = port;
        }

        @Override
        public MySqlConnection open(TargetDefinition target) throws IOException {
            openCalls.incrementAndGet();
            events.add("open");
            openEntered.countDown();
            try {
                if (!proceed.await(5, TimeUnit.SECONDS)) {
                    throw new IOException("test timeout waiting for proceed");
                }
            } catch (InterruptedException e) {
                Thread.currentThread().interrupt();
                throw new IOException(e);
            }
            return new MySqlConnection(new Socket("127.0.0.1", port));
        }
    }

    @Test
    public void ackIsSentBeforeTargetConnect() throws Exception {
        TargetDefinition t = target("early-ack-a", 5);
        final BlockingTunnel tunnel = new BlockingTunnel(stubMysql.getLocalPort());
        final DefaultAuthenticatedSessionManager mgr = managerFor(t, tunnel);
        final AtomicInteger acks = new AtomicInteger();
        final AtomicInteger openCallsAtAck = new AtomicInteger(-1);
        Runnable ack = new Runnable() {
            @Override
            public void run() {
                // 在回调现场取样，避免「测试线程醒来时 worker 已进入 open」的竞态。
                openCallsAtAck.set(tunnel.openCalls.get());
                acks.incrementAndGet();
                tunnel.events.add("ack");
            }
        };

        final AuthMessage msg = auth(t, "nonce-a");
        final Runnable hook = ack;
        final AtomicReference<AuthenticatedSession> result = new AtomicReference<AuthenticatedSession>();
        final AtomicReference<Throwable> error = new AtomicReference<Throwable>();
        Thread worker = new Thread(new Runnable() {
            @Override
            public void run() {
                try {
                    result.set(mgr.authenticate(msg, hook));
                } catch (Throwable e) {
                    error.set(e);
                }
            }
        }, "early-ack-worker");
        worker.start();

        // open() 被卡住时 ACK 仍应到达；若实现把 ACK 放在 open 之后，这里必然超时。
        long deadline = System.currentTimeMillis() + 2000L;
        while (acks.get() == 0 && System.currentTimeMillis() < deadline) {
            Thread.sleep(5L);
        }
        assertEquals("目标库建连被卡住时，ACK 必须已经发出", 1, acks.get());
        assertEquals("ACK 触发的瞬间，目标库连接尚未开始", 0, openCallsAtAck.get());

        tunnel.proceed.countDown();
        worker.join(5000L);
        if (error.get() != null) {
            throw new AssertionError("authenticate 不应失败", error.get());
        }
        assertNotNull(result.get());
        assertEquals("顺序必须是 ACK → open", Arrays.asList("ack", "open"), tunnel.events);
        result.get().mysql().close();
        mgr.release(t.getName());
    }

    @Test
    public void invalidAuthKey_doesNotSendAck() throws Exception {
        TargetDefinition t = target("early-ack-b", 5);
        BlockingTunnel tunnel = new BlockingTunnel(stubMysql.getLocalPort());
        DefaultAuthenticatedSessionManager mgr = managerFor(t, tunnel);
        final AtomicInteger acks = new AtomicInteger();
        Runnable hook = new Runnable() {
            @Override
            public void run() {
                acks.incrementAndGet();
            }
        };
        AuthMessage bad = new AuthMessage("wrong-key",
                System.currentTimeMillis() / 1000L, "nonce-b", t.getName());

        try {
            mgr.authenticate(bad, hook);
            fail("错误 authKey 必须认证失败");
        } catch (AuthFailedException expected) {
            // 预期路径
        }
        assertEquals("认证失败绝不能提前 ACK", 0, acks.get());
        assertEquals("认证失败不应连接目标库", 0, tunnel.openCalls.get());
        assertEquals(0, mgr.currentConnections(t.getName()));
    }

    @Test
    public void replayedNonce_doesNotSendAck() throws Exception {
        TargetDefinition t = target("early-ack-c", 5);
        BlockingTunnel tunnel = new BlockingTunnel(stubMysql.getLocalPort());
        DefaultAuthenticatedSessionManager mgr = managerFor(t, tunnel);
        AuthMessage first = auth(t, "nonce-c");

        // 用真实的 open 完成第一次（放行 latch），占掉这个 nonce。
        tunnel.proceed.countDown();
        AuthenticatedSession ok = mgr.authenticate(first);
        ok.mysql().close();
        mgr.release(t.getName());

        final AtomicInteger acks = new AtomicInteger();
        Runnable hook = new Runnable() {
            @Override
            public void run() {
                acks.incrementAndGet();
            }
        };
        try {
            mgr.authenticate(first, hook);
            fail("重放 nonce 必须认证失败");
        } catch (AuthFailedException expected) {
            // 预期路径
        }
        assertEquals("重放攻击不得收到 ACK", 0, acks.get());
        assertEquals(0, mgr.currentConnections(t.getName()));
    }

    @Test
    public void capacityExceeded_doesNotSendAck() throws Exception {
        TargetDefinition t = target("early-ack-d", 1);
        BlockingTunnel tunnel = new BlockingTunnel(stubMysql.getLocalPort());
        tunnel.proceed.countDown();
        DefaultAuthenticatedSessionManager mgr = managerFor(t, tunnel);

        AuthenticatedSession occupying = mgr.authenticate(auth(t, "nonce-d0"));
        final AtomicInteger acks = new AtomicInteger();
        Runnable hook = new Runnable() {
            @Override
            public void run() {
                acks.incrementAndGet();
            }
        };
        try {
            mgr.authenticate(auth(t, "nonce-d1"), hook);
            fail("超过容量必须失败");
        } catch (TargetCapacityExceededException expected) {
            // 预期路径
        }
        assertEquals("容量超限不得收到「认证成功」ACK", 0, acks.get());
        assertEquals("被拒绝的请求不得触发目标库连接", 1, tunnel.openCalls.get());
        assertEquals(1, mgr.currentConnections(t.getName()));

        occupying.mysql().close();
        mgr.release(t.getName());
    }

    @Test
    public void callbackFailure_rollsBackCapacityAndSkipsConnect() throws Exception {
        TargetDefinition t = target("early-ack-e", 1);
        BlockingTunnel tunnel = new BlockingTunnel(stubMysql.getLocalPort());
        DefaultAuthenticatedSessionManager mgr = managerFor(t, tunnel);
        Runnable badHook = new Runnable() {
            @Override
            public void run() {
                throw new IllegalStateException("ack transport down");
            }
        };

        try {
            mgr.authenticate(auth(t, "nonce-e0"), badHook);
            fail("回调异常必须让认证失败");
        } catch (IllegalStateException expected) {
            // 预期路径
        }
        assertEquals("回调失败不得留下容量残留", 0, mgr.currentConnections(t.getName()));
        assertEquals("回调失败不得连接目标库", 0, tunnel.openCalls.get());

        // 槽位必须仍可用——否则就是静默泄漏。
        tunnel.proceed.countDown();
        AuthenticatedSession recovered = mgr.authenticate(auth(t, "nonce-e1"));
        assertNotNull(recovered);
        recovered.mysql().close();
        mgr.release(t.getName());
        assertEquals(0, mgr.currentConnections(t.getName()));
    }
}
