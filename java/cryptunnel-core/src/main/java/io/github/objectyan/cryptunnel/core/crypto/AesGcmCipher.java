package io.github.objectyan.cryptunnel.core.crypto;

import javax.crypto.Cipher;
import javax.crypto.spec.GCMParameterSpec;
import javax.crypto.spec.SecretKeySpec;
import java.security.SecureRandom;
import java.util.Base64;

/**
 * AES-256-GCM 隧道加密算法（JDK 8 原生支持，无需额外依赖）。
 *
 * <p>GCM 本身是 AEAD（带认证的加密），密文尾部自带 16 字节认证标签，
 * 因此<b>不再需要外挂 HMAC</b>——再加一层 HMAC 只会增加长度和复杂度而不提升安全性。</p>
 *
 * <p>载荷格式（比默认算法更短：省掉 32 字节 HMAC，改用 16 字节 GCM 标签）：</p>
 * <pre>
 *   Base64( IV[12] || AES/GCM/NoPadding 密文（末 16 字节为 GCM 标签） )
 *   标签长度：128 bit
 *   密钥派生：SHA-256("AES:" + rawKey) —— 与默认算法一致，同一把 rawKey 可直接复用
 * </pre>
 *
 * <p><b>为什么 IV 是 12 字节</b>：GCM 的标准 nonce 长度为 96 bit。12 字节以外的长度
 * 需要额外的 GHASH 派生，且各实现支持度不一（.NET 的 {@code AesGcm} 直到 .NET 8
 * 才放开非 12 字节 nonce），统一用 12 字节可保证三端互通。</p>
 *
 * <p><b>注意</b>：AES-256 在 JDK 8u161 之前需要安装 JCE 无限制强度策略文件。
 * 该要求与默认的 {@link AesCbcHmacSha256Cipher} 相同，不是新增约束。</p>
 */
public final class AesGcmCipher implements TunnelCipher {

    /** 算法标识，用于配置匹配；不进报文。 */
    public static final String ID = "aes-256-gcm";

    private static final String TRANSFORMATION = "AES/GCM/NoPadding";
    private static final int IV_LENGTH = 12;
    private static final int TAG_LENGTH_BITS = 128;

    private static final SecureRandom SECURE_RANDOM = new SecureRandom();

    @Override
    public String id() {
        return ID;
    }

    @Override
    public String seal(byte[] plaintext, String rawKey) {
        try {
            byte[] iv = new byte[IV_LENGTH];
            SECURE_RANDOM.nextBytes(iv);

            Cipher cipher = Cipher.getInstance(TRANSFORMATION);
            cipher.init(Cipher.ENCRYPT_MODE, deriveKey(rawKey), new GCMParameterSpec(TAG_LENGTH_BITS, iv));
            byte[] ciphertext = cipher.doFinal(plaintext);

            return Base64.getEncoder().encodeToString(AesUtil.concat(iv, ciphertext));
        } catch (Exception e) {
            throw new TunnelCryptoException("aes-256-gcm seal failed: " + e.getMessage(), e);
        }
    }

    @Override
    public byte[] open(String payload, String rawKey) {
        try {
            byte[] raw = Base64.getDecoder().decode(payload);
            if (raw.length < IV_LENGTH + TAG_LENGTH_BITS / 8) {
                throw new TunnelCryptoException(
                        "aes-256-gcm payload too short: " + raw.length + " bytes");
            }

            byte[] iv = AesUtil.subarray(raw, 0, IV_LENGTH);
            byte[] ciphertext = AesUtil.subarray(raw, IV_LENGTH, raw.length);

            Cipher cipher = Cipher.getInstance(TRANSFORMATION);
            cipher.init(Cipher.DECRYPT_MODE, deriveKey(rawKey), new GCMParameterSpec(TAG_LENGTH_BITS, iv));
            return cipher.doFinal(ciphertext);
        } catch (TunnelCryptoException e) {
            throw e;
        } catch (Exception e) {
            // 密钥错误与数据篡改在 GCM 下都表现为 AEADBadTagException，
            // 统一包装，不区分二者（区分会泄露信息）。
            throw new TunnelCryptoException("aes-256-gcm open failed: " + e.getMessage(), e);
        }
    }

    /**
     * 与默认算法共用派生前缀，使同一把 rawKey 在切换算法时无需更换。
     */
    private static SecretKeySpec deriveKey(String rawKey) {
        return AesUtil.deriveAesKey(rawKey);
    }
}
