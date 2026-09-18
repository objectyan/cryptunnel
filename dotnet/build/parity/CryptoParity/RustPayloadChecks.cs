using Cryptunnel.Core.Crypto;

namespace CryptoParity;

/// <summary>
/// 解开 Rust 侧现场加密的载荷 —— 验证「Rust 加密 -> C# 解密」这个反方向。
///
/// 与 <see cref="PayloadChecks"/>（Java 加密 -> C# 解密）互补：
/// 正方向证明 C# 能读别人，这里证明 Rust 产出的东西 C# 也能读。
/// 两个方向都过，才能认定 Rust 端与现有 .NET/Java 实现字节级互通。
///
/// 输入文件是 `cargo run --bin parity -- --export` 的输出（键值对，含 RUST_AES_CBC/RUST_SM4_CBC/RUST_AES_GCM）。
/// </summary>
internal static class RustPayloadChecks
{
    public static int Run(string path)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch (IOException) { }

        var lines = File.ReadAllLines(path);
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var idx = line.IndexOf('=');
            if (idx <= 0) continue;
            map[line[..idx].Trim()] = line[(idx + 1)..].Trim();
        }

        string Require(string k) => map.TryGetValue(k, out var v)
            ? v
            : throw new InvalidDataException($"Rust 导出文件缺少键 {k}");

        var rawKey = Require("RAW_KEY");
        var expected = Require("PLAIN");
        int passed = 0, failed = 0;

        void Check(string name, Func<bool> assertion)
        {
            bool ok;
            string extra = string.Empty;
            try { ok = assertion(); }
            catch (Exception e) { ok = false; extra = $" -> {e.GetType().Name}: {e.Message}"; }
            if (ok) { passed++; Console.WriteLine($"  [PASS] {name}"); }
            else { failed++; Console.WriteLine($"  [FAIL] {name}{extra}"); }
        }

        Console.WriteLine("=== Rust 加密 -> C# 解密 反方向校验 ===");
        Console.WriteLine();

        Check("aes-256-cbc-hmac-sha256", () =>
            Encoding.UTF8.GetString(CipherRegistry.Default.Open(Require("RUST_AES_CBC"), rawKey)) == expected);

        Check("sm4-cbc-hmac-sha256", () =>
            Encoding.UTF8.GetString(CipherRegistry.Get(Sm4Cipher.CipherId).Open(Require("RUST_SM4_CBC"), rawKey)) == expected);

        Check("aes-256-gcm", () =>
            Encoding.UTF8.GetString(CipherRegistry.Get(AesGcmCipher.CipherId).Open(Require("RUST_AES_GCM"), rawKey)) == expected);

        Console.WriteLine();
        Console.WriteLine($"=== 通过 {passed} 项，失败 {failed} 项 ===");
        return failed == 0 ? 0 : 1;
    }
}
