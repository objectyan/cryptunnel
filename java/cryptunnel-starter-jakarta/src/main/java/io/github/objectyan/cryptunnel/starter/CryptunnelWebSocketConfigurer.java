package io.github.objectyan.cryptunnel.starter;

import io.github.objectyan.cryptunnel.core.tunnel.AuthenticatedSessionManager;
import io.github.objectyan.cryptunnel.core.tunnel.TcpTunnel;
import org.slf4j.Logger;
import org.slf4j.LoggerFactory;
import org.springframework.context.annotation.Configuration;
import org.springframework.web.socket.config.annotation.EnableWebSocket;
import org.springframework.web.socket.config.annotation.WebSocketConfigurer;
import org.springframework.web.socket.config.annotation.WebSocketHandlerRegistry;

/**
 * WebSocket 端点注册 + HTTP 降级 Controller 启用。
 *
 * <p><b>注意：这里刻意不加 {@code @EnableScheduling}。</b>那是一个全局开关，
 * 会打开整个宿主应用的定时任务机制——使用方只是引入一个 Cryptunnel starter，
 * 不应因此改变自身应用的调度行为。HTTP 通道的陈旧会话清理改由
 * {@link CryptunnelHttpController} 内部的私有调度器承担，随 Bean 生命周期启停。</p>
 */
@Configuration
@EnableWebSocket
public class CryptunnelWebSocketConfigurer implements WebSocketConfigurer {

    private static final Logger log = LoggerFactory.getLogger(CryptunnelWebSocketConfigurer.class);

    private final CryptunnelProperties properties;
    private final CryptunnelWebSocketHandler handler;

    public CryptunnelWebSocketConfigurer(CryptunnelProperties properties,
                                       CryptunnelWebSocketHandler handler) {
        this.properties = properties;
        this.handler = handler;
        log.info("Cryptunnel: WS endpoint registered, path={}", properties.getWsPath());
    }

    @Override
    public void registerWebSocketHandlers(WebSocketHandlerRegistry registry) {
        registry.addHandler(handler, properties.getWsPath()).setAllowedOrigins("*");
    }
}
