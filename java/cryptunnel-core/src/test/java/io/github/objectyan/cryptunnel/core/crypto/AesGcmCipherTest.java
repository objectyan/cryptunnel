package io.github.objectyan.cryptunnel.core.crypto;

import java.nio.charset.StandardCharsets;
import java.util.Base64;
import org.junit.Test;

import static org.junit.Assert.assertArrayEquals;
import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertFalse;
import static org.junit.Assert.assertNotNull;
import static org.junit.Assert.assertTrue;
import static org.junit.Assert.fail;

/**
 * {@link AesGcmCipher} 测试。
 *
 * <p>GCM 是 AEAD，密文自带 16 字节认证标签，不再外挂 HMAC。因此本组测试重点验证：</p>
 * <ul>
 *   <li>载荷长度确实比默认算法短（省掉 32 字节 HMAC，只多 16 字节标签）</li>
 *   <li>标签能挡住任意位置的篡改（不止尾部）</li>
 *   <li>与默认算法<b>互不兼容</b>——这是刻意的，防止「一端换了算法另一端没换」时
 *       出现半可用的诡异状态，必须是硬失败</li>
 * </ul>
 */
public class AesGcmCipherTest {

    private static final String RAW_KEY = "test-aes-key-32-bytes-long-padding";
    private static final byte[] PLAIN =
            "AUTH:TestAuthKey:1700000000:0123456789abcdef0123456789abcdef"
                    .getBytes(StandardCharsets.UTF_8);

    private final TunnelCipher gcm = new AesGcmCipher();

    // ---------- 基本行为 ----------

    @Test
    public void sealOpen_roundTrip_preservesBytes() {
        assertArrayEquals(PLAIN, gcm.open(gcm.seal(PLAIN, RAW_KEY), RAW_KEY));
    }

    @Test
    public void sealOpen_emptyPlaintext_roundTrips() {
        byte[] empty = new byte[0];
        assertArrayEquals(empty, gcm.open(gcm.seal(empty, RAW_KEY), RAW_KEY));
    }

    @Test
    public void sealOpen_largePayload_roundTrips() {
        byte[] big = new byte[64 * 1024];
        for (int i = 0; i < big.length; i++) {
            big[i] = (byte) (i & 0xFF);
        }
        assertArrayEquals(big, gcm.open(gcm.seal(big, RAW_KEY), RAW_KEY));
    }

    @Test
    public void seal_randomNonce_eachCallDiffers() {
        assertFalse("nonce 应随机，两次密文不应相同",
                gcm.seal(PLAIN, RAW_KEY).equals(gcm.seal(PLAIN, RAW_KEY)));
    }

    @Test
    public void id_isStable() {
        assertEquals("aes-256-gcm", gcm.id());
    }

    // ---------- 帧结构 ----------

    /** 载荷 = nonce[12] + 密文 + 标签[16]，比 CBC+HMAC 少 32-16-4=... 总之更短。 */
    @Test
    public void payload_layout_nonce12PlusCiphertextPlusTag16() {
        byte[] raw = Base64.getDecoder().decode(gcm.seal(PLAIN, RAW_KEY));
        // GCM 是流式（CTR）不填充，故密文长度 == 明文长度
        assertEquals(12 + PLAIN.length + 16, raw.length);
    }

    /** 同一明文下 GCM 载荷应短于 CBC+HMAC 载荷。 */
    @Test
    public void payload_isShorterThanCbcHmac() {
        int gcmLen = Base64.getDecoder().decode(gcm.seal(PLAIN, RAW_KEY)).length;
        int cbcLen = Base64.getDecoder()
                .decode(new AesCbcHmacSha256Cipher().seal(PLAIN, RAW_KEY)).length;
        assertTrue("GCM(" + gcmLen + ") 应短于 CBC+HMAC(" + cbcLen + ")", gcmLen < cbcLen);
    }

    // ---------- 安全性 ----------

    @Test
    public void open_withWrongKey_throws() {
        String sealed = gcm.seal(PLAIN, RAW_KEY);
        try {
            gcm.open(sealed, "wrong-key-" + RAW_KEY);
            fail("密钥错误应当抛异常");
        } catch (TunnelCryptoException expected) {
            assertNotNull(expected.getMessage());
        }
    }

    /** GCM 标签覆盖全部密文，任意字节被改都必须失败。 */
    @Test
    public void open_withTamperedCiphertext_throwsAtAnyPosition() {
        byte[] raw = Base64.getDecoder().decode(gcm.seal(PLAIN, RAW_KEY));
        for (int pos : new int[]{0, 5, 12, raw.length / 2, raw.length - 1}) {
            byte[] copy = raw.clone();
            copy[pos] = (byte) (copy[pos] ^ 0xFF);
            try {
                gcm.open(Base64.getEncoder().encodeToString(copy), RAW_KEY);
                fail("第 " + pos + " 字节被篡改后应当失败");
            } catch (TunnelCryptoException expected) {
                assertNotNull(expected.getMessage());
            }
        }
    }

    @Test
    public void open_tooShortPayload_throws() {
        try {
            gcm.open(Base64.getEncoder().encodeToString(new byte[10]), RAW_KEY);
            fail("过短载荷应当被拒绝");
        } catch (TunnelCryptoException expected) {
            assertTrue(expected.getMessage().contains("too short"));
        }
    }

    // ---------- 跨算法隔离（刻意不兼容） ----------

    /** CBC+HMAC 的载荷不能被 GCM 解开，必须硬失败而非静默产出垃圾。 */
    @Test
    public void open_cbcHmacPayload_fails() {
        String cbc = new AesCbcHmacSha256Cipher().seal(PLAIN, RAW_KEY);
        try {
            gcm.open(cbc, RAW_KEY);
            fail("不同算法的载荷必须硬失败");
        } catch (TunnelCryptoException expected) {
            assertNotNull(expected.getMessage());
        }
    }

    /** 反向亦然：GCM 载荷不能被 CBC+HMAC 解开。 */
    @Test
    public void cbcHmac_cannotOpenGcmPayload() {
        String sealed = gcm.seal(PLAIN, RAW_KEY);
        try {
            new AesCbcHmacSha256Cipher().open(sealed, RAW_KEY);
            fail("不同算法的载荷必须硬失败");
        } catch (TunnelCryptoException expected) {
            assertNotNull(expected.getMessage());
        }
    }

    // ---------- 注册表 ----------

    @Test
    public void registry_gcmIsRegistered() {
        assertTrue(TunnelCiphers.contains(AesGcmCipher.ID));
        assertEquals(AesGcmCipher.ID, TunnelCiphers.get(AesGcmCipher.ID).id());
    }
}
