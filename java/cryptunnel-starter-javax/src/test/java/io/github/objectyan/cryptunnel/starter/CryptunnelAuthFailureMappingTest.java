package io.github.objectyan.cryptunnel.starter;

import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertFalse;

import io.github.objectyan.cryptunnel.core.tunnel.AuthFailedException;
import io.github.objectyan.cryptunnel.core.tunnel.TargetCapacityExceededException;

import org.junit.Before;
import org.junit.Test;
import org.springframework.web.socket.CloseStatus;

/**
 * 认证阶段失败的 close reason 映射。
 *
 * <p><b>要防的回归</b>：容量超限曾与密钥错误共用 {@code NOT_ACCEPTABLE("Auth failed")}，
 * 客户端据此显示「认证失败」，用户便去核对密钥——而真实原因是服务端槽位已满，
 * 正确处置是稍后重试或调大 maxConnections。契约 §6 规定容量超限应为
 * {@code SERVICE_UNAVAILABLE}。</p>
 */
public class CryptunnelAuthFailureMappingTest {

    private CryptunnelWebSocketHandler handler;

    @Before
    public void setUp() {
        handler = new CryptunnelWebSocketHandler(new CryptunnelProperties(), null, null);
    }

    @Test
    public void capacityExceededUsesServiceUnavailableNotAuthFailed() {
        FakeWebSocketSession session = new FakeWebSocketSession("s-cap");

        handler.handleAuthFailure(session, new TargetCapacityExceededException("crm-prod"));

        assertEquals(1, session.closed.size());
        CloseStatus status = session.closed.get(0);
        // 1013（Try Again Later）——契约 §6 写的 SERVICE_UNAVAILABLE，Spring 常量名为 SERVICE_OVERLOAD
        assertEquals(CloseStatus.SERVICE_OVERLOAD.getCode(), status.getCode());
        assertEquals(1013, status.getCode());
        // 客户端 close_reason 按字面量精确匹配，改这里必须同步改客户端
        assertEquals("Max connections exceeded", status.getReason());
        assertFalse("rejected session must be closed", session.isOpen());
    }

    @Test
    public void authFailedKeepsNotAcceptable() {
        FakeWebSocketSession session = new FakeWebSocketSession("s-auth");

        handler.handleAuthFailure(session, new AuthFailedException("auth key invalid"));

        assertEquals(1, session.closed.size());
        CloseStatus status = session.closed.get(0);
        assertEquals(CloseStatus.NOT_ACCEPTABLE.getCode(), status.getCode());
        assertEquals("Auth failed", status.getReason());
    }

    @Test
    public void decryptFailureStaysAuthFailed() {
        FakeWebSocketSession session = new FakeWebSocketSession("s-decrypt");

        // 非上述两类异常（解密/解析失败）仍归入认证失败，保持既有客户端映射
        handler.handleAuthFailure(session, new IllegalStateException("bad ciphertext"));

        assertEquals(1, session.closed.size());
        assertEquals(CloseStatus.NOT_ACCEPTABLE.getCode(), session.closed.get(0).getCode());
        assertEquals("Auth failed", session.closed.get(0).getReason());
    }
}
