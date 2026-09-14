using Cryptunnel.Core.Crypto;

namespace CryptoParity;

/// <summary>
/// 导出 C# 侧加密的载荷，供 Java 侧解密验证——补齐「C# 加密 -> Java 解密」方向。
///
/// 输出格式与 vectors 文件一致（KEY=VALUE），便于 Java 端直接解析。
/// </summary>
internal static class Exporter
{
    public static void Run(Vectors v)
    {
        Console.WriteLine("# C# 侧加密产物，供 Java 解密校验（反方向对齐）");
        Console.WriteLine($"RAW_KEY={v.RawKey}");
        Console.WriteLine($"PLAIN={Encoding.UTF8.GetString(v.Plain)}");
        Console.WriteLine($"DOTNET_CBC_PAYLOAD={CipherRegistry.Default.Seal(v.Plain, v.RawKey)}");
        Console.WriteLine($"DOTNET_GCM_PAYLOAD={CipherRegistry.Get(AesGcmCipher.CipherId).Seal(v.Plain, v.RawKey)}");
        Console.WriteLine($"DOTNET_SM4_PAYLOAD={CipherRegistry.Get(Sm4Cipher.CipherId).Seal(v.Plain, v.RawKey)}");
    }
}
