using System;
using System.Security.Cryptography;
using System.Text;

namespace Cryptunnel.Core.Crypto;

/// <summary>
/// AES 对称加密，1:1 移植自 Java 版 AesUtil。
/// 派生：SHA-256("AES:"+key) / SHA-256("HMAC:"+key)
/// 加密：AES/CBC/PKCS7Padding + 随机 IV + HMAC-SHA256(IV||密文)
/// 传输格式：Base64( IV[16] || HMAC[32] || 密文 )
/// 与服务端字节级兼容。
/// </summary>
public sealed class AesCrypto
{
    // 注意：这里刻意不缓存 SHA256 实例。
    // HashAlgorithm 的实例方法不是线程安全的——多条隧道并发派生密钥时，
    // 共享一个 SHA256.Create() 实例会让内部状态互相踩踏，
    // 结果是「偶发地算出一把错误的密钥」，表现为随机的认证失败/解密失败，极难复现。
    // SHA256.HashData 是静态一次性 API，无共享状态。
    public static byte[] DeriveAesKey(string rawKey)
    {
        return SHA256.HashData(Encoding.UTF8.GetBytes("AES:" + rawKey));
    }

    public static byte[] DeriveHmacKey(string rawKey)
    {
        return SHA256.HashData(Encoding.UTF8.GetBytes("HMAC:" + rawKey));
    }

    // 入参用 ReadOnlySpan<byte> 而非 byte[]：byte[] 可隐式转换，现有调用方零改动，
    // 同时让 AesCbcHmacSha256Cipher 走 SPI 时无需为了凑签名而 ToArray() 多拷一份明文。
    public static string Encrypt(ReadOnlySpan<byte> data, byte[] aesKey, byte[] hmacKey)
    {
        using var aes = Aes.Create();
        aes.Key = aesKey;
        aes.GenerateIV();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        byte[] ciphertext;
        using (var enc = aes.CreateEncryptor())
        using (var ms = new MemoryStream())
        using (var cs = new CryptoStream(ms, enc, CryptoStreamMode.Write))
        {
            cs.Write(data);
            cs.FlushFinalBlock();
            ciphertext = ms.ToArray();
        }

        byte[] iv = aes.IV;
        byte[] mac;
        using (var hmac = new HMACSHA256(hmacKey))
            mac = hmac.ComputeHash(Concat(iv, ciphertext));

        return Convert.ToBase64String(Concat(iv, mac, ciphertext));
    }

    public static byte[] Decrypt(string encryptedBase64, byte[] aesKey, byte[] hmacKey)
    {
        var raw = Convert.FromBase64String(encryptedBase64);

        const int ivLen = 16;
        const int macLen = 32;
        if (raw.Length < ivLen + macLen)
            throw new CryptographicException("数据过短，疑似损坏");

        var iv = raw[..ivLen];
        var mac = raw[ivLen..(ivLen + macLen)];
        var ciphertext = raw[(ivLen + macLen)..];

        using var hmac = new HMACSHA256(hmacKey);
        var computed = hmac.ComputeHash(Concat(iv, ciphertext));
        if (!CryptographicOperations.FixedTimeEquals(mac, computed))
            throw new CryptographicException("HMAC 校验失败，数据可能被篡改");

        using var aes = Aes.Create();
        aes.Key = aesKey;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        using var dec = aes.CreateDecryptor();
        using var ms = new MemoryStream();
        using (var cs = new CryptoStream(ms, dec, CryptoStreamMode.Write))
        {
            cs.Write(ciphertext, 0, ciphertext.Length);
            cs.FlushFinalBlock();
        }
        return ms.ToArray();
    }

    private static byte[] Concat(params byte[][] arrays)
    {
        var total = 0;
        foreach (var a in arrays) total += a.Length;
        var result = new byte[total];
        var off = 0;
        foreach (var a in arrays)
        {
            Buffer.BlockCopy(a, 0, result, off, a.Length);
            off += a.Length;
        }
        return result;
    }
}
