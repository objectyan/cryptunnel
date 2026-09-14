using Cryptunnel.Core.Config;
using Cryptunnel.Core.Health;

namespace FramingVerify;

/// <summary>
/// 健康检查（探活）的验收断言。
///
/// <para><b>为什么必须实跑</b>：<see cref="MySqlHandshake.Parse"/> 的输入来自网络，
/// 是彻头彻尾的不可信字节。它有两条互相矛盾的要求 ——
/// 既要在正常包上解出准确的版本号/错误码，又要在任何畸形输入上
/// 降级为 Unknown 而不是抛异常（探活是诊断工具，它自己崩掉就毫无意义）。
/// 光看代码无法证明这两条同时成立。</para>
///
/// <para><b>SQLState 那条是重点</b>：ERR 包里的 <c>'#'+5</c> 段只在客户端声明了
/// CLIENT_PROTOCOL_41 时才出现，而握手阶段通常没有。无条件跳 6 字节的实现
/// 在真实环境下会把错误文本的前 6 个字符吃掉，且不会报任何错 ——
/// 这种缺陷只有靠「同一段文本、有/无 SQLState 两个用例」才能钉住。</para>
/// </summary>
internal static class HealthChecks
{
    public static void Run(Action<string, Func<bool>> check)
    {
        Console.WriteLine();
        Console.WriteLine("[7] MySQL 首帧解析：正常握手包");
        // SECTION_HANDSHAKE

        check("典型握手包 → Handshake，版本号 8.0.36", () =>
        {
            var f = MySqlHandshake.Parse(Frame(HandshakePayload("8.0.36")));
            return f.Kind == MySqlFrameKind.Handshake && f.ServerVersion == "8.0.36";
        });

        check("MariaDB 风格版本串完整保留（不被截到第一个 '-'）", () =>
        {
            var f = MySqlHandshake.Parse(Frame(HandshakePayload("5.5.5-10.6.12-MariaDB")));
            return f.Kind == MySqlFrameKind.Handshake && f.ServerVersion == "5.5.5-10.6.12-MariaDB";
        });

        check("握手包不得被误判为错误包（ErrorCode 必须为 0）", () =>
        {
            var f = MySqlHandshake.Parse(Frame(HandshakePayload("8.0.36")));
            return f.ErrorCode == 0 && f.ErrorMessage == null;
        });

        check("Detail 含版本号，可直接展示给用户", () =>
        {
            var f = MySqlHandshake.Parse(Frame(HandshakePayload("8.4.0")));
            return f.Detail.Contains("8.4.0");
        });

        check("版本串为空（NUL 紧跟协议版本）→ 仍判为 Handshake，服务端到 MySQL 是通的", () =>
        {
            // 这条不能判成 Unknown：收到包这一事实本身就证明了服务端连上了 MySQL，
            // 版本号解不出来只是少一条信息，不影响「数据库可达」这个结论。
            var f = MySqlHandshake.Parse(Frame(0x0A, 0x00, 0x01, 0x02));
            return f.Kind == MySqlFrameKind.Handshake && f.ServerVersion == "(未知)";
        });


        Console.WriteLine();
        Console.WriteLine("[8] MySQL 首帧解析：ERR 包（必须与「连不上」区分开）");
        // SECTION_ERR

        check("1130 Host not allowed（无 SQLState）→ 错误码与文本都对", () =>
        {
            var f = MySqlHandshake.Parse(Frame(ErrPayload(
                1130, "Host '10.6.10.22' is not allowed to connect to this MySQL server", null)));
            return f.Kind == MySqlFrameKind.ErrorPacket
                && f.ErrorCode == 1130
                && f.ErrorMessage == "Host '10.6.10.22' is not allowed to connect to this MySQL server";
        });

        // 这是本组最关键的一条：SQLState 段只在客户端声明 CLIENT_PROTOCOL_41 时出现，
        // 握手阶段通常没有。若实现无条件跳 6 字节，这里的文本会被吃掉前 6 个字符，
        // 变成 "10.6.10.22' is not allowed..." —— 用户看到的错误信息会缺头。
        check("无 SQLState 时不得吞掉文本前 6 字符", () =>
        {
            var f = MySqlHandshake.Parse(Frame(ErrPayload(1130, "Host 'abc' rejected", null)));
            return f.ErrorMessage == "Host 'abc' rejected"
                && !f.ErrorMessage!.StartsWith("bc'", StringComparison.Ordinal);
        });

        check("含 SQLState（#42000）时必须跳过这 6 字节", () =>
        {
            var f = MySqlHandshake.Parse(Frame(ErrPayload(1045, "Access denied for user", "42000")));
            return f.ErrorCode == 1045
                && f.ErrorMessage == "Access denied for user"
                && !f.ErrorMessage!.Contains('#');
        });

        check("1040 Too many connections → 错误码 1040", () =>
        {
            var f = MySqlHandshake.Parse(Frame(ErrPayload(1040, "Too many connections", null)));
            return f.Kind == MySqlFrameKind.ErrorPacket && f.ErrorCode == 1040;
        });

        // 错误码是 2 字节小端。1040 = 0x0410：低字节 0x10、高字节 0x04。
        // 若实现写反成大端，读出来会是 0x1004 = 4100 —— 一个不存在的错误码，
        // 而用户拿着它去搜索什么也搜不到。
        check("错误码按小端解析（1040 不得读成 4100）", () =>
        {
            var f = MySqlHandshake.Parse(Frame(0xFF, 0x10, 0x04, (byte)'x'));
            return f.ErrorCode == 1040;
        });

        check("ERR 包的 ServerVersion 必须为 null（不能混入握手包的字段）", () =>
        {
            var f = MySqlHandshake.Parse(Frame(ErrPayload(1045, "denied", null)));
            return f.ServerVersion == null;
        });

        check("Detail 明确指出「服务端已连上 MySQL 但被拒绝」", () =>
        {
            var f = MySqlHandshake.Parse(Frame(ErrPayload(1130, "nope", null)));
            // 这句措辞是有意义的：它把排查方向从「网络不通」扭到「MySQL 授权表」。
            return f.Detail.Contains("已连上") && f.Detail.Contains("1130");
        });

        check("无文本的 ERR 包不得崩，给出占位文本", () =>
        {
            var f = MySqlHandshake.Parse(Frame(0xFF, 0x10, 0x04));
            return f.Kind == MySqlFrameKind.ErrorPacket && f.ErrorCode == 1040
                && f.ErrorMessage == "(无错误文本)";
        });


        Console.WriteLine();
        Console.WriteLine("[9] MySQL 首帧解析：畸形输入必须降级为 Unknown 而不是抛异常");
        // SECTION_MALFORMED

        check("空帧 → Unknown", () =>
            MySqlHandshake.Parse(ReadOnlySpan<byte>.Empty).Kind == MySqlFrameKind.Unknown);

        check("只有 4 字节包头（无负载）→ Unknown", () =>
            MySqlHandshake.Parse(new byte[] { 1, 0, 0, 0 }).Kind == MySqlFrameKind.Unknown);

        check("1 字节 → Unknown", () =>
            MySqlHandshake.Parse(new byte[] { 0x0A }).Kind == MySqlFrameKind.Unknown);

        check("声明 100 字节负载但只给 10 字节 → Unknown 且 Detail 说明被截断", () =>
        {
            var f = MySqlHandshake.Parse(FrameWithDeclaredLength(100, new byte[10]));
            return f.Kind == MySqlFrameKind.Unknown && f.Detail.Contains("截断");
        });

        check("首字节既非 0x0A 也非 0xFF → Unknown 且 Detail 报出该字节", () =>
        {
            var f = MySqlHandshake.Parse(Frame(0x09, 0x01, 0x02));
            return f.Kind == MySqlFrameKind.Unknown && f.Detail.Contains("0x09");
        });

        // 明文 HTTP 响应被误当成 MySQL 帧，是配错 serverUrl（指到普通 web 端口）时
        // 最可能出现的情况。必须走到 Unknown 分支而不是抛异常。
        check("HTTP 响应文本 → Unknown（配错 serverUrl 的典型输入）", () =>
        {
            var http = Encoding.ASCII.GetBytes("HTTP/1.1 404 Not Found\r\n\r\n");
            return MySqlHandshake.Parse(http).Kind == MySqlFrameKind.Unknown;
        });

        check("握手包缺 NUL 结束符 → Unknown", () =>
        {
            // 0x0A 后全是非零字节，永远等不到结束符
            var payload = new byte[] { 0x0A, (byte)'8', (byte)'.', (byte)'0' };
            var f = MySqlHandshake.Parse(Frame(payload));
            return f.Kind == MySqlFrameKind.Unknown;
        });

        check("版本串超长（>128 字节）→ Unknown", () =>
        {
            var f = MySqlHandshake.Parse(Frame(HandshakePayload(new string('9', 200))));
            return f.Kind == MySqlFrameKind.Unknown;
        });

        check("128 字节版本串仍在上限内，正常解出", () =>
        {
            var v = new string('9', 128);
            var f = MySqlHandshake.Parse(Frame(HandshakePayload(v)));
            return f.Kind == MySqlFrameKind.Handshake && f.ServerVersion == v;
        });

        // 探活是诊断工具，它自己抛异常就失去了全部意义。这条用随机字节暴力扫一遍。
        check("2000 组随机字节全部不抛异常", () =>
        {
            var rng = new Random(20260909);
            var buf = new byte[64];
            for (int i = 0; i < 2000; i++)
            {
                rng.NextBytes(buf);
                var len = rng.Next(0, buf.Length + 1);
                MySqlHandshake.Parse(buf.AsSpan(0, len));
            }
            return true;
        });


        Console.WriteLine();
        Console.WriteLine("[10] 健康检查配置：health.intervalSec 越界必须在加载阶段被拒");
        // SECTION_CONFIG

        check("不写 health 段 → 默认关闭，间隔为 300", () =>
        {
            var (cfgs, errs) = LoadHealth(null);
            return errs.Count == 0 && cfgs.Count == 1
                && !cfgs[0].HealthCheckEnabled && cfgs[0].HealthIntervalSec == 300;
        });

        check("health.enabled: true 生效", () =>
        {
            var (cfgs, errs) = LoadHealth("health:\n  enabled: true\n");
            return errs.Count == 0 && cfgs.Count == 1 && cfgs[0].HealthCheckEnabled;
        });

        check("health.intervalSec: 60 生效", () =>
        {
            var (cfgs, errs) = LoadHealth("health:\n  enabled: true\n  intervalSec: 60\n");
            return errs.Count == 0 && cfgs.Count == 1 && cfgs[0].HealthIntervalSec == 60;
        });

        check("下限 30 恰好被接受", () =>
        {
            var (cfgs, errs) = LoadHealth("health:\n  enabled: true\n  intervalSec: 30\n");
            return errs.Count == 0 && cfgs.Count == 1 && cfgs[0].HealthIntervalSec == 30;
        });

        // 越界必须硬拒绝而不是悄悄夹到 30：夹一下会让配置文件里的数字
        // 与实际行为不一致，下次排查时又多一层误导。
        check("29 被拒（刚过下限），且不是夹到 30 后放行", () =>
        {
            var (cfgs, errs) = LoadHealth("health:\n  enabled: true\n  intervalSec: 29\n");
            return cfgs.Count == 0 && errs.Count == 1;
        });

        check("1 被拒", () =>
        {
            var (cfgs, errs) = LoadHealth("health:\n  enabled: true\n  intervalSec: 1\n");
            return cfgs.Count == 0 && errs.Count == 1;
        });

        check("0 被拒", () =>
        {
            var (cfgs, errs) = LoadHealth("health:\n  enabled: true\n  intervalSec: 0\n");
            return cfgs.Count == 0 && errs.Count == 1;
        });

        check("负数被拒", () =>
        {
            var (cfgs, errs) = LoadHealth("health:\n  enabled: true\n  intervalSec: -60\n");
            return cfgs.Count == 0 && errs.Count == 1;
        });

        check("拒绝时给出可操作的中文诊断（含数值与下限）", () =>
        {
            var (_, errs) = LoadHealth("health:\n  enabled: true\n  intervalSec: 5\n");
            if (errs.Count != 1) return false;
            var m = errs[0].Message ?? string.Empty;
            return m.Contains("intervalSec") && m.Contains('5') && m.Contains("30");
        });

        // enabled: false 时 intervalSec 写多小都无所谓 —— 它不会被用到。
        // 为一个不生效的字段拒绝整个项目，是过度校验。
        check("enabled: false 时越界的 intervalSec 不拦（该值不会被使用）", () =>
        {
            var (cfgs, errs) = LoadHealth("health:\n  enabled: false\n  intervalSec: 1\n");
            return errs.Count == 0 && cfgs.Count == 1 && !cfgs[0].HealthCheckEnabled;
        });

        check("defaults 段的 health 能被项目继承", () =>
        {
            var dir = NewDir();
            File.WriteAllText(Path.Combine(dir, "00-defaults.yaml"),
                "defaults:\n  health:\n    enabled: true\n    intervalSec: 120\n");
            File.WriteAllText(Path.Combine(dir, "p.yaml"), ProjectYaml("p", 5010, null));
            var (cfgs, errs) = ConfigLoader.Load(dir);
            return errs.Count == 0 && cfgs.Count == 1
                && cfgs[0].HealthCheckEnabled && cfgs[0].HealthIntervalSec == 120;
        });

        check("项目级 health 覆盖 defaults", () =>
        {
            var dir = NewDir();
            File.WriteAllText(Path.Combine(dir, "00-defaults.yaml"),
                "defaults:\n  health:\n    enabled: true\n    intervalSec: 120\n");
            File.WriteAllText(Path.Combine(dir, "p.yaml"),
                ProjectYaml("p", 5011, "health:\n  intervalSec: 600\n"));
            var (cfgs, errs) = ConfigLoader.Load(dir);
            return errs.Count == 0 && cfgs.Count == 1 && cfgs[0].HealthIntervalSec == 600;
        });

        // 老配置文件（v1.2.x 就已经在用户机器上的那些）绝不能因为新增字段而加载失败。
        check("完全不含 health 键的老配置照常加载", () =>
        {
            var dir = NewDir();
            File.WriteAllText(Path.Combine(dir, "legacy.yaml"), ProjectYaml("legacy", 5012, null));
            var (cfgs, errs) = ConfigLoader.Load(dir);
            return errs.Count == 0 && cfgs.Count == 1;
        });


        Console.WriteLine();
        Console.WriteLine("[11] HealthReport 的三态与摘要");
        // SECTION_REPORT

        check("三层全 Pass → IsHealthy 为 true，FirstFailure 为 null", () =>
        {
            var r = Report(HealthStatus.Pass, HealthStatus.Pass, HealthStatus.Pass);
            return r.IsHealthy && r.FirstFailure == null;
        });

        // Skipped 绝不能被当成 Pass —— 把没验过的东西显示成绿勾是最坏的一种谎。
        check("含 Skipped 层 → IsHealthy 必须为 false", () =>
        {
            var r = Report(HealthStatus.Pass, HealthStatus.Fail, HealthStatus.Skipped);
            return !r.IsHealthy;
        });

        check("Pass + Pass + Skipped 也不算健康", () =>
        {
            var r = Report(HealthStatus.Pass, HealthStatus.Pass, HealthStatus.Skipped);
            return !r.IsHealthy;
        });

        check("空 Stages 不算健康（没跑过 ≠ 通过）", () =>
            !new HealthReport().IsHealthy);

        check("FirstFailure 取第一个 Fail 而非最后一个", () =>
        {
            var r = Report(HealthStatus.Pass, HealthStatus.Fail, HealthStatus.Fail);
            return r.FirstFailure?.Stage == HealthStage.Authentication;
        });

        check("FirstFailure 跳过 Skipped 只认 Fail", () =>
        {
            var r = Report(HealthStatus.Fail, HealthStatus.Skipped, HealthStatus.Skipped);
            return r.FirstFailure?.Stage == HealthStage.Transport;
        });

        check("健康时 OneLine 含耗时与 MySQL 版本", () =>
        {
            var r = new HealthReport
            {
                Stages = new[] { Stage(HealthStage.Transport, HealthStatus.Pass) },
                TotalMs = 123,
                ServerVersion = "8.0.36"
            };
            var s = r.OneLine();
            return s.Contains("123") && s.Contains("8.0.36") && s.Contains("通过");
        });

        check("失败时 OneLine 指出失败在哪一层", () =>
        {
            var r = Report(HealthStatus.Pass, HealthStatus.Fail, HealthStatus.Skipped);
            var s = r.OneLine();
            return s.Contains("加密认证") && s.Contains("失败");
        });

        check("三层的中文名互不相同且非空", () =>
        {
            var a = HealthReport.StageName(HealthStage.Transport);
            var b = HealthReport.StageName(HealthStage.Authentication);
            var c = HealthReport.StageName(HealthStage.Database);
            return a.Length > 0 && b.Length > 0 && c.Length > 0 && a != b && b != c && a != c;
        });

    }

