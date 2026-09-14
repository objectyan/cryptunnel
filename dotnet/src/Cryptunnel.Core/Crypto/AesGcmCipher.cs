using System;
using System.Security.Cryptography;

namespace Cryptunnel.Core.Crypto;

/// <summary>
/// AES-256-GCM 隧道加密算法，与 Java 侧 <c>AesGcmCipher</c> 字节级对应。
///
/// GCM 本身是 AEAD，密文尾部自带 16 字节认证标签，故<b>不再外挂 HMAC</b>。
///
/// 载荷格式（比默认算法短 24 字节：省掉 32 字节 HMAC，改用 16 字节标签，nonce 也从 16 减到 12）：
/// <code>
///   Base64( nonce[12] || 密文 || tag[16] )
///   密钥派生：SHA256("AES:" + rawKey) —— 与默认算法一致，同一把 rawKey 可直接复用
/// </code>
///
/// <b>为什么 nonce 是 12 字节</b>：GCM 标准 nonce 长度为 96 bit，
/// 非 12 字节需要额外 GHASH 派生且各实现支持度不一，统一用 12 字节保证三端互通。
///
/// <b>与 Java 侧的一处结构差异需注意</b>：.NET 的 <see cref="AesGcm"/> 把 tag 与密文分开传参，
/// 而 Java 的 <c>Cipher</c> 把 tag 追加在密文尾部。本实现按 Java 的布局拼装，
/// 即 tag 位于载荷末尾，两端才能互解。
/// </summary>
public sealed class AesGcmCipher : ITunnelCipher
{
    /// <summary>算法标识，与 Java 侧 <c>AesGcmCipher.ID</c> 完全一致。</summary>
    public const string CipherId = "aes-256-gcm";

    private const int NonceLength = 12;
    private const int TagLength = 16;

    public string Id => CipherId;

    public string Seal(ReadOnlySpan<byte> plaintext, string rawKey)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        var key = AesCrypto.DeriveAesKey(rawKey);

        // 布局：nonce || 密文 || tag。tag 追加在尾部以匹配 Java 的 Cipher 输出。
        var result = new byte[NonceLength + plaintext.Length + TagLength];
        nonce.CopyTo(result.AsSpan(0, NonceLength));

        using var gcm = new AesGcm(key, TagLength);
        gcm.Encrypt(
            nonce,
            plaintext,
            result.AsSpan(NonceLength, plaintext.Length),
            result.AsSpan(NonceLength + plaintext.Length, TagLength));

        return Convert.ToBase64String(result);
    }

    public byte[] Open(string payload, string rawKey)
    {
        var raw = Convert.FromBase64String(payload);
        if (raw.Length < NonceLength + TagLength)
            throw new CryptographicException(
                $"aes-256-gcm 载荷长度不足：{raw.Length} 字节，至少需要 {NonceLength + TagLength} 字节（nonce+tag）");

        var cipherLength = raw.Length - NonceLength - TagLength;
        var nonce = raw.AsSpan(0, NonceLength);
        var ciphertext = raw.AsSpan(NonceLength, cipherLength);
        var tag = raw.AsSpan(NonceLength + cipherLength, TagLength);

        var plaintext = new byte[cipherLength];
        using var gcm = new AesGcm(AesCrypto.DeriveAesKey(rawKey), TagLength);
        // 密钥错误与数据篡改都表现为 AuthenticationTagMismatchException（CryptographicException 子类），
        // 刻意不区分二者——区分会泄露信息。
        gcm.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }
}
