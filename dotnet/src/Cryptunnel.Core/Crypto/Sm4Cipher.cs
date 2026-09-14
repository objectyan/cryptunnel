using System;
using System.Security.Cryptography;
using System.Text;

namespace Cryptunnel.Core.Crypto;

/// <summary>
/// 国密 SM4 隧道加密：SM4-CBC + HMAC-SHA256（encrypt-then-MAC），
/// 与 Java 侧 <c>Sm4Cipher</c> 字节级对应。
///
/// 载荷格式与默认算法 <see cref="AesCbcHmacSha256Cipher"/> <b>完全一致</b>，
/// 只把分组算法从 AES 换成 SM4——帧结构、HMAC 覆盖范围、IV 长度全都不变：
/// <code>
///   Base64( IV[16] || HMAC-SHA256[32] || SM4/CBC/PKCS7 密文 )
///   HMAC 覆盖范围：IV || 密文（不含 HMAC 字段本身）
///   SM4  密钥：SHA256("SM4:"  + rawKey) 的前 16 字节
///   HMAC 密钥：SHA256("HMAC:" + rawKey)（与默认算法共用）
/// </code>
///
/// <b>关于 PKCS7 与 Java 的 PKCS5Padding</b>：Java/BC 对 16 字节分组算法所谓的
/// "PKCS5Padding" 实际就是 PKCS7，两端填充逐字节相同。
///
/// <b>为什么不用 BouncyCastle</b>：见 <see cref="Sm4Engine"/> 的说明——
/// 分组函数自研 + 官方测试向量守护，避免给单文件客户端加 4MB 第三方依赖。
/// </summary>
public sealed class Sm4Cipher : ITunnelCipher
{
    /// <summary>算法标识，与 Java 侧 <c>Sm4Cipher.ID</c> 完全一致。</summary>
    public const string CipherId = "sm4-cbc-hmac-sha256";

    /// <summary>简便别名，配置里写 <c>sm4</c> 亦可。</summary>
    public const string Alias = "sm4";

    private const int IvLength = 16;
    private const int HmacLength = 32;
    private const int HeaderLength = IvLength + HmacLength;
    private const int Sm4KeyLength = 16;

    public string Id => CipherId;

    /// <summary>SM4-128 密钥：SHA256("SM4:" + rawKey) 的前 16 字节。</summary>
    internal static byte[] DeriveSm4Key(string rawKey)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes("SM4:" + rawKey));
        return digest[..Sm4KeyLength];
    }

    public string Seal(ReadOnlySpan<byte> plaintext, string rawKey)
    {
        var iv = RandomNumberGenerator.GetBytes(IvLength);
        var ciphertext = EncryptCbcPkcs7(plaintext, DeriveSm4Key(rawKey), iv);

        // HMAC 覆盖 (IV || 密文)
        var macInput = new byte[IvLength + ciphertext.Length];
        iv.CopyTo(macInput, 0);
        ciphertext.CopyTo(macInput, IvLength);
        var mac = HMACSHA256.HashData(AesCrypto.DeriveHmacKey(rawKey), macInput);

        var result = new byte[HeaderLength + ciphertext.Length];
        iv.CopyTo(result, 0);
        mac.CopyTo(result, IvLength);
        ciphertext.CopyTo(result, HeaderLength);

        return Convert.ToBase64String(result);
    }

    public byte[] Open(string payload, string rawKey)
    {
        var raw = Convert.FromBase64String(payload);
        if (raw.Length < HeaderLength)
            throw new CryptographicException(
                $"sm4 载荷长度不足：{raw.Length} 字节，至少需要 {HeaderLength} 字节（IV+HMAC）");

        var iv = raw.AsSpan(0, IvLength);
        var receivedMac = raw.AsSpan(IvLength, HmacLength);
        var ciphertext = raw.AsSpan(HeaderLength);

        var macInput = new byte[IvLength + ciphertext.Length];
        iv.CopyTo(macInput);
        ciphertext.CopyTo(macInput.AsSpan(IvLength));
        var computedMac = HMACSHA256.HashData(AesCrypto.DeriveHmacKey(rawKey), macInput);

        if (!CryptographicOperations.FixedTimeEquals(receivedMac, computedMac))
            throw new CryptographicException("HMAC 校验失败，数据可能被篡改或密钥不一致");

        return DecryptCbcPkcs7(ciphertext, DeriveSm4Key(rawKey), iv);
    }

    /// <summary>SM4-CBC 加密 + PKCS7 填充。明文长度为分组整数倍时也补满一整块。</summary>
    private static byte[] EncryptCbcPkcs7(ReadOnlySpan<byte> plaintext, byte[] key, ReadOnlySpan<byte> iv)
    {
        var padLength = Sm4Engine.BlockSize - plaintext.Length % Sm4Engine.BlockSize;
        var total = plaintext.Length + padLength;

        var buffer = new byte[total];
        plaintext.CopyTo(buffer);
        buffer.AsSpan(plaintext.Length).Fill((byte)padLength);

        var engine = new Sm4Engine(key);
        var output = new byte[total];
        Span<byte> previous = stackalloc byte[Sm4Engine.BlockSize];
        iv.CopyTo(previous);
        Span<byte> block = stackalloc byte[Sm4Engine.BlockSize];

        for (var offset = 0; offset < total; offset += Sm4Engine.BlockSize)
        {
            for (var i = 0; i < Sm4Engine.BlockSize; i++)
                block[i] = (byte)(buffer[offset + i] ^ previous[i]);

            var target = output.AsSpan(offset, Sm4Engine.BlockSize);
            engine.EncryptBlock(block, target);
            target.CopyTo(previous);
        }

        CryptographicOperations.ZeroMemory(buffer);
        return output;
    }

    /// <summary>SM4-CBC 解密 + 剥离 PKCS7 填充。</summary>
    private static byte[] DecryptCbcPkcs7(ReadOnlySpan<byte> ciphertext, byte[] key, ReadOnlySpan<byte> iv)
    {
        if (ciphertext.Length == 0 || ciphertext.Length % Sm4Engine.BlockSize != 0)
            throw new CryptographicException(
                $"sm4 密文长度 {ciphertext.Length} 不是分组长度 {Sm4Engine.BlockSize} 的整数倍");

        var engine = new Sm4Engine(key);
        var plain = new byte[ciphertext.Length];
        Span<byte> previous = stackalloc byte[Sm4Engine.BlockSize];
        iv.CopyTo(previous);
        Span<byte> decrypted = stackalloc byte[Sm4Engine.BlockSize];

        for (var offset = 0; offset < ciphertext.Length; offset += Sm4Engine.BlockSize)
        {
            var current = ciphertext.Slice(offset, Sm4Engine.BlockSize);
            engine.DecryptBlock(current, decrypted);
            for (var i = 0; i < Sm4Engine.BlockSize; i++)
                plain[offset + i] = (byte)(decrypted[i] ^ previous[i]);
            current.CopyTo(previous);
        }

        var padLength = plain[^1];
        if (padLength is 0 || padLength > Sm4Engine.BlockSize || padLength > plain.Length)
            throw new CryptographicException("sm4 PKCS7 填充非法，密钥不匹配或数据损坏");
        for (var i = plain.Length - padLength; i < plain.Length; i++)
        {
            if (plain[i] != padLength)
                throw new CryptographicException("sm4 PKCS7 填充非法，密钥不匹配或数据损坏");
        }

        var result = plain[..^padLength];
        CryptographicOperations.ZeroMemory(plain);
        return result;
    }
}
