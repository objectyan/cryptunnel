package io.github.objectyan.cryptunnel.core.crypto;

import javax.crypto.Cipher;
import javax.crypto.Mac;
import javax.crypto.spec.IvParameterSpec;
import javax.crypto.spec.SecretKeySpec;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.security.SecureRandom;
import java.util.Base64;

/**
 * AES 对称加密解密工具（核心库版，与原 sunrise AesUtil 字节级一致）
 *
 * 传输格式：Base64( IV[16字节] + HMAC[32字节] + 密文 )
 *
 * <p>密钥派生：</p>
 * <ul>
 *   <li>AES 密钥：SHA-256("AES:" + rawKey) → 取前 32 字节做 AES-256</li>
 *   <li>HMAC 密钥：SHA-256("HMAC:" + rawKey) → 32 字节做 HMAC-SHA256</li>
 * </ul>
 *
 * <p>认证密钥（authKey）仅参与认证报文内容，不参与加密密钥派生。</p>
 */
public final class AesUtil {

    private static final String AES_ALGORITHM = "AES";
    private static final String AES_TRANSFORMATION = "AES/CBC/PKCS5Padding";
    private static final String HMAC_ALGORITHM = "HmacSHA256";
    private static final int IV_LENGTH = 16;
    private static final int HMAC_LENGTH = 32;
    private static final int AES_KEY_LENGTH = 32;

    private static final SecureRandom SECURE_RANDOM = new SecureRandom();

    private AesUtil() {
        // utility
    }

    public static SecretKeySpec deriveAesKey(String rawKey) {
        try {
            MessageDigest sha = MessageDigest.getInstance("SHA-256");
            byte[] keyBytes = sha.digest(("AES:" + rawKey).getBytes(StandardCharsets.UTF_8));
            return new SecretKeySpec(keyBytes, AES_ALGORITHM);
        } catch (Exception e) {
            throw new RuntimeException("Failed to derive AES key", e);
        }
    }

    public static SecretKeySpec deriveHmacKey(String rawKey) {
        try {
            MessageDigest sha = MessageDigest.getInstance("SHA-256");
            byte[] keyBytes = sha.digest(("HMAC:" + rawKey).getBytes(StandardCharsets.UTF_8));
            return new SecretKeySpec(keyBytes, HMAC_ALGORITHM);
        } catch (Exception e) {
            throw new RuntimeException("Failed to derive HMAC key", e);
        }
    }

    private static IvParameterSpec generateIv() {
        byte[] iv = new byte[IV_LENGTH];
        SECURE_RANDOM.nextBytes(iv);
        return new IvParameterSpec(iv);
    }

    public static String encrypt(byte[] data, SecretKeySpec aesKey, SecretKeySpec hmacKey) {
        try {
            IvParameterSpec iv = generateIv();

            Cipher cipher = Cipher.getInstance(AES_TRANSFORMATION);
            cipher.init(Cipher.ENCRYPT_MODE, aesKey, iv);
            byte[] ciphertext = cipher.doFinal(data);

            Mac mac = Mac.getInstance(HMAC_ALGORITHM);
            mac.init(hmacKey);
            byte[] hmacBytes = mac.doFinal(concat(iv.getIV(), ciphertext));

            byte[] result = concat(iv.getIV(), hmacBytes, ciphertext);

            return Base64.getEncoder().encodeToString(result);
        } catch (Exception e) {
            throw new RuntimeException("AES encrypt failed", e);
        }
    }

    public static byte[] decrypt(String encryptedBase64, SecretKeySpec aesKey, SecretKeySpec hmacKey) {
        try {
            byte[] raw = Base64.getDecoder().decode(encryptedBase64);

            byte[] ivBytes = subarray(raw, 0, IV_LENGTH);
            byte[] hmacReceived = subarray(raw, IV_LENGTH, IV_LENGTH + HMAC_LENGTH);
            byte[] ciphertext = subarray(raw, IV_LENGTH + HMAC_LENGTH, raw.length);

            IvParameterSpec iv = new IvParameterSpec(ivBytes);
            Mac mac = Mac.getInstance(HMAC_ALGORITHM);
            mac.init(hmacKey);
            byte[] hmacComputed = mac.doFinal(concat(ivBytes, ciphertext));

            if (!MessageDigest.isEqual(hmacReceived, hmacComputed)) {
                throw new SecurityException("HMAC verification failed - data may be tampered");
            }

            Cipher cipher = Cipher.getInstance(AES_TRANSFORMATION);
            cipher.init(Cipher.DECRYPT_MODE, aesKey, iv);
            return cipher.doFinal(ciphertext);
        } catch (SecurityException e) {
            throw e;
        } catch (Exception e) {
            throw new RuntimeException("AES decrypt failed", e);
        }
    }

    static byte[] concat(byte[]... arrays) {
        int totalLen = 0;
        for (byte[] a : arrays) {
            totalLen += a.length;
        }
        byte[] result = new byte[totalLen];
        int offset = 0;
        for (byte[] a : arrays) {
            System.arraycopy(a, 0, result, offset, a.length);
            offset += a.length;
        }
        return result;
    }

    static byte[] subarray(byte[] src, int start, int end) {
        byte[] result = new byte[end - start];
        System.arraycopy(src, start, result, 0, end - start);
        return result;
    }
}
