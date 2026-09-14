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
 * {@link Sm4Cipher} 测试。
 *
 * <p>SM4 的帧结构与默认算法完全一致（IV[16] ‖ HMAC[32] ‖ 密文），
 * 只把分组算法换成 SM4，因此测试重点是：</p>
 * <ul>
 *   <li>帧长度与 AES-CBC+HMAC 一致（便于跨语言对齐时逐字节比对）</li>
 *   <li>密钥派生前缀独立（{@code "SM4:"}），与 AES 密钥互不相关</li>
 *   <li>与 AES-CBC 载荷刻意不兼容（帧同构但密钥/算法不同，必须硬失败）</li>
 * </ul>
 *
 * <p>测试期 BouncyCastle 一定存在（core 的 optional 依赖在自身 test scope 内可见）。</p>
 */
public class Sm4CipherTest {

    private static final String RAW_KEY = "test-aes-key-32-bytes-long-padding";
    private static final byte[] PLAIN =
            "AUTH:TestAuthKey:1700000000:0123456789abcdef0123456789abcdef"
                    .getBytes(StandardCharsets.UTF_8);

    private final TunnelCipher sm4 = new Sm4Cipher();

    // ---------- 基本行为 ----------

    @Test
    public void sealOpen_roundTrip_preservesBytes() {
        assertArrayEquals(PLAIN, sm4.open(sm4.seal(PLAIN, RAW_KEY), RAW_KEY));
    }

    @Test
    public void sealOpen_emptyPlaintext_roundTrips() {
        byte[] empty = new byte[0];
        assertArrayEquals(empty, sm4.open(sm4.seal(empty, RAW_KEY), RAW_KEY));
    }

    @Test
    public void sealOpen_largePayload_roundTrips() {
        byte[] big = new byte[64 * 1024];
        for (int i = 0; i < big.length; i++) {
            big[i] = (byte) (i * 7 & 0xFF);
        }
        assertArrayEquals(big, sm4.open(sm4.seal(big, RAW_KEY), RAW_KEY));
    }

    @Test
    public void seal_randomIv_eachCallDiffers() {
        assertFalse("IV 应随机，两次密文不应相同",
                sm4.seal(PLAIN, RAW_KEY).equals(sm4.seal(PLAIN, RAW_KEY)));
    }

    @Test
    public void id_isStable() {
        assertEquals("sm4-cbc-hmac-sha256", sm4.id());
    }

    // ---------- 帧结构与默认算法同构 ----------

    /**
     * SM4 与 AES 都是 128 位分组，同样 PKCS7 填充，故同一明文下帧长度必须完全相等。
     * 这条断言是「跨语言对齐成本最低」这一设计意图的守护。
     */
    @Test
    public void payload_lengthMatchesAesCbcHmac() {
        int sm4Len = Base64.getDecoder().decode(sm4.seal(PLAIN, RAW_KEY)).length;
        int aesLen = Base64.getDecoder()
                .decode(new AesCbcHmacSha256Cipher().seal(PLAIN, RAW_KEY)).length;
        assertEquals("SM4 帧结构应与 AES-CBC+HMAC 同构", aesLen, sm4Len);
    }

    // ---------- 安全性 ----------

    @Test
    public void open_withWrongKey_throws() {
        String sealed = sm4.seal(PLAIN, RAW_KEY);
        try {
            sm4.open(sealed, "wrong-key-" + RAW_KEY);
            fail("密钥错误应当抛异常");
        } catch (TunnelCryptoException expected) {
            assertNotNull(expected.getMessage());
        }
    }

    @Test
    public void open_withTamperedPayload_throws() {
        byte[] raw = Base64.getDecoder().decode(sm4.seal(PLAIN, RAW_KEY));
        raw[raw.length - 1] = (byte) (raw[raw.length - 1] ^ 0xFF);
        try {
            sm4.open(Base64.getEncoder().encodeToString(raw), RAW_KEY);
            fail("篡改后的载荷应当被 HMAC 拒绝");
        } catch (TunnelCryptoException expected) {
            assertNotNull(expected.getMessage());
        }
    }

    @Test
    public void open_tooShortPayload_throws() {
        try {
            sm4.open(Base64.getEncoder().encodeToString(new byte[20]), RAW_KEY);
            fail("过短载荷应当被拒绝");
        } catch (TunnelCryptoException expected) {
            assertTrue(expected.getMessage().contains("too short"));
        }
    }

    // ---------- 跨算法隔离 ----------

    /**
     * 帧同构不等于可互解：SM4 密钥派生前缀为 {@code "SM4:"}，
     * 与 AES 的 {@code "AES:"} 不同，且分组算法不同。
     * 注意 HMAC 密钥两者一致，所以 HMAC 会校验通过，
     * 真正的拒绝点在 SM4 解密后的 PKCS7 填充校验——这也必须是硬失败。
     */
    @Test
    public void open_aesCbcPayload_fails() {
        String aes = new AesCbcHmacSha256Cipher().seal(PLAIN, RAW_KEY);
        try {
            byte[] out = sm4.open(aes, RAW_KEY);
            fail("AES 载荷不应被 SM4 解出，实际得到 " + out.length + " 字节");
        } catch (TunnelCryptoException expected) {
            assertNotNull(expected.getMessage());
        }
    }

    @Test
    public void aesCbc_cannotOpenSm4Payload() {
        String sealed = sm4.seal(PLAIN, RAW_KEY);
        try {
            byte[] out = new AesCbcHmacSha256Cipher().open(sealed, RAW_KEY);
            fail("SM4 载荷不应被 AES 解出，实际得到 " + out.length + " 字节");
        } catch (TunnelCryptoException expected) {
            assertNotNull(expected.getMessage());
        }
    }

    // ---------- 注册表 ----------

    @Test
    public void registry_sm4RegisteredWithIdAndAlias() {
        assertTrue("BouncyCastle 在 test scope 可见，SM4 应已注册",
                TunnelCiphers.contains(Sm4Cipher.ID));
        assertTrue("短别名 sm4 应可用", TunnelCiphers.contains(Sm4Cipher.ALIAS));
        assertEquals(Sm4Cipher.ID, TunnelCiphers.get(Sm4Cipher.ALIAS).id());
    }
}
