package io.github.objectyan.cryptunnel.starter;

import java.io.IOException;
import java.net.InetSocketAddress;
import java.net.URI;
import java.security.Principal;
import java.util.List;
import java.util.Map;
import java.util.concurrent.CopyOnWriteArrayList;
import java.util.concurrent.atomic.AtomicInteger;

import org.springframework.http.HttpHeaders;
import org.springframework.web.socket.CloseStatus;
import org.springframework.web.socket.WebSocketExtension;
import org.springframework.web.socket.WebSocketMessage;
import org.springframework.web.socket.WebSocketSession;

/**
 * 记录发送 / 关闭动作的假会话，供 starter 的单元测试共用。
 *
 * <p>只实现测试用得到的方法，其余一律 {@link UnsupportedOperationException}——
 * 这样接口未来新增方法时，测试会立刻暴露而不是静默走默认实现。</p>
 */
final class FakeWebSocketSession implements WebSocketSession {

    private final String id;
    private volatile boolean open = true;

    final List<WebSocketMessage<?>> sent = new CopyOnWriteArrayList<WebSocketMessage<?>>();
    final List<CloseStatus> closed = new CopyOnWriteArrayList<CloseStatus>();
    final AtomicInteger sendAttempts = new AtomicInteger();

    /** 为 true 时 {@link #sendMessage} 直接抛 IOException，模拟写不通的死链路。 */
    volatile boolean failSends = false;

    FakeWebSocketSession(String id) {
        this.id = id;
    }

    @Override
    public String getId() {
        return id;
    }

    @Override
    public boolean isOpen() {
        return open;
    }

    @Override
    public void sendMessage(WebSocketMessage<?> message) throws IOException {
        sendAttempts.incrementAndGet();
        if (failSends) {
            throw new IOException("simulated dead link");
        }
        sent.add(message);
    }

    @Override
    public void close(CloseStatus status) throws IOException {
        closed.add(status);
        open = false;
    }

    @Override
    public void close() throws IOException {
        close(CloseStatus.NO_STATUS_CODE);
    }

    // ---- 以下方法本测试用不到 ----

    @Override
    public URI getUri() {
        throw new UnsupportedOperationException();
    }

    @Override
    public HttpHeaders getHandshakeHeaders() {
        throw new UnsupportedOperationException();
    }

    @Override
    public Map<String, Object> getAttributes() {
        throw new UnsupportedOperationException();
    }

    @Override
    public Principal getPrincipal() {
        throw new UnsupportedOperationException();
    }

    @Override
    public InetSocketAddress getLocalAddress() {
        throw new UnsupportedOperationException();
    }

    @Override
    public InetSocketAddress getRemoteAddress() {
        throw new UnsupportedOperationException();
    }

    @Override
    public String getAcceptedProtocol() {
        throw new UnsupportedOperationException();
    }

    @Override
    public void setTextMessageSizeLimit(int messageSizeLimit) {
        throw new UnsupportedOperationException();
    }

    @Override
    public int getTextMessageSizeLimit() {
        throw new UnsupportedOperationException();
    }

    @Override
    public void setBinaryMessageSizeLimit(int messageSizeLimit) {
        throw new UnsupportedOperationException();
    }

    @Override
    public int getBinaryMessageSizeLimit() {
        throw new UnsupportedOperationException();
    }

    @Override
    public List<WebSocketExtension> getExtensions() {
        throw new UnsupportedOperationException();
    }
}
