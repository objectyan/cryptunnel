package io.github.objectyan.cryptunnel.starter;

import io.github.objectyan.cryptunnel.core.crypto.AesCbcHmacSha256Cipher;
import io.github.objectyan.cryptunnel.core.crypto.TunnelCipher;
import io.github.objectyan.cryptunnel.core.crypto.TunnelCiphers;
import io.github.objectyan.cryptunnel.core.target.DefaultTargetRegistry;
import io.github.objectyan.cryptunnel.core.target.LegacyConfigAdapter;
import io.github.objectyan.cryptunnel.core.target.TargetDefinition;
import io.github.objectyan.cryptunnel.core.target.TargetRegistry;
import io.github.objectyan.cryptunnel.core.tunnel.AuthenticatedSessionManager;
import io.github.objectyan.cryptunnel.core.tunnel.DefaultAuthenticatedSessionManager;
import io.github.objectyan.cryptunnel.core.tunnel.DefaultTcpTunnel;
import io.github.objectyan.cryptunnel.core.tunnel.NonceCache;
import io.github.objectyan.cryptunnel.core.tunnel.TcpTunnel;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;
import org.slf4j.Logger;
import org.slf4j.LoggerFactory;
import org.springframework.boot.autoconfigure.condition.ConditionalOnProperty;
import org.springframework.boot.context.properties.EnableConfigurationProperties;
import org.springframework.context.annotation.Bean;
import org.springframework.context.annotation.Configuration;

@Configuration
@ConditionalOnProperty(name = "cryptunnel.enabled", havingValue = "true", matchIfMissing = false)
@EnableConfigurationProperties(CryptunnelProperties.class)
public class CryptunnelAutoConfiguration {

    private static final Logger log = LoggerFactory.getLogger(CryptunnelAutoConfiguration.class);

    /**
     * 隧道加密算法。服务端同一时刻只允许启用一个（ADR-0003）；
     * 配置了未注册的算法直接启动失败，避免到运行期才暴露。
     */
    @Bean
    public TunnelCipher tunnelCipher(CryptunnelProperties props) {
        String id = props.getCipher();
        if (id == null || id.isEmpty()) {
            id = AesCbcHmacSha256Cipher.ID;
        }
        TunnelCipher cipher;
        try {
            // 交给注册表报错：它知道哪些算法需要额外依赖（如 SM4 需要 BouncyCastle），
            // 能给出可操作的修复提示，比在此处自己拼文案更准确。
            cipher = TunnelCiphers.get(id);
        } catch (IllegalArgumentException e) {
            throw new IllegalStateException("cryptunnel.cipher 配置无效: " + e.getMessage(), e);
        }
        TunnelCiphers.setDefault(cipher);
        log.info("Cryptunnel: cipher={} (single cipher enforced, see ADR-0003)", cipher.id());
        return cipher;
    }

    @Bean
    public TargetRegistry targetRegistry(CryptunnelProperties props) {
        List<TargetDefinition> targets = new ArrayList<TargetDefinition>();
        if (props.getTargets() != null) {
            for (Map.Entry<String, CryptunnelProperties.TargetConfig> e : props.getTargets().entrySet()) {
                CryptunnelProperties.TargetConfig c = e.getValue();
                targets.add(new TargetDefinition(
                        e.getKey(),
                        c.getDisplayName(),
                        c.getMysqlHost(),
                        c.getMysqlPort(),
                        c.getAesKey(),
                        c.getAuthKey(),
                        c.getMaxConnections(),
                        c.getConnectTimeout(),
                        c.getReadTimeout()
                ));
            }
        }
        List<TargetDefinition> wrapped = LegacyConfigAdapter.wrap(
                targets,
                props.getMysqlHost(),
                props.getMysqlPort(),
                props.getAesKey(),
                props.getAuthKey(),
                props.getMaxConnections(),
                props.getConnectTimeout(),
                props.getReadTimeout()
        );
        DefaultTargetRegistry registry = new DefaultTargetRegistry(wrapped, props.getDefaultTarget());
        log.info("Cryptunnel: registered targets {}; default={}",
                registry.names(), registry.defaultTargetName());
        return registry;
    }

    @Bean
    public NonceCache nonceCache(CryptunnelProperties props) {
        long windowMs = ((long) props.getAuthTimeWindow()) * 2L * 1000L;
        return new NonceCache(windowMs);
    }

    @Bean
    public TcpTunnel tcpTunnel() {
        return new DefaultTcpTunnel();
    }

    @Bean
    public AuthenticatedSessionManager authenticatedSessionManager(
            CryptunnelProperties props,
            TargetRegistry registry, TcpTunnel tunnel, NonceCache nonceCache) {
        return new DefaultAuthenticatedSessionManager(
                registry, tunnel, nonceCache, props.getAuthTimeWindow());
    }
}
