package io.github.objectyan.cryptunnel.core.auth;

import io.github.objectyan.cryptunnel.core.crypto.AesUtil;
import io.github.objectyan.cryptunnel.core.target.DefaultTargetRegistry;
import io.github.objectyan.cryptunnel.core.target.TargetDefinition;
import io.github.objectyan.cryptunnel.core.target.TargetNotFoundException;
import io.github.objectyan.cryptunnel.core.target.TargetRegistry;
import java.util.Arrays;
import java.util.Collections;
import javax.crypto.spec.SecretKeySpec;
import org.junit.Test;
import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertNotNull;
import static org.junit.Assert.fail;

public class TargetDiscoveryTest {

    private TargetDefinition make(String name, String aesKey) {
        return new TargetDefinition(name, null, "10.0.0.1", 3306, aesKey, "auth-key",
                5, 10000, 0);
    }

    private String encrypt(String rawKey, String plain) {
        SecretKeySpec aes = AesUtil.deriveAesKey(rawKey);
        SecretKeySpec mac = AesUtil.deriveHmacKey(rawKey);
        return AesUtil.encrypt(plain.getBytes(java.nio.charset.StandardCharsets.UTF_8), aes, mac);
    }

    @Test
    public void discover_findsCorrectTarget_amongMultiple() {
        TargetDefinition t1 = make("alpha", "alpha-aes-key-32-bytes-padded");
        TargetDefinition t2 = make("beta", "beta-aes-key-32-bytes-padded-xx");
        TargetDefinition t3 = make("gamma", "gamma-aes-key-32-bytes-padding");
        TargetRegistry r = new DefaultTargetRegistry(Arrays.asList(t1, t2, t3), "alpha");

        // 用 t2 的 key 加密 AUTH 报文
        String encrypted = encrypt("beta-aes-key-32-bytes-padded-xx",
                "AUTH:authkey:1700000000:abc123:beta");

        TargetDiscovery.DecryptedAuth result = TargetDiscovery.discover(r, encrypted);
        assertEquals("beta", result.target().getName());
        assertEquals("beta", result.auth().getTargetId());
        assertEquals("authkey", result.auth().getAuthKey());
    }

    @Test
    public void discover_legacy4Segment_usesDefaultTarget() {
        TargetDefinition t1 = make("default-1", "default-1-aes-key-32-bytes-xx");
        TargetDefinition t2 = make("named-2", "named-2-aes-key-32-bytes-pad");
        TargetRegistry r = new DefaultTargetRegistry(Arrays.asList(t1, t2), "default-1");

        // 用 default target 的 key 加密 legacy 4-segment AUTH
        String encrypted = encrypt("default-1-aes-key-32-bytes-xx",
                "AUTH:authkey:1700000000:abc123");

        TargetDiscovery.DecryptedAuth result = TargetDiscovery.discover(r, encrypted);
        assertEquals("default-1", result.target().getName());
        assertNotNull(result.auth());
    }

    @Test
    public void discover_wrongKey_throws() {
        TargetDefinition t1 = make("only", "correct-aes-key-32-bytes-pad-x");
        TargetRegistry r = new DefaultTargetRegistry(Collections.singletonList(t1), "only");

        // 用错的 key 加密
        String encrypted = encrypt("wrong-aes-key-32-bytes-padded-yyy",
                "AUTH:authkey:1700000000:abc123");

        try {
            TargetDiscovery.discover(r, encrypted);
            fail("expected IllegalArgumentException");
        } catch (IllegalArgumentException e) {
            // expected — 没有 target 解得开
        }
    }

    @Test
    public void discover_emptyPayload_throws() {
        TargetDefinition t1 = make("only", "only-aes-key-32-bytes-padded-xxx");
        TargetRegistry r = new DefaultTargetRegistry(Collections.singletonList(t1), "only");
        try {
            TargetDiscovery.discover(r, null);
            fail("expected IllegalArgumentException");
        } catch (IllegalArgumentException e) {
            // expected
        }
        try {
            TargetDiscovery.discover(r, "");
            fail("expected IllegalArgumentException");
        } catch (IllegalArgumentException e) {
            // expected
        }
    }
}
