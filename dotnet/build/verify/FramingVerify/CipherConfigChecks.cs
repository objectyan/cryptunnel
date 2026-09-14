using Cryptunnel.Core.Config;
using Cryptunnel.Core.Crypto;

namespace FramingVerify;

/// <summary>
/// cipher 配置项从 YAML 到 <c>TunnelConfig</c> 的贯通验收。
///
/// <para>要证明的核心命题只有一条：<b>算法配错必须在加载阶段就被拒</b>。
/// 按 ADR-0003，报文内不含算法标识，两端纯靠配置字符串对齐，没有协商余地。
/// 一个不被接受的算法名若放行到运行期，用户拿到的是服务端一句
/// <c>Auth decrypt failed</c> —— 而这条 reason 与 aesKey 配错完全无法区分，
/// 是全链路最难诊断的失败。所以这里验的不是「能不能读到值」，
/// 而是「读不懂的值会不会被挡在门外」。</para>
/// </summary>
internal static class CipherConfigChecks
{
    internal static void Run(Action<string, Func<bool>> check)
    {
        Console.WriteLine();
        Console.WriteLine("[9] cipher 配置项：省略与合法值");

        check("省略 cipher 时回落默认（老配置文件行为不变）", () =>
        {
            var (cfgs, errs) = Load(null);
            return cfgs.Count == 1 && errs.Count == 0
                && cfgs[0].Cipher == CipherRegistry.Default.Id;
        });

        foreach (var id in CipherRegistry.Ids.Where(i => CipherRegistry.Normalize(i) == i))
        {
            check($"显式 cipher: {id} 被接受并原样保留", () =>
            {
                var (cfgs, errs) = Load(id);
                return cfgs.Count == 1 && errs.Count == 0 && cfgs[0].Cipher == id;
            });
        }

        Console.WriteLine();
        Console.WriteLine("[10] 别名必须在解析期归一为规范标识");

        // sm4 是 Java 侧 TunnelCiphers 唯一声明了别名的算法。
        // 归一若漏做，界面展示 / YAML 回写 / 日志三处会出现同一算法的两种写法，
        // 用户核对两端配置时会误判为不一致。
        check("cipher: sm4 归一为 sm4-cbc-hmac-sha256", () =>
        {
            var (cfgs, errs) = Load("sm4");
            return cfgs.Count == 1 && errs.Count == 0
                && cfgs[0].Cipher == "sm4-cbc-hmac-sha256";
        });

        check("大小写与前后空格不影响识别", () =>
        {
            var (cfgs, errs) = Load("  AES-256-GCM  ");
            return cfgs.Count == 1 && errs.Count == 0 && cfgs[0].Cipher == "aes-256-gcm";
        });

        Console.WriteLine();
        Console.WriteLine("[11] 未知算法必须在加载阶段被拒（不得降级放行）");

        check("拼错的算法名被拒", () => IsRejected("aes-256-cbc"));
        check("服务端才有的写法被拒（客户端集合须是服务端子集）", () => IsRejected("chacha20-poly1305"));
        check("空字符串视为省略而非错误", () =>
        {
            var (cfgs, errs) = Load("\"\"");
            return cfgs.Count == 1 && errs.Count == 0
                && cfgs[0].Cipher == CipherRegistry.Default.Id;
        });

        check("被拒项目不出现在可用配置里", () =>
        {
            var (cfgs, errs) = Load("no-such-cipher");
            return cfgs.Count == 0 && errs.Count == 1;
        });

        check("错误信息列出可用算法并点明两端须一致", () =>
        {
            var (_, errs) = Load("no-such-cipher");
            if (errs.Count != 1) return false;
            var msg = errs[0].Message ?? string.Empty;
            return msg.Contains("no-such-cipher")
                && msg.Contains("aes-256-cbc-hmac-sha256")
                && msg.Contains("服务端");
        });

        Console.WriteLine();
        Console.WriteLine("[12] defaults 段与项目段的覆盖关系");

        check("defaults.cipher 对未声明的项目生效", () =>
        {
            var dir = NewDir();
            WriteDefaults(dir, "aes-256-gcm");
            WriteProject(dir, "demo", 5100, null);
            var (cfgs, errs) = ConfigLoader.Load(dir);
            return cfgs.Count == 1 && errs.Count == 0 && cfgs[0].Cipher == "aes-256-gcm";
        });

        check("项目段 cipher 覆盖 defaults", () =>
        {
            var dir = NewDir();
            WriteDefaults(dir, "aes-256-gcm");
            WriteProject(dir, "demo", 5101, "sm4");
            var (cfgs, errs) = ConfigLoader.Load(dir);
            return cfgs.Count == 1 && errs.Count == 0 && cfgs[0].Cipher == "sm4-cbc-hmac-sha256";
        });

        check("defaults 里的非法算法同样被拒（不能只校验项目段）", () =>
        {
            var dir = NewDir();
            WriteDefaults(dir, "bogus-cipher");
            WriteProject(dir, "demo", 5102, null);
            var (cfgs, errs) = ConfigLoader.Load(dir);
            return cfgs.Count == 0 && errs.Count >= 1;
        });

        check("一个项目算法配错不拖垮其它项目（故障隔离）", () =>
        {
            var dir = NewDir();
            WriteProject(dir, "good", 5103, "aes-256-gcm");
            WriteProject(dir, "bad", 5104, "nope");
            var (cfgs, errs) = ConfigLoader.Load(dir);
            return cfgs.Count == 1 && cfgs[0].Name == "good" && errs.Count == 1;
        });
    }

    private static bool IsRejected(string cipher)
    {
        var (cfgs, errs) = Load(cipher);
        return cfgs.Count == 0 && errs.Count == 1;
    }

    private static (List<Cryptunnel.Core.Models.TunnelConfig> Configs, List<ConfigError> Errors)
        Load(string? cipher)
    {
        var dir = NewDir();
        WriteProject(dir, "demo", 5099, cipher);
        return ConfigLoader.Load(dir);
    }

    /// <summary>每个用例独立目录，避免上一轮 yaml 残留影响结论。</summary>
    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cipher-cfg-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WriteDefaults(string dir, string cipher)
    {
        // 文件名前缀保证排在项目文件之前（ConfigLoader 按文件名序叠加 defaults）
        File.WriteAllText(Path.Combine(dir, "00-defaults.yaml"),
            "schemaVersion: 1\ndefaults:\n  cipher: " + cipher + "\n");
    }

    private static void WriteProject(string dir, string name, int port, string? cipher)
    {
        var cipherLine = cipher is null ? string.Empty : $"cipher: {cipher}\n";
        var yaml =
            $"name: {name}\n"
            + "serverUrl: http://127.0.0.1:8080\n"
            + "aesKey: verify-aes-key\n"
            + "authKey: verify-auth-key\n"
            + cipherLine
            + "local:\n"
            + $"  port: {port}\n";
        File.WriteAllText(Path.Combine(dir, $"{name}.yaml"), yaml);
    }
}
