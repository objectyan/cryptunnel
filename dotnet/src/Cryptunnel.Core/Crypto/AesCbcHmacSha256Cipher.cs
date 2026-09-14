using System;
using System.Security.Cryptography;

namespace Cryptunnel.Core.Crypto;

/// <summary>
/// 默认隧道加密算法：AES-256-CBC + HMAC-SHA256。
///
/// 本实现直接委派给 <see cref="AesCrypto"/>，而非重新实现——
/// 从构造上保证输出与历史版本<b>字节级等价</b>，这是「现役用户的隧道不能因升级断掉」的底线。
///
/// 载荷格式：Base64( IV[16] || HMAC[32] || AES/CBC/PKCS7 密文 )
/// </summary>
public sealed class AesCbcHmacSha256Cipher : ITunnelCipher
{
    /// <summary>算法标识，与 Java 侧 <c>AesCbcHmacSha256Cipher.ID</c> 完全一致。</summary>
    public const string CipherId = "aes-256-cbc-hmac-sha256";

    public string Id => CipherId;

    public string Seal(ReadOnlySpan<byte> plaintext, string rawKey) =>
        AesCrypto.Encrypt(plaintext, AesCrypto.DeriveAesKey(rawKey), AesCrypto.DeriveHmacKey(rawKey));

    public byte[] Open(string payload, string rawKey) =>
        AesCrypto.Decrypt(payload, AesCrypto.DeriveAesKey(rawKey), AesCrypto.DeriveHmacKey(rawKey));
}
