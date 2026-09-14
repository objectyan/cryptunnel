package io.github.objectyan.cryptunnel.core.crypto;

import java.nio.charset.StandardCharsets;
import java.util.Arrays;
import java.util.Base64;
import org.junit.Test;

import static org.junit.Assert.assertArrayEquals;
import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertNotNull;
import static org.junit.Assert.assertTrue;
import static org.junit.Assert.fail;

/**
 * {@link TunnelCipher} 的默认实现与注册表测试。
 *
 * <p><b>最关键的验证是字节级等价</b>：默认实现走 SPI 后，必须与改动前直接调用
 * {@link AesUtil} 产生完全一致的线上载荷，否则「老客户端零改动」承诺即被破坏。
 * 由于加密含随机 IV，无法直接比较两次输出，故采用<b>双向交叉验证</b>：
 * 新加密给老方法解、老加密给新方法解。</p>
 */
public class TunnelCipherTest {

    private static final String RAW_KEY = "test-aes-key-32-bytes-long-padding";
    private static final byte[] PLAIN =
            "AUTH:TestAuthKey:1700000000:0123456789abcdef0123456789abcdef"
                    .getBytes(StandardCharsets.UTF_8);

    private final TunnelCipher cipher = new AesCbcHmacSha256Cipher();

    // ---------- 兼容性红线 ----------

    /** 新实现加密 -&gt; 老方法解密，必须成功。 */
    @Test
    public void seal_outputIsDecryptableByLegacyAesUtil() {
        String sealed = cipher.seal(PLAIN, RAW_KEY);
        byte[] out = AesUtil.decrypt(sealed,
                AesUtil.deriveAesKey(RAW_KEY),
                AesUtil.deriveHmacKey(RAW_KEY));
        assertArrayEquals(PLAIN, out);
    }

    /** 老方法加密 -&gt; 新实现解密，必须成功。 */
    @Test
    public void open_canDecryptLegacyAesUtilOutput() {
        String legacy = AesUtil.encrypt(PLAIN,
                AesUtil.deriveAesKey(RAW_KEY),
                AesUtil.deriveHmacKey(RAW_KEY));
        byte[] out = cipher.open(legacy, RAW_KEY);
        assertArrayEquals(PLAIN, out);
    }

    /** 载荷结构须为 Base64( IV[16] || HMAC[32] || 密文 )。 */
    @Test
    public void sealedPayload_matchesLegacyWireFormat() {
        String sealed = cipher.seal(PLAIN, RAW_KEY);
        byte[] raw = Base64.getDecoder().decode(sealed);
        // IV[16] + HMAC[32] + 密文（PKCS5 填充后至少 16 字节）
        assertTrue("payload too short: " + raw.length, raw.length >= 16 + 32 + 16);
    }

    // ---------- 基本行为 ----------

    @Test
    public void sealOpen_roundTrip_preservesBytes() {
        byte[] out = cipher.open(cipher.seal(PLAIN, RAW_KEY), RAW_KEY);
        assertArrayEquals(PLAIN, out);
    }

    @Test
    public void seal_randomIv_eachCallDiffers() {
        String a = cipher.seal(PLAIN, RAW_KEY);
        String b = cipher.seal(PLAIN, RAW_KEY);
        assertTrue("IV 应随机，两次密文不应相同", !a.equals(b));
    }

    @Test
    public void open_withWrongKey_throws() {
        String sealed = cipher.seal(PLAIN, RAW_KEY);
        try {
            cipher.open(sealed, "wrong-key-" + RAW_KEY);
            fail("密钥错误应当抛异常");
        } catch (TunnelCryptoException expected) {
            assertNotNull(expected.getMessage());
        }
    }

    @Test
    public void open_withTamperedPayload_throws() {
        String sealed = cipher.seal(PLAIN, RAW_KEY);
        byte[] raw = Base64.getDecoder().decode(sealed);
        raw[raw.length - 1] = (byte) (raw[raw.length - 1] ^ 0xFF);
        String tampered = Base64.getEncoder().encodeToString(raw);
        try {
            cipher.open(tampered, RAW_KEY);
            fail("篡改后的载荷应当被 HMAC 拒绝");
        } catch (TunnelCryptoException expected) {
            assertNotNull(expected.getCause());
        }
    }

    @Test
    public void id_isStableAndLowercase() {
        assertEquals("aes-256-cbc-hmac-sha256", cipher.id());
    }

    // ---------- 注册表 ----------

    @Test
    public void registry_defaultCipherIsRegistered() {
        assertTrue(TunnelCiphers.contains(AesCbcHmacSha256Cipher.ID));
        assertEquals(AesCbcHmacSha256Cipher.ID, TunnelCiphers.defaultCipher().id());
    }

    @Test
    public void registry_getUnknownId_throws() {
        try {
            TunnelCiphers.get("no-such-cipher");
            fail("未注册的标识应当抛异常");
        } catch (IllegalArgumentException expected) {
            assertTrue(expected.getMessage().contains("no-such-cipher"));
        }
    }

    @Test
    public void registry_canRegisterCustomCipher() {
        TunnelCiphers.register(new ReversedCipher());
        TunnelCipher got = TunnelCiphers.get("test-reversed");
        assertEquals("test-reversed", got.id());
        // 自定义实现自身可用
        String sealed = got.seal(PLAIN, RAW_KEY);
        assertArrayEquals(PLAIN, got.open(sealed, RAW_KEY));
    }

    /**
     * 仅用于验证注册表可扩展性的玩具实现（不做任何真实加密）。
     */
    private static final class ReversedCipher implements TunnelCipher {
        @Override
        public String id() {
            return "test-reversed";
        }

        @Override
        public String seal(byte[] plaintext, String rawKey) {
            byte[] copy = Arrays.copyOf(plaintext, plaintext.length);
            reverse(copy);
            return Base64.getEncoder().encodeToString(copy);
        }

        @Override
        public byte[] open(String payload, String rawKey) {
            byte[] raw = Base64.getDecoder().decode(payload);
            reverse(raw);
            return raw;
        }

        private static void reverse(byte[] a) {
            for (int i = 0, j = a.length - 1; i < j; i++, j--) {
                byte t = a[i];
                a[i] = a[j];
                a[j] = t;
            }
        }
    }
}
