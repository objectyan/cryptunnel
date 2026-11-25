package io.github.objectyan.cryptunnel.core.tunnel;

import org.junit.Test;

import java.io.IOException;
import java.net.SocketException;

import static org.junit.Assert.assertFalse;
import static org.junit.Assert.assertTrue;

/**
 * {@link ReaderShutdown} 的判定测试。
 *
 * <p>断言的两个方向都要有：既锁住「收尾噪音判为噪音」，也锁住「真故障不得被降级」——
 * 后者尤其重要，降级逻辑一旦放得太宽，真故障会被静默成 INFO。</p>
 */
public class ReaderShutdownTest {

    @Test
    public void socketException_afterSocketClosed_isNoise() {
        // 客户端先断开 → cleanup 关掉 MySQL socket → 阻塞中的 read 抛 Socket closed
        assertTrue(ReaderShutdown.isNoise(new SocketException("Socket closed"), true, true));
    }

    @Test
    public void socketException_afterSessionClosed_isNoise() {
        // WS 已关闭但 socket 尚未观察到关闭（关闭顺序的另一形态）
        assertTrue(ReaderShutdown.isNoise(new SocketException("Socket closed"), false, false));
    }

    @Test
    public void socketException_withBothAlive_isRealFailure() {
        // MySQL 侧连接被重置：socket 仍开着、会话也还活着 → 必须留 ERROR
        assertFalse(ReaderShutdown.isNoise(new SocketException("Connection reset"), false, true));
    }

    @Test
    public void nonSocketException_isRealFailure() {
        assertFalse(ReaderShutdown.isNoise(new IOException("boom"), true, false));
        assertFalse(ReaderShutdown.isNoise(new IllegalStateException("boom"), true, false));
    }
}
