using Cryptunnel.Core.Config;

namespace FramingVerify;

/// <summary>
/// HTTP 降级通道基础路径（<c>httpBasePath</c>）的配置贯通验收。
///
/// <para>为什么需要这一批断言：改名 cryptunnel 之前，三个 HTTP 端点是
/// <c>"/jdbc-proxy/connect"</c> 这样硬编码在 <c>Tunnel.cs</c> 里的，
/// 而服务端那侧是硬编码在 <c>@RequestMapping</c> 注解里。两边都写死意味着
/// 「换个品牌名」变成了「两端同时改代码并同时发版」，中间任何一端先上线，
/// HTTP 降级通道就静默 404 —— 而 WS 主通道仍然是通的，
/// 用户只会在防火墙拦掉 WS 之后才发现降级根本用不了。</para>
///
/// <para>所以把它做成配置项。但配置项本身引入了新的失效面：路径写法。
/// 用户会写 <c>cryptunnel</c>（漏开头斜杠）、<c>/cryptunnel/</c>（多结尾斜杠）
/// 甚至留空。这些都会拼出错误 URL 且报错完全看不出根因，
/// 所以归一化逻辑必须有断言守着。</para>
/// </summary>
internal static class HttpBasePathChecks
{
    internal static void Run(Action<string, Func<bool>> check)
    {
        Console.WriteLine();
        Console.WriteLine("[12] httpBasePath：默认值与显式配置");

        check("省略 httpBasePath 时回落 /cryptunnel（新装用户开箱即用）", () =>
        {
            var cfg = LoadOne(null);
            return cfg != null && cfg.HttpBasePath == "/cryptunnel";
        });

        check("显式 /cryptunnel 原样保留", () =>
        {
            var cfg = LoadOne("/cryptunnel");
            return cfg != null && cfg.HttpBasePath == "/cryptunnel";
        });

        // 这一条是「能对接未改名的旧服务端」的能力证明 ——
        // 也正是把硬编码改成配置项的唯一实际收益。
        check("可配成旧服务端的 /jdbc-proxy（无需改代码即可对接旧服务端）", () =>
        {
            var cfg = LoadOne("/jdbc-proxy");
            return cfg != null && cfg.HttpBasePath == "/jdbc-proxy";
        });

        check("可配成任意自定义前缀（不写死品牌名）", () =>
        {
            var cfg = LoadOne("/my-tunnel");
            return cfg != null && cfg.HttpBasePath == "/my-tunnel";
        });

        Console.WriteLine();
        Console.WriteLine("[13] httpBasePath 归一化：错误写法必须被纠正而不是拼出坏 URL");

        // 漏开头斜杠：不纠正会拼成 http://host:8081cryptunnel/connect
        check("漏开头斜杠 cryptunnel -> /cryptunnel", () =>
        {
            var cfg = LoadOne("cryptunnel");
            return cfg != null && cfg.HttpBasePath == "/cryptunnel";
        });

        // 多结尾斜杠：不纠正会拼成 /cryptunnel//connect，
        // 被服务端当成另一个路径 -> 404，且报错看不出是配置写法问题
        check("多结尾斜杠 /cryptunnel/ -> /cryptunnel", () =>
        {
            var cfg = LoadOne("/cryptunnel/");
            return cfg != null && cfg.HttpBasePath == "/cryptunnel";
        });

        check("多个结尾斜杠 /cryptunnel/// -> /cryptunnel", () =>
        {
            var cfg = LoadOne("/cryptunnel///");
            return cfg != null && cfg.HttpBasePath == "/cryptunnel";
        });

        check("两头都错 cryptunnel/ -> /cryptunnel", () =>
        {
            var cfg = LoadOne("cryptunnel/");
            return cfg != null && cfg.HttpBasePath == "/cryptunnel";
        });

        check("前后空格被吃掉", () =>
        {
            var cfg = LoadOne("  /cryptunnel  ");
            return cfg != null && cfg.HttpBasePath == "/cryptunnel";
        });

        check("空字符串回落默认值（而不是产出空前缀）", () =>
        {
            var cfg = LoadOne("\"\"");
            return cfg != null && cfg.HttpBasePath == "/cryptunnel";
        });

        // 单个 '/' 是合法但特殊的输入：端点直接挂在根上。
        // 归一化不能把它削成空串，否则拼出的是 "connect" 而非 "/connect"。
        check("单个 / 保持为 /（不被削成空串）", () =>
        {
            var cfg = LoadOne("/");
            return cfg != null && cfg.HttpBasePath == "/";
        });

        check("多层路径 /api/v1/cryptunnel 原样保留（反向代理场景）", () =>
        {
            var cfg = LoadOne("/api/v1/cryptunnel");
            return cfg != null && cfg.HttpBasePath == "/api/v1/cryptunnel";
        });

        Console.WriteLine();
        Console.WriteLine("[14] httpBasePath 的 defaults 继承与项目级覆盖");

        check("defaults 段的 httpBasePath 能被项目继承", () =>
        {
            var dir = NewDir();
            WriteDefaults(dir, "  httpBasePath: /from-defaults\n");
            WriteProject(dir, "demo", 5099, null);
            var (cfgs, errs) = ConfigLoader.Load(dir);
            return cfgs.Count == 1 && errs.Count == 0
                && cfgs[0].HttpBasePath == "/from-defaults";
        });

        check("项目级 httpBasePath 覆盖 defaults", () =>
        {
            var dir = NewDir();
            WriteDefaults(dir, "  httpBasePath: /from-defaults\n");
            WriteProject(dir, "demo", 5099, "/from-project");
            var (cfgs, errs) = ConfigLoader.Load(dir);
            return cfgs.Count == 1 && errs.Count == 0
                && cfgs[0].HttpBasePath == "/from-project";
        });

        check("完全不含 httpBasePath 键的老配置照常加载", () =>
        {
            var dir = NewDir();
            WriteProject(dir, "demo", 5099, null);
            var (cfgs, errs) = ConfigLoader.Load(dir);
            return cfgs.Count == 1 && errs.Count == 0
                && cfgs[0].HttpBasePath == "/cryptunnel";
        });

        // 三个端点是 basePath + "/connect" 拼出来的，
        // 断言拼接结果而不只是断言字段值 —— 字段对但拼错照样 404。
        check("拼接结果不含双斜杠且不粘连（三端点逐一验）", () =>
        {
            foreach (var raw in new[] { "cryptunnel", "/cryptunnel/", "/cryptunnel", "  /cryptunnel//  " })
            {
                var cfg = LoadOne(raw);
                if (cfg == null) return false;
                foreach (var ep in new[] { "/connect", "/tunnel", "/disconnect" })
                {
                    var url = "http://127.0.0.1:8080" + cfg.HttpBasePath + ep;
                    if (url != "http://127.0.0.1:8080/cryptunnel" + ep) return false;
                    // 只检查 scheme 之后的部分，"http://" 自带双斜杠
                    if (url["http://".Length..].Contains("//")) return false;
                }
            }
            return true;
        });
    }

