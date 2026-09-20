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
     * 跨算法拒绝的可测口径必须诚实：SM4 与 AES 同为 128 位分组 + PKCS7，
     * 且 HMAC 密钥相同（同为 {@code SHA256("HMAC:"+k)}，这是「帧同构」设计意图，
     * 线级契约记录在案），因此错误密钥解出的末块恰好构成合法填充的概率约 1/256，
     * 偶发样本会被「成功解出」——java-jakarta CI 2026-09-20 就真实命中过一次
     * （输出 63 字节）。<b>跨算法硬失败是概率防线，不是确定性防线</b>；
     * 部署层的真正防线是两端配置核对 + 服务端日志 cipherId（见 ADR-0003）。
     *
     * <p>所以本测试的口径是「拒绝率必须 ≥ 99%」：512 个独立样本（随机 IV），
     * 若隔离机制整体失效（例如有人把密钥派生前缀改成相同），解出率会是 ~100%，
     * 与 1/256 的天然噪音有数量级差距，测试稳定区分两种情形。
     * 采样耗时亚毫秒级/个，测试总时长与原来相当。</p>
     */
    private static final int CROSS_SAMPLES = 512;

    /** 天然填充巧合容忍上限（约 1/256 × 512 ≈ 2 个的期望，放宽到 5 个防抖动）。 */
    private static final int CROSS_LUCKY_TOLERANCE = 5;

    @Test
    public void open_aesCbcPayload_fails() {
        TunnelCipher aes = new AesCbcHmacSha256Cipher();
        int lucky = 0;
        for (int i = 0; i < CROSS_SAMPLES; i++) {
            try {
                sm4.open(aes.seal(PLAIN, RAW_KEY), RAW_KEY);
                lucky++; // 末块填充巧合，期望 ~512/256 = 2 个，不计入隔离失效
            } catch (TunnelCryptoException expected) {
                // 期望路径：PKCS7 填充校验拒绝。
            }
        }
        assertTrue("AES→SM4 方向解出 " + lucky + "/" + CROSS_SAMPLES
                + " 个，远超 1/256 的天然填充巧合——跨算法隔离疑似失效"
                + "（检查密钥派生前缀 \"AES:\"/\"SM4:\" 是否被改成相同）",
                lucky <= CROSS_LUCKY_TOLERANCE);
    }

    @Test
    public void aesCbc_cannotOpenSm4Payload() {
        TunnelCipher aes = new AesCbcHmacSha256Cipher();
        int lucky = 0;
        for (int i = 0; i < CROSS_SAMPLES; i++) {
            try {
                aes.open(sm4.seal(PLAIN, RAW_KEY), RAW_KEY);
                lucky++;
            } catch (TunnelCryptoException expected) {
                // 期望路径：PKCS7 填充校验拒绝。
            }
        }
        assertTrue("SM4→AES 方向解出 " + lucky + "/" + CROSS_SAMPLES
                + " 个，远超 1/256 的天然填充巧合——跨算法隔离疑似失效",
                lucky <= CROSS_LUCKY_TOLERANCE);
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
