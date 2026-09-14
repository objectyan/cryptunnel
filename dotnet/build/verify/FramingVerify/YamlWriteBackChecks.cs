using System.Reflection;
using Cryptunnel.Core.Crypto;
using Cryptunnel.Core.Models;

namespace FramingVerify;

/// <summary>
/// YAML 写回的兼容性验收（反射调用 <c>AppController.BuildProjectYaml</c>）。
///
/// <para>要守的不变量：<b>选默认算法时，产出的 YAML 里不得出现 cipher 行</b>。</para>
///
/// <para>为什么这条值得单独验：用户只是改个端口号并保存，如果无条件写回 cipher，
/// 文件就会凭空多出一行。对于把 config.d 纳入版本管理、或习惯 diff 配置的用户，
/// 这是升级后一次性的全量噪声 —— 而且是那种「看起来像是程序动了我不懂的东西」
/// 的噪声，最容易引发不信任。省略与显式写默认值在语义上完全等价
/// （ConfigLoader 的缺省链回落到同一算法），所以省略是免费的。</para>
///
/// <para>反射是必要的恶：<c>BuildProjectYaml</c> 是 App 层的 private static，
/// 为了测试把它改成 public 就是为测试便利放宽生产代码封装。宁可在验收侧多写四行反射。</para>
/// </summary>
internal static class YamlWriteBackChecks
{
    internal static void Run(Action<string, Func<bool>> check, Action<string> skip)
    {
        Console.WriteLine();
        Console.WriteLine("[14] YAML 写回：默认算法不落盘，非默认算法必须落盘");

        var build = ResolveBuilder(out var why);
        if (build is null)
        {
            skip($"未能加载 AppController.BuildProjectYaml（{why}）—— 写回兼容性未验证");
            return;
        }

        check("选默认算法时，产出中不含 cipher 行（老文件零变化）", () =>
        {
            var yaml = build(Sample(CipherRegistry.Default.Id));
            return !yaml.Contains("cipher");
        });

        check("Cipher 为 null 时同样不写（等价于默认）", () =>
        {
            var yaml = build(Sample(null));
            return !yaml.Contains("cipher");
        });

        foreach (var id in CipherRegistry.Ids
                     .Where(i => CipherRegistry.Normalize(i) == i && i != CipherRegistry.Default.Id))
        {
            check($"选 {id} 时写出 cipher 行", () =>
            {
                var yaml = build(Sample(id));
                return yaml.Contains($"cipher: {id}");
            });
        }

        check("别名写回时归一为规范标识（避免两端核对时误判不一致）", () =>
        {
            var yaml = build(Sample("sm4"));
            return yaml.Contains("cipher: sm4-cbc-hmac-sha256") && !yaml.Contains("cipher: sm4\n");
        });

        check("写回结果能被 ConfigLoader 原样读回（往返一致）", () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "yaml-rt-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "demo.yaml"), build(Sample("aes-256-gcm")));
            var (cfgs, errs) = Cryptunnel.Core.Config.ConfigLoader.Load(dir);
            return cfgs.Count == 1 && errs.Count == 0 && cfgs[0].Cipher == "aes-256-gcm";
        });
    }

    private static ProjectFile Sample(string? cipher) => new()
    {
        SchemaVersion = 1,
        Name = "demo",
        Enabled = true,
        ServerUrl = "http://127.0.0.1:8080",
        AesKey = "verify-aes-key",
        AuthKey = "verify-auth-key",
        Local = new LocalSection { Port = 5200 },
        Cipher = cipher,
    };

    /// <summary>
    /// 从已编译的 App 程序集里取出 BuildProjectYaml。
    /// 找不到时返回 null 并说明原因 —— 静默通过等于把「没验」记成「验过了」。
    /// </summary>
    private static Func<ProjectFile, string>? ResolveBuilder(out string why)
    {
        try
        {
            var dll = LocateAppDll();
            if (dll is null) { why = "未找到含 AppController 的 App 程序集，请先构建 App 工程"; return null; }

            var type = Assembly.LoadFrom(dll).GetType("Cryptunnel.App.AppController");
            if (type is null) { why = "程序集中没有 AppController 类型"; return null; }

            var mi = type.GetMethod("BuildProjectYaml",
                BindingFlags.NonPublic | BindingFlags.Static);
            if (mi is null) { why = "未找到 private static BuildProjectYaml 方法"; return null; }

            why = string.Empty;
            return pf => (string)mi.Invoke(null, new object?[] { pf })!;
        }
        catch (Exception e)
        {
            why = $"{e.GetType().Name}: {e.Message}";
            return null;
        }
    }

    private static string? LocateAppDll()
    {
        // 从验收程序自身位置往上找到仓库根，再定位 App 的构建产物。
        //
        // 按类型名而不是文件名定位：App 工程的 AssemblyName 是 Cryptunnel（不是
        // Cryptunnel.App），文件名与工程名不一致。写死文件名会在别人改 AssemblyName
        // 时静默变成 SKIP —— 而 SKIP 是不会让 CI 变红的，缺陷就这么溜过去了。
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            var binDir = Path.Combine(dir, "src", "Cryptunnel.App", "bin");
            if (Directory.Exists(binDir))
            {
                var hit = Directory.GetFiles(binDir, "*.dll", SearchOption.AllDirectories)
                    .Where(f => Path.GetFileName(f).StartsWith("Cryptunnel", StringComparison.OrdinalIgnoreCase))
                    .Where(HasAppController)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();
                if (hit is not null) return hit;
            }
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }
        return null;
    }

    private static bool HasAppController(string dll)
    {
        try
        {
            return Assembly.LoadFrom(dll).GetType("Cryptunnel.App.AppController") is not null;
        }
        catch
        {
            return false;   // 非托管/无关 dll，跳过
        }
    }
}
