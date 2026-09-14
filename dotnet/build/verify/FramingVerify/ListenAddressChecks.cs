using Cryptunnel.Core.Config;

namespace FramingVerify;

/// <summary>
/// 监听地址闸门：非回环地址必须由 <c>local.allowNonLoopback: true</c> 显式授权，
/// 否则配置在加载阶段就被拒绝。
///
/// <para><b>为什么这道闸门值得单独验</b>：非回环监听是本客户端唯一一个能让本机之外的人
/// 穿透防火墙的配置 —— 局域网内任意机器都能连上本地端口，而隧道会用本地配置的密钥
/// 替对方完成认证，<b>对方无需知道 aesKey 或 authKey</b>。这类缺陷不会有任何运行时症状，
/// 隧道照常工作，只是多了一个无人知晓的入口，靠人工 review 极易漏掉。</para>
/// </summary>
internal static class ListenAddressChecks
{
    internal static void Run(Action<string, Func<bool>> check)
    {
        Console.WriteLine();
        Console.WriteLine("[15] 监听地址闸门：默认只允许回环，非回环需显式授权");

        check("不写 address 时默认 127.0.0.1 且可加载", () =>
        {
            var dir = NewDir();
            WriteProject(dir, "demo", 5200, null, null);
            var (cfgs, errs) = ConfigLoader.Load(dir);
            return errs.Count == 0 && cfgs.Count == 1 && cfgs[0].ListenAddress == "127.0.0.1";
        });

        check("显式写 127.0.0.1 无需授权即可加载", () =>
        {
            var dir = NewDir();
            WriteProject(dir, "demo", 5201, "127.0.0.1", null);
            var (cfgs, errs) = ConfigLoader.Load(dir);
            return errs.Count == 0 && cfgs.Count == 1;
        });

        // 127.0.0.0/8 整段都是回环，只比对 "127.0.0.1" 字符串会把 127.0.0.5 误判成危险配置。
        check("127.0.0.0/8 段内其它地址同样免授权", () =>
        {
            var dir = NewDir();
            WriteProject(dir, "demo", 5202, "127.0.0.5", null);
            var (cfgs, errs) = ConfigLoader.Load(dir);
            return errs.Count == 0 && cfgs.Count == 1;
        });

        check("::1 视为回环，免授权", () =>
        {
            var dir = NewDir();
            WriteProject(dir, "demo", 5203, "::1", null);
            var (cfgs, errs) = ConfigLoader.Load(dir);
            return errs.Count == 0 && cfgs.Count == 1;
        });

        check("0.0.0.0 未授权时被拒绝加载", () =>
        {
            var dir = NewDir();
            WriteProject(dir, "demo", 5204, "0.0.0.0", null);
            var (cfgs, errs) = ConfigLoader.Load(dir);
            return cfgs.Count == 0 && errs.Count == 1;
        });

        check("0.0.0.0 显式授权后放行，且开关落到配置对象", () =>
        {
            var dir = NewDir();
            WriteProject(dir, "demo", 5205, "0.0.0.0", true);
            var (cfgs, errs) = ConfigLoader.Load(dir);
            return errs.Count == 0 && cfgs.Count == 1
                   && cfgs[0].ListenAddress == "0.0.0.0" && cfgs[0].AllowNonLoopback;
        });

        check("allowNonLoopback: false 与不写等价，仍被拒绝", () =>
        {
            var dir = NewDir();
            WriteProject(dir, "demo", 5206, "0.0.0.0", false);
            var (cfgs, errs) = ConfigLoader.Load(dir);
            return cfgs.Count == 0 && errs.Count == 1;
        });

        // 通配写法与 0.0.0.0 等价，漏一个就等于留一个后门。
        //
        // 这里必须比对错误信息而不只是「没加载成功」：`*` 在 YAML 里是别名指示符，
        // 若写法有误会先在解析阶段抛异常，同样得到 cfgs.Count == 0。
        // 只断言「没加载」会让这条用例在闸门根本没跑到的情况下照样变绿。
        check("通配写法 :: / * / + 一律被闸门拦下", () =>
        {
            var port = 5210;
            foreach (var addr in new[] { "::", "*", "+" })
            {
                var dir = NewDir();
                WriteProject(dir, "demo", port++, addr, null);
                var (cfgs, errs) = ConfigLoader.Load(dir);
                if (cfgs.Count != 0) return false;
                if (errs.Count != 1 || !errs[0].Message.Contains("allowNonLoopback")) return false;
            }
            return true;
        });

        check("具体网卡 IP 同样需要授权", () =>
        {
            var dir = NewDir();
            WriteProject(dir, "demo", 5220, "192.168.1.20", null);
            var (cfgs, errs) = ConfigLoader.Load(dir);
            return cfgs.Count == 0 && errs.Count == 1;
        });

        // 错误信息决定用户能否自助解决。只说「不允许」等于把人推去翻源码。
        check("拒绝信息含开关名与改回回环的建议", () =>
        {
            var dir = NewDir();
            WriteProject(dir, "demo", 5221, "0.0.0.0", null);
            var (_, errs) = ConfigLoader.Load(dir);
            var msg = errs.Count == 1 ? (errs[0].Message ?? "") : "";
            return msg.Contains("allowNonLoopback") && msg.Contains("127.0.0.1");
        });

        check("拒绝信息说明对方无需密钥（点明真实风险）", () =>
        {
            var dir = NewDir();
            WriteProject(dir, "demo", 5222, "0.0.0.0", null);
            var (_, errs) = ConfigLoader.Load(dir);
            return errs.Count == 1 && errs[0].Message.Contains("无需知道");
        });

        check("defaults 段的 allowNonLoopback 对项目生效", () =>
        {
            var dir = NewDir();
            File.WriteAllText(Path.Combine(dir, "00-defaults.yaml"),
                "schemaVersion: 1\ndefaults:\n  local:\n    allowNonLoopback: true\n");
            WriteProject(dir, "demo", 5223, "0.0.0.0", null);
            var (cfgs, errs) = ConfigLoader.Load(dir);
            return errs.Count == 0 && cfgs.Count == 1 && cfgs[0].AllowNonLoopback;
        });

        // 一个项目配错不该拖垮其它项目 —— 与 cipher 校验保持同样的故障隔离语义。
        check("被拒项目不影响同目录其它项目加载", () =>
        {
            var dir = NewDir();
            WriteProject(dir, "good", 5230, "127.0.0.1", null);
            WriteProject(dir, "bad", 5231, "0.0.0.0", null);
            var (cfgs, errs) = ConfigLoader.Load(dir);
            return cfgs.Count == 1 && cfgs[0].Name == "good" && errs.Count == 1;
        });
    }

    /// <summary>每个用例独立目录，避免上一轮 yaml 残留影响结论。</summary>
    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "addr-cfg-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WriteProject(string dir, string name, int port, string? address, bool? allow)
    {
        var addrLine = address is null ? string.Empty : $"  address: \"{address}\"\n";
        var allowLine = allow is null ? string.Empty : $"  allowNonLoopback: {(allow.Value ? "true" : "false")}\n";
        var yaml =
            $"name: {name}\n"
            + "serverUrl: http://127.0.0.1:8080\n"
            + "aesKey: verify-aes-key\n"
            + "authKey: verify-auth-key\n"
            + "local:\n"
            + $"  port: {port}\n"
            + addrLine
            + allowLine;
        File.WriteAllText(Path.Combine(dir, $"{name}.yaml"), yaml);
    }
}
