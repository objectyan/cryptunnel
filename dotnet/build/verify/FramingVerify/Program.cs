using Cryptunnel.Core.Config;

namespace FramingVerify;

/// <summary>
/// 帧上限守卫的验收程序。全部断言通过退出码 0，任一失败退出码 1。
///
/// <para><b>刻意只走公开 API</b>：<c>TunnelFraming</c> 是 internal，
/// 本来可以加 <c>InternalsVisibleTo</c> 拿到它直接单测，但那样是为了测试而放宽生产代码的封装。
/// 这里改为从 <see cref="ConfigLoader.Load"/> 这个真实入口喂 YAML，
/// 验的是「用户把 chunkSize 写大了会发生什么」——
/// 比直接调内部函数更接近真实场景，也不用动可见性。</para>
/// </summary>
internal static class Program
{
    private static int _passed;
    private static int _failed;
    private static int _skipped;

    private static int Main()
    {
        try { Console.OutputEncoding = Encoding.UTF8; }
        catch (IOException) { /* 无控制台宿主时忽略 */ }

        Console.WriteLine("=== 隧道层移植验收：帧上限 / 认证报文 / close reason 映射 / 健康检查 ===");
        Console.WriteLine();

        var dir = Path.Combine(Path.GetTempPath(), "cryptunnel-framing-verify-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            RunChecks(dir);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* 临时目录，清理失败不影响结论 */ }
        }

        Console.WriteLine();
        var skipNote = _skipped > 0 ? $"，跳过 {_skipped} 项" : string.Empty;
        Console.WriteLine($"=== 通过 {_passed} 项，失败 {_failed} 项{skipNote} ===");
        return _failed == 0 ? 0 : 1;
    }

    private static void RunChecks(string dir)
    {
        Console.WriteLine("[1] 合法 chunkSize 必须被接受");

        Check("默认值 4096 通过", () =>
        {
            var (cfgs, errs) = LoadWith(dir, 4096);
            return cfgs.Count == 1 && errs.Count == 0 && cfgs[0].ChunkSize == 4096;
        });

        Check("省略 chunkSize 时回落 4096", () =>
        {
            var (cfgs, errs) = LoadWith(dir, null);
            return cfgs.Count == 1 && errs.Count == 0 && cfgs[0].ChunkSize == 4096;
        });

        Check("安全上界 6000 通过（TunnelFraming.MaxPlaintextChunkBytes）", () =>
        {
            var (cfgs, errs) = LoadWith(dir, 6000);
            return cfgs.Count == 1 && errs.Count == 0 && cfgs[0].ChunkSize == 6000;
        });

        Console.WriteLine();
        Console.WriteLine("[2] 越界 chunkSize 必须在加载阶段被拒（不得进入运行期）");

        Check("6001 被拒（刚过安全上界）", () => IsRejected(dir, 6001));
        Check("8192 被拒", () => IsRejected(dir, 8192));
        Check("65536 被拒", () => IsRejected(dir, 65536));
        Check("0 被拒", () => IsRejected(dir, 0));
        Check("-1 被拒", () => IsRejected(dir, -1));

        Console.WriteLine();
        Console.WriteLine("[3] 拒绝时必须给出可操作的中文诊断");

        Check("错误信息含 chunkSize 与 1009 线索", () =>
        {
            var (_, errs) = LoadWith(dir, 65536);
            if (errs.Count != 1) return false;
            var msg = errs[0].Message ?? string.Empty;
            return msg.Contains("chunkSize") && msg.Contains("1009") && msg.Contains("4096");
        });

        Check("被拒项目不会出现在可用配置里（不是降级放行）", () =>
        {
            var (cfgs, errs) = LoadWith(dir, 65536);
            return cfgs.Count == 0 && errs.Count == 1;
        });

        Console.WriteLine();
        Console.WriteLine("[4] 一个项目配错不得拖垮其它项目（故障隔离）");

        Check("坏项目被剔除，好项目照常加载", () =>
        {
            var isolated = Path.Combine(dir, "isolated");
            Directory.CreateDirectory(isolated);
            WriteProject(isolated, "good", 5001, 4096);
            WriteProject(isolated, "bad", 5002, 99999);

            var (cfgs, errs) = ConfigLoader.Load(isolated);
            return cfgs.Count == 1 && cfgs[0].Name == "good" && errs.Count == 1;
        });

        // 认证报文构造器的验收。与帧上限守卫是同一批移植产物，放同一个程序里跑，
        // 保证「移植是否等价」只有一个退出码要看。
        AuthChecks.Run(Check);

        // cipher 配置项从 YAML 贯通到 TunnelConfig 的验收。
        // 与 chunkSize 同属「配错必须在加载阶段被拒」这一类不变量。
        CipherConfigChecks.Run(Check);

        // httpBasePath 的验收。它是唯一一处「两端都曾硬编码」的线级契约，
        // 改成配置项后必须证明：默认值可用、旧服务端路径可配、写法错了会被纠正。
        HttpBasePathChecks.Run(Check);

        // close reason 映射的验收。会尝试对着服务端 Java 源码比对覆盖完整性。
        CloseReasonChecks.Run(Check, Skip);

        // YAML 写回的兼容性验收（反射调 App 层的 BuildProjectYaml）。
        YamlWriteBackChecks.Run(Check, Skip);

        // 监听地址闸门的验收。与 cipher 同属「配错必须在加载阶段被拒」，
        // 但后果更重：配错的结果不是连不上，而是多一个无需密钥即可利用的入口。
        ListenAddressChecks.Run(Check);

        // 健康检查（探活）的验收：MySQL 首帧解析 + health 配置校验 + 报告三态。
        HealthChecks.Run(Check);
    }

