using Cryptunnel.Core.Crypto;

namespace CryptoParity;

/// <summary>
/// 解开 Java 侧真实产出的载荷 —— 验证「Java 加密 -> C# 解密」方向。
/// 反方向（C# 加密 -> Java 解密）由 run.sh 调 Java 侧完成。
/// </summary>
internal static class PayloadChecks
{
    public static void Run(Vectors v, Action<string, Func<bool>> check)
    {
        Console.WriteLine("[3] 解开 Java 侧真实载荷（Java 加密 -> C# 解密）");

        var expected = v["PLAIN"];

        check("默认算法 aes-256-cbc-hmac-sha256", () =>
        {
            var plain = CipherRegistry.Default.Open(v["JAVA_CIPHER"], v.RawKey);
            return Encoding.UTF8.GetString(plain) == expected;
        });

        check("sm4-cbc-hmac-sha256", () =>
        {
            var plain = CipherRegistry.Get(Sm4Cipher.CipherId).Open(v["JAVA_SM4_PAYLOAD"], v.RawKey);
            return Encoding.UTF8.GetString(plain) == expected;
        });

        check("aes-256-gcm", () =>
        {
            var plain = CipherRegistry.Get(AesGcmCipher.CipherId).Open(v["JAVA_GCM_PAYLOAD"], v.RawKey);
            return Encoding.UTF8.GetString(plain) == expected;
        });

        check("SM4 载荷帧长与默认算法一致（IV16+HMAC32+密文）", () =>
        {
            var sm4 = Convert.FromBase64String(v["JAVA_SM4_PAYLOAD"]).Length;
            var cbc = Convert.FromBase64String(v["JAVA_CIPHER"]).Length;
            return sm4 == cbc;
        });

        check("GCM 载荷比默认算法短 24 字节（省 HMAC、nonce 缩短）", () =>
        {
            var gcm = Convert.FromBase64String(v["JAVA_GCM_PAYLOAD"]).Length;
            var cbc = Convert.FromBase64String(v["JAVA_CIPHER"]).Length;
            return cbc - gcm == 24;
        });

        Console.WriteLine();
    }
}

/// <summary>三种算法各自的 round-trip 与边界行为。</summary>
internal static class RoundTripChecks
{
    public static void Run(Vectors v, Action<string, Func<bool>> check)
    {
        Console.WriteLine("[4] 本地 round-trip 与边界");

        foreach (var id in new[]
                 {
                     AesCbcHmacSha256Cipher.CipherId,
                     AesGcmCipher.CipherId,
                     Sm4Cipher.CipherId,
                 })
        {
            var cipher = CipherRegistry.Get(id);

            check($"{id}: round-trip 基准明文", () =>
            {
                var payload = cipher.Seal(v.Plain, v.RawKey);
                return cipher.Open(payload, v.RawKey).AsSpan().SequenceEqual(v.Plain);
            });

            check($"{id}: 空明文", () =>
                cipher.Open(cipher.Seal(ReadOnlySpan<byte>.Empty, v.RawKey), v.RawKey).Length == 0);

            check($"{id}: 64KB 大报文（隧道分片上限量级）", () =>
            {
                var big = RandomNumberGenerator.GetBytes(65536);
                return cipher.Open(cipher.Seal(big, v.RawKey), v.RawKey).AsSpan().SequenceEqual(big);
            });

            check($"{id}: 正好一个分组长度（16 字节，验 PKCS7 补满整块）", () =>
            {
                var block = RandomNumberGenerator.GetBytes(16);
                return cipher.Open(cipher.Seal(block, v.RawKey), v.RawKey).AsSpan().SequenceEqual(block);
            });

            check($"{id}: 相同明文两次加密结果不同（随机 IV/nonce）", () =>
                cipher.Seal(v.Plain, v.RawKey) != cipher.Seal(v.Plain, v.RawKey));

            check($"{id}: 错误密钥必须失败", () =>
                Program.Throws(() => cipher.Open(cipher.Seal(v.Plain, v.RawKey), v.RawKey + "x")));

            check($"{id}: 篡改末字节必须被检测", () =>
            {
                var raw = Convert.FromBase64String(cipher.Seal(v.Plain, v.RawKey));
                raw[^1] ^= 0xFF;
                return Program.Throws(() => cipher.Open(Convert.ToBase64String(raw), v.RawKey));
            });

            check($"{id}: 篡改 IV/nonce 首字节必须被检测", () =>
            {
                var raw = Convert.FromBase64String(cipher.Seal(v.Plain, v.RawKey));
                raw[0] ^= 0xFF;
                return Program.Throws(() => cipher.Open(Convert.ToBase64String(raw), v.RawKey));
            });

            check($"{id}: 过短载荷被拒绝而非越界", () =>
                Program.Throws(() => cipher.Open(Convert.ToBase64String(new byte[4]), v.RawKey)));
        }

        Console.WriteLine();
    }
}
