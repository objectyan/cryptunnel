using System.Reflection;
using System.Text.RegularExpressions;
using Cryptunnel.Core.Crypto;

namespace FramingVerify;

/// <summary>
/// 认证报文构造器的格式一致性校验。
///
/// <para><b>为什么用反射而不是加 InternalsVisibleTo</b>：
/// <c>AuthMessageBuilder</c> 是 internal，为了验它而在生产工程上开
/// <c>InternalsVisibleTo</c> 是用放宽封装换测试便利，不划算。
/// 这里是一次性的移植验收，用反射读到即可，不留任何生产侧痕迹。</para>
///
/// <para><b>为什么现在就要验</b>：Task #41 会用 <c>AuthMessageBuilder</c> 替换
/// <c>Tunnel.GenerateEncryptedAuth</c>。两者报文格式若有任何偏差，
/// 表现是服务端 <c>Auth format error</c> 或 <c>Auth key invalid</c>，
/// 而那时故障已经在隧道里了，排查成本远高于现在。</para>
/// </summary>
internal static class AuthChecks
{
    private static readonly Regex FourSeg =
        new(@"^AUTH:[^:]+:\d{10,}:[0-9a-f]{32}$", RegexOptions.Compiled);

    private static readonly Regex FiveSeg =
        new(@"^AUTH:[^:]+:\d{10,}:[0-9a-f]{32}:[^:]+$", RegexOptions.Compiled);

    internal static void Run(Action<string, Func<bool>> check)
    {
        var type = typeof(CipherRegistry).Assembly
            .GetType("Cryptunnel.Core.Tunnel.AuthMessageBuilder", throwOnError: true)!;

        var build = type.GetMethod("Build", BindingFlags.NonPublic | BindingFlags.Static)!;
        var nonce = type.GetMethod("GenerateNonce", BindingFlags.NonPublic | BindingFlags.Static)!;
        var sealed_ = type.GetMethod("BuildSealed", BindingFlags.NonPublic | BindingFlags.Static)!;
        var mask = type.GetMethod("MaskKey", BindingFlags.NonPublic | BindingFlags.Static)!;

        string Build(string authKey, string? targetId = null) =>
            (string)build.Invoke(null, new object?[] { authKey, targetId })!;

        Console.WriteLine();
        Console.WriteLine("[5] 认证报文格式（须与服务端 handleAuth 解析逻辑对齐）");

        check("4 段格式 AUTH:key:ts:nonce", () => FourSeg.IsMatch(Build("k1")));

        check("5 段格式带 targetId", () => FiveSeg.IsMatch(Build("k1", "crm-main")));

        check("targetId 为空白时退回 4 段", () => FourSeg.IsMatch(Build("k1", "   ")));

        check("时间戳是秒级而非毫秒级", () =>
        {
            var ts = long.Parse(Build("k1").Split(':')[2]);
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            // 毫秒级会比秒级大三个数量级，这里允许 5 秒抖动
            return Math.Abs(now - ts) <= 5;
        });

        check("nonce 是 32 位小写十六进制", () =>
        {
            var n = (string)nonce.Invoke(null, null)!;
            return n.Length == 32 && n.All(c => char.IsDigit(c) || (c >= 'a' && c <= 'f'));
        });

        check("nonce 每次不同（防重放的前提）", () =>
        {
            var set = new HashSet<string>();
            for (var i = 0; i < 200; i++)
                set.Add((string)nonce.Invoke(null, null)!);
            return set.Count == 200;
        });

        Console.WriteLine();
        Console.WriteLine("[6] 非法输入必须硬失败（不得静默产出坏报文）");

        check("authKey 为空被拒", () => ThrowsArg(() => Build("")));

        check("authKey 含冒号被拒（否则服务端段数错乱）", () => ThrowsArg(() => Build("bad:key")));

        check("targetId 含冒号被拒", () => ThrowsArg(() => Build("k1", "bad:target")));

        Console.WriteLine();
        Console.WriteLine("[7] BuildSealed 产出必须可被同算法解回原文");

        foreach (var id in new[] { "aes-256-cbc-hmac-sha256", "aes-256-gcm", "sm4-cbc-hmac-sha256" })
        {
            var cipher = CipherRegistry.Get(id);
            check($"{id}: 密文可解回合法 4 段报文", () =>
            {
                var payload = (string)sealed_.Invoke(null, new object?[] { cipher, "k1", "raw-key", null })!;
                var plain = Encoding.UTF8.GetString(cipher.Open(payload, "raw-key"));
                return FourSeg.IsMatch(plain);
            });

            check($"{id}: 载荷字节数精确等于密码学字段之和（放不下算法头）", () =>
            {
                var payload = (string)sealed_.Invoke(null, new object?[] { cipher, "k1", "raw-key", null })!;
                var raw = Convert.FromBase64String(payload);
                var plainLen = cipher.Open(payload, "raw-key").Length;

                // 这是「报文内零算法标识」的确定性证法。
                // 扫描字节里有没有 "aes"/"sm4" 之类的子串是概率性的（随机 IV 有极小概率凑出来，
                // 会变成偶发红灯），而长度是恒等的：只要实际字节数等于
                // 「IV/nonce + 认证标签 + 密文」的精确值，就说明中间没有任何一个字节留给算法标识。
                var expected = id == "aes-256-gcm"
                    ? 12 + plainLen + 16                        // nonce ‖ 密文 ‖ tag
                    : 16 + 32 + ((plainLen / 16) + 1) * 16;     // IV ‖ HMAC ‖ PKCS7 密文
                return raw.Length == expected;
            });

            check($"{id}: Base64 原文中不出现算法名（人眼与 DPI 都看不到）", () =>
            {
                var payload = (string)sealed_.Invoke(null, new object?[] { cipher, "k1", "raw-key", null })!;
                return !payload.Contains(id, StringComparison.OrdinalIgnoreCase);
            });
        }

        check("aesKey 为空被拒", () =>
            ThrowsArg(() => sealed_.Invoke(null, new object?[] { CipherRegistry.Default, "k1", "", null })));

        Console.WriteLine();
        Console.WriteLine("[8] 密钥掩码不得泄漏完整密钥");

        check("长密钥只留前 6 字符", () =>
            (string)mask.Invoke(null, new object?[] { "abcdefghijklmn" })! == "abcdef...");

        check("短密钥全部打码", () =>
            (string)mask.Invoke(null, new object?[] { "abc" })! == "***");

        check("空密钥给出明确提示", () =>
            (string)mask.Invoke(null, new object?[] { null })! == "(未配置)");
    }

    /// <summary>反射调用会把异常包在 TargetInvocationException 里，需要拆开看内层类型。</summary>
    private static bool ThrowsArg(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (TargetInvocationException e)
        {
            return e.InnerException is ArgumentException;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }
}