        // SECTION_HELPERS

    /// <summary>
    /// 拼一个 MySQL 报文：3 字节负载长度（小端） + 1 字节序号 + 负载。
    ///
    /// <para>长度字段由 payload 实际长度算出，而不是由调用方传 ——
    /// 否则每个用例都要自己算一遍，算错了测的就不是解析器而是测试自己。
    /// 需要构造「声明长度与实际不符」的截断包时走 <see cref="FrameWithDeclaredLength"/>。</para>
    /// </summary>
    private static byte[] Frame(params byte[] payload) =>
        FrameWithDeclaredLength(payload.Length, payload);

    /// <summary>长度字段与实际负载脱钩的版本，专门用来造截断包。</summary>
    private static byte[] FrameWithDeclaredLength(int declared, byte[] payload)
    {
        var buf = new byte[4 + payload.Length];
        buf[0] = (byte)(declared & 0xFF);
        buf[1] = (byte)((declared >> 8) & 0xFF);
        buf[2] = (byte)((declared >> 16) & 0xFF);
        buf[3] = 0; // 序号
        Array.Copy(payload, 0, buf, 4, payload.Length);
        return buf;
    }

    /// <summary>握手初始化包 v10 的负载：0x0A + 版本字符串 + NUL + 后续字段。</summary>
    private static byte[] HandshakePayload(string version, int trailing = 20)
    {
        var v = Encoding.ASCII.GetBytes(version);
        var payload = new byte[1 + v.Length + 1 + trailing];
        payload[0] = 0x0A;
        Array.Copy(v, 0, payload, 1, v.Length);
        payload[1 + v.Length] = 0; // NUL 结束符
        // trailing 部分模拟 thread id / 盐值等字段，内容无所谓但不能全是 0，
        // 否则「解析器提前撞上 0 就停」这类缺陷会被掩盖掉。
        for (int i = 0; i < trailing; i++)
            payload[2 + v.Length + i] = (byte)(i + 1);
        return payload;
    }

