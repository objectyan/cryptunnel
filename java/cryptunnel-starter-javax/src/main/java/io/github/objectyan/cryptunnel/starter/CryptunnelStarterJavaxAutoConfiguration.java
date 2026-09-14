package io.github.objectyan.cryptunnel.starter;

import io.github.objectyan.cryptunnel.core.target.TargetRegistry;
import io.github.objectyan.cryptunnel.core.tunnel.AuthenticatedSessionManager;
import io.github.objectyan.cryptunnel.core.tunnel.TcpTunnel;
import org.springframework.boot.autoconfigure.condition.ConditionalOnBean;
import org.springframework.context.annotation.Bean;
import org.springframework.context.annotation.Configuration;
import org.springframework.web.socket.config.annotation.EnableWebSocket;

/**
 * javax 子模块专属 Bean 注册。
 *
 * <p>由 common 的 {@link CryptunnelAutoConfiguration} 先注册 TargetRegistry / sessionMgr / tunnel，
 * 本类在 javax 子模块下额外注册 WebSocketHandler / HTTP Controller / WebSocketConfigurer。</p>
 */
@Configuration
@ConditionalOnBean(TargetRegistry.class)
@EnableWebSocket
public class CryptunnelStarterJavaxAutoConfiguration {

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
