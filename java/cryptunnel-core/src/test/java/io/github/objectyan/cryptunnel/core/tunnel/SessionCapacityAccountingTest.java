package io.github.objectyan.cryptunnel.core.tunnel;

import io.github.objectyan.cryptunnel.core.auth.AuthMessage;
import io.github.objectyan.cryptunnel.core.target.DefaultTargetRegistry;
import io.github.objectyan.cryptunnel.core.target.TargetDefinition;
import io.github.objectyan.cryptunnel.core.target.TargetRegistry;
import java.io.IOException;
import java.net.ServerSocket;
import java.net.Socket;
import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.atomic.AtomicInteger;
import org.junit.After;
import org.junit.Before;
import org.junit.Test;

import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertNotNull;
import static org.junit.Assert.fail;

/**
 * per-target 连接计数的<b>增减配对</b>不变量。
 *
 * <p><b>这组测试的由来</b>：{@code DefaultAuthenticatedSessionManager.release()}
 * 曾经在整个代码库中<b>零调用方</b>——{@code authenticate()} 每次成功都
 * {@code incrementAndGet}，但没有任何路径调用 {@code release()}。
 * 后果是每个 target 的计数只增不减，达到 {@code maxConnections} 后
 * <b>永久拒绝</b>新连接，只有重启应用才能恢复。</p>
 *
 * <p>更隐蔽的是它<b>无法被普通功能测试发现</b>：单次连接、甚至连续几次连接都完全正常，
 * 只有累计次数越过阈值后才突然全面失效，且此时故障现象（连不上）
 * 与真实原因（计数泄漏）毫无字面关联。</p>
 *
 * <p>因此这里直接把「配对」本身钉成断言，而不是只测某次连接能否成功。</p>
 */
public class SessionCapacityAccountingTest {

    /** 本地回环监听，用来产生真实可用的 Socket——比 mock 更贴近实际行为。 */
    private ServerSocket stubMysql;
    private final List<Socket> accepted = new ArrayList<Socket>();
    private Thread acceptor;

    @Before
    public void setUp() throws IOException {
        stubMysql = new ServerSocket(0);
        acceptor = new Thread(new Runnable() {
            @Override
            public void run() {
                while (!stubMysql.isClosed()) {
                    try {
                        synchronized (accepted) {
                            accepted.add(stubMysql.accept());
                        }
                    } catch (IOException e) {
                        return; // 关闭时退出
                    }
                }
            }
        }, "stub-mysql-acceptor");
        acceptor.setDaemon(true);
        acceptor.start();
    }

    @After
    public void tearDown() throws IOException {
        stubMysql.close();
        synchronized (accepted) {
            for (Socket s : accepted) {
                try {
                    s.close();
                } catch (IOException ignored) {
                }
            }
        }
    }

    private TargetDefinition target(String name, int maxConnections) {
        return new TargetDefinition(
                name, name, "127.0.0.1", stubMysql.getLocalPort(),
                "aes-key-for-test-0123456789", "auth-key-for-test-0123456789",
                maxConnections, 10000, 0);
    }

    private DefaultAuthenticatedSessionManager managerFor(TargetDefinition... defs) {
        List<TargetDefinition> list = new ArrayList<TargetDefinition>();
        for (TargetDefinition d : defs) {
            list.add(d);
        }
        TargetRegistry registry = new DefaultTargetRegistry(list, defs[0].getName());
        return new DefaultAuthenticatedSessionManager(
                registry, new DefaultTcpTunnel(), new NonceCache(600_000L), 300L);
    }

    /** nonce 必须每次不同，否则会被重放保护拦下，测不到计数逻辑。 */
    private AuthMessage auth(TargetDefinition t, int seq) {
        return new AuthMessage(
                t.getAuthKey(), System.currentTimeMillis() / 1000L, "nonce-" + seq, t.getName());
    }

    /**
     * 核心不变量：认证 N 次、释放 N 次之后，计数必须精确回到 0。
     *
     * <p>这是 {@code release()} 零调用缺陷的直接探针。</p>
     */
    @Test
    public void authenticateThenRelease_returnsCountToZero() throws IOException {
        TargetDefinition t = target("acct-a", 5);
        DefaultAuthenticatedSessionManager mgr = managerFor(t);

        assertEquals("初始计数应为 0", 0, mgr.currentConnections("acct-a"));

        List<AuthenticatedSession> sessions = new ArrayList<AuthenticatedSession>();
        for (int i = 0; i < 5; i++) {
            sessions.add(mgr.authenticate(auth(t, i)));
        }
        assertEquals("5 次认证后计数应为 5", 5, mgr.currentConnections("acct-a"));

        for (int i = 0; i < 5; i++) {
            sessions.get(i).mysql().close();
            mgr.release("acct-a");
        }
        assertEquals("全部释放后计数必须归零（release 泄漏会让它停在 5）",
                0, mgr.currentConnections("acct-a"));
    }

