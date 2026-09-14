package io.github.objectyan.cryptunnel.starter;

import io.github.objectyan.cryptunnel.core.target.TargetRegistry;
import io.github.objectyan.cryptunnel.core.tunnel.AuthenticatedSessionManager;
import io.github.objectyan.cryptunnel.core.tunnel.TcpTunnel;
import org.springframework.boot.autoconfigure.condition.ConditionalOnBean;
import org.springframework.context.annotation.Bean;
import org.springframework.context.annotation.Configuration;

/**
 * jakarta 子模块专属 Bean 注册（Boot 3.x / 4.x）。
 *
 * <p>由 common 的 {@link CryptunnelAutoConfiguration} 先注册 TargetRegistry / sessionMgr / tunnel，
 * 本类在 jakarta 子模块下额外注册 WebSocketHandler / HTTP Controller / WebSocketConfigurer。</p>
 *
 * <p>注意：本 starter 完全通过 Spring 的 {@code @RestController} / {@code @EnableWebSocket} 抽象访问
 * Servlet API，不直接 import {@code javax.servlet.*}，因此 jakarta 子模块的 Java 源与 javax 子模块相同
 * （javax.crypto 是 JDK 自带 JCE，未被 Jakarta EE 9+ 改动）。</p>
 */
@Configuration
@ConditionalOnBean(TargetRegistry.class)
public class CryptunnelStarterJakartaAutoConfiguration {

    @Bean
    public CryptunnelWebSocketHandler jdbcProxyWebSocketHandler(
            CryptunnelProperties properties,
            AuthenticatedSessionManager sessionManager,
            TargetRegistry targetRegistry) {
        return new CryptunnelWebSocketHandler(properties, sessionManager, targetRegistry);
    }

    @Bean
    public CryptunnelWebSocketConfigurer jdbcProxyWebSocketConfigurer(
            CryptunnelProperties properties,
            CryptunnelWebSocketHandler handler) {
        return new CryptunnelWebSocketConfigurer(properties, handler);
    }

    @Bean
    public CryptunnelHttpController jdbcProxyHttpController(
            CryptunnelProperties properties,
            AuthenticatedSessionManager sessionManager,
            TcpTunnel tunnel,
            TargetRegistry targetRegistry) {
        return new CryptunnelHttpController(properties, sessionManager, tunnel, targetRegistry);
    }
}
