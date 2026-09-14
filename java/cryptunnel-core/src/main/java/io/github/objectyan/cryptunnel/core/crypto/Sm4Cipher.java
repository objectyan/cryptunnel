package io.github.objectyan.cryptunnel.core.crypto;

import org.bouncycastle.jce.provider.BouncyCastleProvider;

import javax.crypto.Cipher;
import javax.crypto.Mac;
import javax.crypto.spec.IvParameterSpec;
import javax.crypto.spec.SecretKeySpec;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.security.Provider;
import java.security.SecureRandom;
import java.util.Arrays;
import java.util.Base64;
import java.util.Collection;
import java.util.Collections;

/**
 * 国密 SM4 隧道加密算法：SM4-CBC + HMAC-SHA256（encrypt-then-MAC）。
 *
 * <p>JDK 标准 Provider（SunJCE）在任何版本都不提供 SM4，因此本实现依赖 BouncyCastle。
 * BouncyCastle 在 core 中是 <b>optional 依赖</b>：不使用 SM4 时无需引入，
 * 也不会传递给宿主应用。</p>
 *
 * <p>载荷格式与默认的 {@link AesCbcHmacSha256Cipher} <b>完全一致</b>，
 * 只是把分组算法从 AES 换成 SM4——这样三端改造只需替换 Cipher 算法名，
 * 帧结构、HMAC 覆盖范围、IV 长度全部不变，跨语言对齐成本最低：</p>
 * <pre>
 *   Base64( IV[16] || HMAC[32] || SM4/CBC/PKCS7Padding 密文 )
 *   HMAC 覆盖范围：IV || 密文
 *   密钥派生：SHA-256("SM4:" + rawKey) 取前 16 字节做 SM4-128 密钥
 *            HMAC 密钥仍为 SHA-256("HMAC:" + rawKey)，与默认算法一致
 * </pre>
 *
 * <p>采用独立派生前缀 {@code "SM4:"}，使同一把 rawKey 下的 SM4 密钥与 AES 密钥
 * 互不相关（避免算法间密钥复用带来的推导风险）。</p>
 *
 * <p><b>Provider 使用方式</b>：显式传入 {@link Provider} 实例而非调用
 * {@code Security.addProvider(...)}，避免污染宿主应用的全局 JCE 配置。</p>
 *
 * <p><b>可用性</b>：classpath 上不存在 BouncyCastle 时，{@link TunnelCiphers} 不会注册本实现，
 * 配置 {@code cipher: sm4} 会给出「缺少依赖」的明确报错，而不是 NoClassDefFoundError。</p>
 */
public final class Sm4Cipher implements TunnelCipher {

    /** 算法标识，用于配置匹配；不进报文。 */
    public static final String ID = "sm4-cbc-hmac-sha256";

    /** 简便别名，yml 里写 {@code cipher: sm4} 亦可。 */
    public static final String ALIAS = "sm4";

    private static final String HMAC_ALGORITHM = "HmacSHA256";

    private static final int IV_LENGTH = 16;
    private static final int HMAC_LENGTH = 32;
    private static final int SM4_KEY_LENGTH = 16;

    private static final SecureRandom SECURE_RANDOM = new SecureRandom();

    /** 显式 Provider 实例，避免全局注册影响宿主应用。 */
    private static final Provider BC = new BouncyCastleProvider();

    /** BC 的 SM4 变换名：新版本用 PKCS7Padding，旧版本只有 PKCS5Padding，故运行时探测。 */
    private static final String TRANSFORMATION = resolveTransformation();

    @Override
    public String id() {
        return ID;
    }

    @Override
    public Collection<String> aliases() {
        return Collections.singletonList(ALIAS);
    }

    @Override
    public String seal(byte[] plaintext, String rawKey) {
        try {
            byte[] iv = new byte[IV_LENGTH];
            SECURE_RANDOM.nextBytes(iv);
            IvParameterSpec ivSpec = new IvParameterSpec(iv);

            Cipher cipher = Cipher.getInstance(TRANSFORMATION, BC);
            cipher.init(Cipher.ENCRYPT_MODE, deriveSm4Key(rawKey), ivSpec);
            byte[] ciphertext = cipher.doFinal(plaintext);

            Mac mac = Mac.getInstance(HMAC_ALGORITHM);
            mac.init(AesUtil.deriveHmacKey(rawKey));
            byte[] hmacBytes = mac.doFinal(AesUtil.concat(iv, ciphertext));

            return Base64.getEncoder().encodeToString(AesUtil.concat(iv, hmacBytes, ciphertext));
        } catch (Exception e) {
            throw new TunnelCryptoException("sm4 seal failed: " + e.getMessage(), e);
        }
    }

    @Override
    public byte[] open(String payload, String rawKey) {
        try {
            byte[] raw = Base64.getDecoder().decode(payload);
            if (raw.length < IV_LENGTH + HMAC_LENGTH) {
                throw new TunnelCryptoException("sm4 payload too short: " + raw.length + " bytes");
            }

            byte[] iv = AesUtil.subarray(raw, 0, IV_LENGTH);
            byte[] hmacReceived = AesUtil.subarray(raw, IV_LENGTH, IV_LENGTH + HMAC_LENGTH);
            byte[] ciphertext = AesUtil.subarray(raw, IV_LENGTH + HMAC_LENGTH, raw.length);

            Mac mac = Mac.getInstance(HMAC_ALGORITHM);
            mac.init(AesUtil.deriveHmacKey(rawKey));
            byte[] hmacComputed = mac.doFinal(AesUtil.concat(iv, ciphertext));
            if (!MessageDigest.isEqual(hmacReceived, hmacComputed)) {
                throw new SecurityException("HMAC verification failed - data may be tampered");
            }

            Cipher cipher = Cipher.getInstance(TRANSFORMATION, BC);
            cipher.init(Cipher.DECRYPT_MODE, deriveSm4Key(rawKey), new IvParameterSpec(iv));
            return cipher.doFinal(ciphertext);
        } catch (RuntimeException e) {
            throw new TunnelCryptoException("sm4 open failed: " + e.getMessage(), e);
        } catch (Exception e) {
            throw new TunnelCryptoException("sm4 open failed: " + e.getMessage(), e);
        }
    }

    /**
     * @return SM4-128 密钥：SHA-256("SM4:" + rawKey) 的前 16 字节
     */
    private static SecretKeySpec deriveSm4Key(String rawKey) {
        try {
            MessageDigest sha = MessageDigest.getInstance("SHA-256");
            byte[] digest = sha.digest(("SM4:" + rawKey).getBytes(StandardCharsets.UTF_8));
            return new SecretKeySpec(Arrays.copyOf(digest, SM4_KEY_LENGTH), "SM4");
        } catch (Exception e) {
            throw new TunnelCryptoException("failed to derive SM4 key", e);
        }
    }

    private static String resolveTransformation() {
        String[] candidates = {"SM4/CBC/PKCS7Padding", "SM4/CBC/PKCS5Padding"};
        for (String t : candidates) {
            try {
                Cipher.getInstance(t, BC);
                return t;
            } catch (Exception ignored) {
                // 试下一个
            }
        }
        throw new TunnelCryptoException(
                "BouncyCastle 未提供可用的 SM4/CBC 变换，请升级 bcprov 版本");
    }
}
