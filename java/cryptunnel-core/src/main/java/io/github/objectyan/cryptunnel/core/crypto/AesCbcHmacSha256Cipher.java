package io.github.objectyan.cryptunnel.core.crypto;

/**
 * 默认隧道加密算法：AES-256-CBC + HMAC-SHA256。
 *
 * <p><b>本实现直接委派给 {@link AesUtil}</b>，而非重新实现——从构造上保证
 * 输出与改动前的固定实现<b>字节级等价</b>，这是「老客户端零改动」承诺的底线。</p>
 *
 * <p>载荷格式（与历史实现完全一致）：</p>
 * <pre>
 *   Base64( IV[16] || HMAC[32] || AES/CBC/PKCS5Padding 密文 )
 *   HMAC 覆盖范围：IV || 密文
 *   密钥派生：SHA-256("AES:" + rawKey) / SHA-256("HMAC:" + rawKey)
 * </pre>
 */
public final class AesCbcHmacSha256Cipher implements TunnelCipher {

    /** 算法标识，用于配置匹配；不进报文。 */
    public static final String ID = "aes-256-cbc-hmac-sha256";

    @Override
    public String id() {
        return ID;
    }

    @Override
    public String seal(byte[] plaintext, String rawKey) {
        return AesUtil.encrypt(plaintext,
                AesUtil.deriveAesKey(rawKey),
                AesUtil.deriveHmacKey(rawKey));
    }

    @Override
    public byte[] open(String payload, String rawKey) {
        try {
            return AesUtil.decrypt(payload,
                    AesUtil.deriveAesKey(rawKey),
                    AesUtil.deriveHmacKey(rawKey));
        } catch (RuntimeException e) {
            // 统一异常类型。原因保留在 cause 中（HMAC 失败时为 SecurityException）。
            throw new TunnelCryptoException("aes-cbc-hmac-sha256 open failed: " + e.getMessage(), e);
        }
    }
}
