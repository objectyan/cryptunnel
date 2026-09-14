using Cryptunnel.Core.Crypto;

namespace CryptoParity;

/// <summary>密钥派生对齐：确定性输出，是最强的跨语言校验项。</summary>
internal static class KeyDerivationChecks
{
    public static void Run(Vectors v, Action<string, Func<bool>> check)
    {
        Console.WriteLine("[1] 密钥派生（确定性，必须逐字节等于 Java）");

        check("AES 密钥  = SHA256(\"AES:\"  + rawKey)", () =>
            Vectors.ToHex(AesCrypto.DeriveAesKey(v.RawKey)) == v["AES_KEY"]);

        check("HMAC 密钥 = SHA256(\"HMAC:\" + rawKey)", () =>
            Vectors.ToHex(AesCrypto.DeriveHmacKey(v.RawKey)) == v["HMAC_KEY"]);

        check("SM4 密钥  = SHA256(\"SM4:\"  + rawKey)[..16]", () =>
            Vectors.ToHex(Sm4Cipher.DeriveSm4Key(v.RawKey)) == v["SM4_KEY"]);

        check("SM4 密钥长度为 16 字节（SM4-128）", () =>
            Sm4Cipher.DeriveSm4Key(v.RawKey).Length == 16);

        check("SM4 与 AES 派生前缀不同 -> 密钥不相关", () =>
        {
            var sm4 = Sm4Cipher.DeriveSm4Key(v.RawKey);
            var aes = AesCrypto.DeriveAesKey(v.RawKey);
            return !sm4.AsSpan().SequenceEqual(aes.AsSpan(0, 16));
        });

        Console.WriteLine();
    }
}

/// <summary>SM4 分组函数校验：对齐国家标准官方向量，不依赖任何第三方实现。</summary>
internal static class Sm4EngineChecks
{
    public static void Run(Vectors v, Action<string, Func<bool>> check)
    {
        Console.WriteLine("[2] SM4 分组函数（GB/T 32907-2016 附录 A.1 官方向量）");

        var key = Vectors.FromHex(v["GBT_A1_KEY"]);
        var plain = Vectors.FromHex(v["GBT_A1_PLAIN"]);
        var expected = v["GBT_A1_BLOCK"];

        check($"单分组加密 = {expected}", () =>
        {
            var engine = new Sm4Engine(key);
            var output = new byte[Sm4Engine.BlockSize];
            engine.EncryptBlock(plain, output);
            return Vectors.ToHex(output) == expected;
        });

        check("单分组解密可还原明文（轮密钥逆序正确）", () =>
        {
            var engine = new Sm4Engine(key);
            var cipher = Vectors.FromHex(expected);
            var output = new byte[Sm4Engine.BlockSize];
            engine.DecryptBlock(cipher, output);
            return Vectors.ToHex(output) == v["GBT_A1_PLAIN"];
        });

        check("非 16 字节密钥被拒绝", () =>
            Program.Throws(() => new Sm4Engine(new byte[15])));

        check("非 16 字节分组被拒绝", () =>
            Program.Throws(() =>
            {
                var engine = new Sm4Engine(key);
                engine.EncryptBlock(new byte[8], new byte[Sm4Engine.BlockSize]);
            }));

        Console.WriteLine();
    }
}
