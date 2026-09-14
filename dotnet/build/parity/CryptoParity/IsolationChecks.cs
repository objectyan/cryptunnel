using Cryptunnel.Core.Crypto;

namespace CryptoParity;

/// <summary>
/// 跨算法隔离与注册表行为。
///
/// 按 ADR-0003，报文内不含算法标识，两端靠配置约定一致。
/// 因此「配置不一致」必须表现为<b>硬失败</b>，绝不能解出垃圾数据当成合法明文。
/// </summary>
internal static class IsolationChecks
{
    public static void Run(Vectors v, Action<string, Func<bool>> check)
    {
        Console.WriteLine("[5] 跨算法隔离与注册表");

        var cbc = CipherRegistry.Get(AesCbcHmacSha256Cipher.CipherId);
        var gcm = CipherRegistry.Get(AesGcmCipher.CipherId);
        var sm4 = CipherRegistry.Get(Sm4Cipher.CipherId);

        // CBC 与 SM4 帧结构相同、HMAC 密钥相同 -> HMAC 会通过，
        // 必须靠分组算法不同导致解密/填充失败来兜住。这条是本组里最关键的一项。
        check("CBC 载荷 -> SM4 解密：硬失败", () =>
            Program.Throws(() => sm4.Open(cbc.Seal(v.Plain, v.RawKey), v.RawKey)));

        check("SM4 载荷 -> CBC 解密：硬失败", () =>
            Program.Throws(() => cbc.Open(sm4.Seal(v.Plain, v.RawKey), v.RawKey)));

        check("CBC 载荷 -> GCM 解密：硬失败", () =>
            Program.Throws(() => gcm.Open(cbc.Seal(v.Plain, v.RawKey), v.RawKey)));

        check("GCM 载荷 -> CBC 解密：硬失败", () =>
            Program.Throws(() => cbc.Open(gcm.Seal(v.Plain, v.RawKey), v.RawKey)));

        check("SM4 载荷 -> GCM 解密：硬失败", () =>
            Program.Throws(() => gcm.Open(sm4.Seal(v.Plain, v.RawKey), v.RawKey)));

        check("GCM 载荷 -> SM4 解密：硬失败", () =>
            Program.Throws(() => sm4.Open(gcm.Seal(v.Plain, v.RawKey), v.RawKey)));

        check("注册表：留空回落默认算法（兼容旧配置）", () =>
            CipherRegistry.Get(null).Id == AesCbcHmacSha256Cipher.CipherId
            && CipherRegistry.Get("").Id == AesCbcHmacSha256Cipher.CipherId
            && CipherRegistry.Get("   ").Id == AesCbcHmacSha256Cipher.CipherId);

        check("注册表：sm4 别名映射到完整标识", () =>
            CipherRegistry.Get(Sm4Cipher.Alias).Id == Sm4Cipher.CipherId);

        check("注册表：标识大小写不敏感", () =>
            CipherRegistry.Get("AES-256-GCM").Id == AesGcmCipher.CipherId);

        check("注册表：未知标识抛错并列出可用项", () =>
        {
            try
            {
                CipherRegistry.Get("rot13");
                return false;
            }
            catch (ArgumentException e)
            {
                return e.Message.Contains(AesGcmCipher.CipherId, StringComparison.Ordinal);
            }
        });

        check("注册表：三种算法 + sm4 别名共 4 个键", () =>
            CipherRegistry.Ids.Count == 4
            && CipherRegistry.Contains(AesCbcHmacSha256Cipher.CipherId)
            && CipherRegistry.Contains(AesGcmCipher.CipherId)
            && CipherRegistry.Contains(Sm4Cipher.CipherId)
            && CipherRegistry.Contains(Sm4Cipher.Alias));

        Console.WriteLine();
    }
}