    private static Cryptunnel.Core.Models.TunnelConfig? LoadOne(string? basePath)
    {
        var dir = NewDir();
        WriteProject(dir, "demo", 5099, basePath);
        var (cfgs, errs) = ConfigLoader.Load(dir);
        return errs.Count == 0 && cfgs.Count == 1 ? cfgs[0] : null;
    }

    /// <summary>每个用例独立目录，避免上一轮 yaml 残留影响结论。</summary>
    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "httpbase-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WriteDefaults(string dir, string body)
    {
        // 文件名前缀保证排在项目文件之前（ConfigLoader 按文件名序叠加 defaults）
        File.WriteAllText(Path.Combine(dir, "00-defaults.yaml"),
            "schemaVersion: 1\ndefaults:\n" + body);
    }

    private static void WriteProject(string dir, string name, int port, string? basePath)
    {
        var line = basePath is null ? string.Empty : $"httpBasePath: {basePath}\n";
        var yaml =
            $"name: {name}\n"
            + "serverUrl: http://127.0.0.1:8080\n"
            + "aesKey: verify-aes-key\n"
            + "authKey: verify-auth-key\n"
            + line
            + "local:\n"
            + $"  port: {port}\n";
        File.WriteAllText(Path.Combine(dir, $"{name}.yaml"), yaml);
    }
}
