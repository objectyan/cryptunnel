package io.github.objectyan.cryptunnel.starter;

import io.github.objectyan.cryptunnel.core.tunnel.AuthenticatedSessionManager;
import io.github.objectyan.cryptunnel.core.tunnel.TcpTunnel;
import org.slf4j.Logger;
import org.slf4j.LoggerFactory;
import org.springframework.web.socket.config.annotation.WebSocketConfigurer;
import org.springframework.web.socket.config.annotation.WebSocketHandlerRegistry;

/**
 * WebSocket 端点注册 + HTTP 降级 Controller 启用。
 *
 * <p><b>注意：这里刻意不加 {@code @EnableScheduling}。</b>那是一个全局开关，
 * 会打开整个宿主应用的定时任务机制——使用方只是引入一个 Cryptunnel starter，
 * 不应因此改变自身应用的调度行为。HTTP 通道的陈旧会话清理改由
 * {@link CryptunnelHttpController} 内部的私有调度器承担，随 Bean 生命周期启停。</p>
 *
 * <p><b>此类不是 {@code @Configuration}、也不写 {@code @EnableWebSocket}。</b>
 * 原因：它经由 {@code CryptunnelStarterJavaxAutoConfiguration} 的 {@code @Bean} 工厂方法创建，
 * 而「由 {@code @Bean} 工厂方法返回的 {@code @Configuration} 类」其类级 {@code @Import}
 * 元注解不会被 {@code ConfigurationClassPostProcessor} 处理，导致
 * {@code @EnableWebSocket} 引入的 {@code WebSocketConfiguration} 永不加载、
 * {@code registerWebSocketHandlers} 永不回调、WS 路径从不进入 handler mapping
 * （这正是本地握手返回 404 JSON 的根因）。{@code @EnableWebSocket} 应放在
 * 会被 Spring 正常处理的自动装配类上。</p>
 */
public class CryptunnelWebSocketConfigurer implements WebSocketConfigurer {

    private static final Logger log = LoggerFactory.getLogger(CryptunnelWebSocketConfigurer.class);

    private final CryptunnelProperties properties;
    private final CryptunnelWebSocketHandler handler;

    public CryptunnelWebSocketConfigurer(CryptunnelProperties properties,
                                       CryptunnelWebSocketHandler handler) {
        this.properties = properties;
        this.handler = handler;
    }

    @Override
    public void registerWebSocketHandlers(WebSocketHandlerRegistry registry) {
        registry.addHandler(handler, properties.getWsPath()).setAllowedOrigins("*");
        log.info("Cryptunnel: WS endpoint registered, path={}", properties.getWsPath());
    }
}
