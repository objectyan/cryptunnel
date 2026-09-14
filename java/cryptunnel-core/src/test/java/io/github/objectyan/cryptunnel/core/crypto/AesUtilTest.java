package io.github.objectyan.cryptunnel.core.crypto;

import javax.crypto.spec.SecretKeySpec;
import org.junit.Test;
import static org.junit.Assert.assertArrayEquals;
import static org.junit.Assert.assertNotEquals;
import static org.junit.Assert.assertNotNull;
import static org.junit.Assert.assertTrue;
import static org.junit.Assert.fail;

public class AesUtilTest {

    @Test
    public void encryptDecrypt_roundTrip_preservesBytes() {
        String rawKey = "test-aes-key-32-bytes-long-padding";
        SecretKeySpec aes = AesUtil.deriveAesKey(rawKey);
        SecretKeySpec mac = AesUtil.deriveHmacKey(rawKey);

        byte[] plain = "Hello, jdbc proxy tunnel!".getBytes(java.nio.charset.StandardCharsets.UTF_8);
        String encrypted = AesUtil.encrypt(plain, aes, mac);
        byte[] decrypted = AesUtil.decrypt(encrypted, aes, mac);

        assertArrayEquals(plain, decrypted);
    }

    @Test
    public void encrypt_eachCallProducesDifferentCiphertext_dueToRandomIv() {
        String rawKey = "test-aes-key-32-bytes-long-padding";
        SecretKeySpec aes = AesUtil.deriveAesKey(rawKey);
        SecretKeySpec mac = AesUtil.deriveHmacKey(rawKey);

        byte[] plain = "same plaintext".getBytes();
        String c1 = AesUtil.encrypt(plain, aes, mac);
        String c2 = AesUtil.encrypt(plain, aes, mac);
        // 随机 IV 必须产生不同密文（base64 不同）
        assertNotEquals(c1, c2);

        // 但两者都能解出原文
        assertArrayEquals(plain, AesUtil.decrypt(c1, aes, mac));
        assertArrayEquals(plain, AesUtil.decrypt(c2, aes, mac));
    }

    @Test
    public void decrypt_withDifferentKey_fails() {
        SecretKeySpec aes1 = AesUtil.deriveAesKey("key-one-aaaaaaaaaaaaaaaaa");
        SecretKeySpec mac1 = AesUtil.deriveHmacKey("key-one-aaaaaaaaaaaaaaaaa");
        SecretKeySpec aes2 = AesUtil.deriveAesKey("key-two-bbbbbbbbbbbbbbbbbb");
        SecretKeySpec mac2 = AesUtil.deriveHmacKey("key-two-bbbbbbbbbbbbbbbbbb");

        byte[] plain = "secret".getBytes();
        String encrypted = AesUtil.encrypt(plain, aes1, mac1);

        try {
            AesUtil.decrypt(encrypted, aes2, mac2);
            fail("expected SecurityException for HMAC mismatch");
        } catch (SecurityException e) {
            assertTrue(e.getMessage().contains("HMAC"));
        }
    }

    @Test
    public void deriveAesAndHmacKeys_areDifferent() {
        String raw = "raw-key";
        SecretKeySpec a = AesUtil.deriveAesKey(raw);
        SecretKeySpec b = AesUtil.deriveHmacKey(raw);
        assertNotNull(a);
        assertNotNull(b);
        // 密钥字节不同（不同前缀域分离）
        assertNotEquals(new String(a.getEncoded()), new String(b.getEncoded()));
    }
}