    /**
     * 回归测试：连接建立并释放后，容量必须可被重新使用。
     *
     * <p>这精确复现了泄漏时的用户可见症状——DBeaver 反复开关连接若干次后，
     * 明明没有活跃连接却再也连不上，重启服务端才恢复。</p>
     */
    @Test
    public void capacityIsReusableAfterRelease() throws IOException {
        TargetDefinition t = target("acct-b", 2);
        DefaultAuthenticatedSessionManager mgr = managerFor(t);

        // 反复用满再释放 10 轮：泄漏存在时，第 2 轮就会撞上限
        for (int round = 0; round < 10; round++) {
            AuthenticatedSession s1 = mgr.authenticate(auth(t, round * 2));
            AuthenticatedSession s2 = mgr.authenticate(auth(t, round * 2 + 1));
            assertNotNull(s1);
            assertNotNull(s2);

            s1.mysql().close();
            mgr.release("acct-b");
            s2.mysql().close();
            mgr.release("acct-b");

            assertEquals("第 " + round + " 轮结束后计数应归零",
                    0, mgr.currentConnections("acct-b"));
        }
    }

    /** 超过上限必须抛出容量异常，且失败的那次不得留下计数残留。 */
    @Test
    public void exceedingCapacity_throwsAndLeavesNoResidue() throws IOException {
        TargetDefinition t = target("acct-c", 1);
        DefaultAuthenticatedSessionManager mgr = managerFor(t);

        AuthenticatedSession ok = mgr.authenticate(auth(t, 0));
        assertNotNull(ok);
        assertEquals(1, mgr.currentConnections("acct-c"));

        try {
            mgr.authenticate(auth(t, 1));
            fail("超过 maxConnections 应抛 TargetCapacityExceededException");
        } catch (TargetCapacityExceededException expected) {
            // 预期路径
        }

        assertEquals("被拒绝的那次不能留下计数残留", 1, mgr.currentConnections("acct-c"));

        ok.mysql().close();
        mgr.release("acct-c");
        assertEquals(0, mgr.currentConnections("acct-c"));

        // 释放后必须能重新连上，证明拒绝没有污染计数
        AuthenticatedSession again = mgr.authenticate(auth(t, 2));
        assertNotNull("释放后应能重新建立连接", again);
        again.mysql().close();
        mgr.release("acct-c");
    }

    /** 计数必须按 target 隔离，一个 target 用满不得影响另一个。 */
    @Test
    public void countsAreIsolatedPerTarget() throws IOException {
        TargetDefinition a = target("acct-d", 1);
        TargetDefinition b = target("acct-e", 1);
        DefaultAuthenticatedSessionManager mgr = managerFor(a, b);

        AuthenticatedSession sa = mgr.authenticate(auth(a, 0));
        assertEquals(1, mgr.currentConnections("acct-d"));
        assertEquals("另一 target 不应受影响", 0, mgr.currentConnections("acct-e"));

        AuthenticatedSession sb = mgr.authenticate(auth(b, 1));
        assertEquals(1, mgr.currentConnections("acct-e"));

        sa.mysql().close();
        mgr.release("acct-d");
        assertEquals(0, mgr.currentConnections("acct-d"));
        assertEquals("释放 A 不得影响 B", 1, mgr.currentConnections("acct-e"));

        sb.mysql().close();
        mgr.release("acct-e");
    }

    /**
     * 并发下的配对：多线程各自认证再释放，最终仍须归零。
     *
     * <p>计数用的是 {@code AtomicInteger}，但「增了却没减」这类缺陷
     * 在并发下同样成立，因此配对性需要独立于线程安全被验证。</p>
     */
    @Test
    public void concurrentAuthenticateAndRelease_balancesToZero() throws Exception {
        final TargetDefinition t = target("acct-f", 50);
        final DefaultAuthenticatedSessionManager mgr = managerFor(t);
        final AtomicInteger nonceSeq = new AtomicInteger();
        final AtomicInteger failures = new AtomicInteger();

        int threads = 8;
        int perThread = 10;
        List<Thread> workers = new ArrayList<Thread>();
        for (int i = 0; i < threads; i++) {
            Thread w = new Thread(new Runnable() {
                @Override
                public void run() {
                    for (int j = 0; j < 10; j++) {
                        try {
                            AuthenticatedSession s =
                                    mgr.authenticate(auth(t, nonceSeq.incrementAndGet()));
                            s.mysql().close();
                            mgr.release(t.getName());
                        } catch (Exception e) {
                            failures.incrementAndGet();
                        }
                    }
                }
            });
            workers.add(w);
            w.start();
        }
        for (Thread w : workers) {
            w.join(30_000L);
        }

        assertEquals("并发过程中不应有认证失败", 0, failures.get());
        assertEquals("共 " + (threads * perThread) + " 次增减，最终必须归零",
                0, mgr.currentConnections("acct-f"));
    }
}