    /// <summary>
    /// 跳过一项校验。<b>不计入通过数</b> —— 把「没验」记成「验过了」是最坏的失效方式，
    /// 所以单独计数并在汇总行里显式列出。
    /// </summary>
    private static void Skip(string reason)
    {
        _skipped++;
        Console.WriteLine($"  [SKIP] {reason}");
    }

    private static bool IsRejected(string dir, int chunkSize)
    {
        var (cfgs, errs) = LoadWith(dir, chunkSize);
        return cfgs.Count == 0 && errs.Count == 1;
    }

    /// <summary>每次用独立子目录，避免上一轮的 yaml 残留影响结论。</summary>
    private static (List<Cryptunnel.Core.Models.TunnelConfig> Configs, List<ConfigError> Errors)
        LoadWith(string root, int? chunkSize)
    {
        var dir = Path.Combine(root, "case-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        WriteProject(dir, "demo", 5000, chunkSize);
        return ConfigLoader.Load(dir);
    }

    private static void WriteProject(string dir, string name, int port, int? chunkSize)
    {
        var chunkLine = chunkSize.HasValue ? $"chunkSize: {chunkSize.Value}\n" : string.Empty;
        var yaml =
            $"name: {name}\n"
            + "serverUrl: http://127.0.0.1:8080\n"
            + "aesKey: verify-aes-key\n"
            + "authKey: verify-auth-key\n"
            + "local:\n"
            + $"  port: {port}\n"
            + chunkLine;
        File.WriteAllText(Path.Combine(dir, $"{name}.yaml"), yaml);
    }

    private static void Check(string name, Func<bool> assertion)
    {
        bool ok;
        var extra = string.Empty;
        try
        {
            ok = assertion();
        }
        catch (Exception e)
        {
            ok = false;
            extra = $" -> {e.GetType().Name}: {e.Message}";
        }

        if (ok)
        {
            _passed++;
            Console.WriteLine($"  [PASS] {name}");
        }
        else
        {
            _failed++;
            Console.WriteLine($"  [FAIL] {name}{extra}");
        }
    }
}
