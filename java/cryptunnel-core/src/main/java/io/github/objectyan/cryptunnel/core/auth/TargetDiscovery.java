package io.github.objectyan.cryptunnel.core.auth;

import io.github.objectyan.cryptunnel.core.crypto.TunnelCipher;
import io.github.objectyan.cryptunnel.core.crypto.TunnelCiphers;
import io.github.objectyan.cryptunnel.core.target.TargetDefinition;
import io.github.objectyan.cryptunnel.core.target.TargetRegistry;
import java.nio.charset.StandardCharsets;
import java.util.Collection;

/**
 * 服务端接收加密 AUTH 报文后，用此工具**逐个试解**注册表里的 target 密钥。
 *
 * <p>核心约束：AUTH 报文是 AES 加密的，server 在没解开之前不知道属于哪个 target。
 * 由于 target 数量通常很少（个位数），暴力尝试 + HMAC 校验是可接受的：
 * 失败的尝试在 HMAC 校验阶段就被拒绝，不会出现「错密钥解出乱码被误接受」。</p>
 *
 * <p>成功路径：找到 target → AuthMessageCodec.parse → 后续鉴权。</p>
 *
 * <p>失败路径：所有 target 都解不开 → 抛 {@link IllegalArgumentException} 让 handler 返回 401。</p>
 */
public final class TargetDiscovery {

    private TargetDiscovery() {
    }

    /**
     * 用注册表里的 target 逐个尝试解出 AUTH 报文。
     *
     * @param registry         服务端 target 注册表
     * @param encryptedPayload Base64(IV||HMAC||Cipher) 形式的加密 AUTH
     * @return 解密后的 AuthMessage + 对应的 TargetDefinition
     * @throws IllegalArgumentException 没有 target 解得开（视为鉴权失败）
     */
    public static DecryptedAuth discover(TargetRegistry registry, String encryptedPayload) {
        if (registry == null) {
            throw new IllegalStateException("TargetRegistry is null");
        }
        if (encryptedPayload == null || encryptedPayload.isEmpty()) {
            throw new IllegalArgumentException("encrypted payload is empty");
        }

        Collection<TargetDefinition> all = registry.all();
        TunnelCipher cipher = TunnelCiphers.defaultCipher();
        IllegalArgumentException last = null;
        for (TargetDefinition target : all) {
            try {
                byte[] plain = cipher.open(encryptedPayload, target.getAesKey());
                String decoded = new String(plain, StandardCharsets.UTF_8);
                AuthMessage auth = AuthMessageCodec.parse(decoded);
                return new DecryptedAuth(target, auth);
            } catch (Exception e) {
                // 失败就试下一个 target。记录最后一个原因便于排查。
                last = new IllegalArgumentException(
                        "target '" + target.getName() + "' decrypt failed: " + e.getMessage());
            }
        }
        throw last != null ? last : new IllegalArgumentException("no targets registered");
    }

    /** 探测结果：哪个 target 接受 + 解出的 AuthMessage。 */
    public static final class DecryptedAuth {
        private final TargetDefinition target;
        private final AuthMessage auth;

        public DecryptedAuth(TargetDefinition target, AuthMessage auth) {
            this.target = target;
            this.auth = auth;
        }

        public TargetDefinition target() {
            return target;
        }

        public AuthMessage auth() {
            return auth;
        }
    }
}