    /// <summary>ERR 包负载：0xFF + 错误码（小端） + [可选 '#'+5 字节 SQLState] + 文本。</summary>
    private static byte[] ErrPayload(int code, string text, string? sqlState)
    {
        var t = Encoding.UTF8.GetBytes(text);
        var state = sqlState == null ? Array.Empty<byte>() : Encoding.ASCII.GetBytes("#" + sqlState);
        var payload = new byte[3 + state.Length + t.Length];
        payload[0] = 0xFF;
        payload[1] = (byte)(code & 0xFF);
        payload[2] = (byte)((code >> 8) & 0xFF);
        Array.Copy(state, 0, payload, 3, state.Length);
        Array.Copy(t, 0, payload, 3 + state.Length, t.Length);
        return payload;
    }

    private static string ProjectYaml(string name, int port, string? healthBlock) =>
        $"name: {name}\n"
        + "serverUrl: http://127.0.0.1:8080\n"
        + "aesKey: verify-aes-key\n"
        + "authKey: verify-auth-key\n"
        + "local:\n"
        + $"  port: {port}\n"
        + (healthBlock ?? string.Empty);

    /// <summary>每个用例一个独立临时目录，避免上一轮残留的 yaml 影响结论。</summary>
    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(),
            "cryptunnel-health-verify-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static (List<Cryptunnel.Core.Models.TunnelConfig> Configs, List<ConfigError> Errors)
        LoadHealth(string? healthBlock)
    {
        var dir = NewDir();
        File.WriteAllText(Path.Combine(dir, "demo.yaml"), ProjectYaml("demo", 5009, healthBlock));
        return ConfigLoader.Load(dir);
    }

    private static HealthStageResult Stage(HealthStage s, HealthStatus st) =>
        new(s, st, st == HealthStatus.Skipped ? 0 : 10, "summary", st == HealthStatus.Fail ? "advice" : null);

    private static HealthReport Report(HealthStatus t, HealthStatus a, HealthStatus d) =>
        new()
        {
            Stages = new[]
            {
                Stage(HealthStage.Transport, t),
                Stage(HealthStage.Authentication, a),
                Stage(HealthStage.Database, d)
            },
            TotalMs = 30
        };
}
