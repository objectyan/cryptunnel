using System.Reflection;
using System.Text.RegularExpressions;

namespace FramingVerify;

/// <summary>
/// close reason 映射表的完整性校验。
///
/// <para><b>为什么要对着服务端源码验</b>：<c>CloseReasonMapper</c> 用的是精确字符串匹配，
/// 服务端 reason 少一个字、多半句后缀，对应分支就静默失效 —— 落到未知分支后被判为
/// 「可重试」，于是一个本该立刻停下来让用户改配置的错误，变成了无休止的重连。
/// 移植这个类时就真的踩到了：客户端写的是 <c>"Plain-text auth rejected"</c>，
/// 服务端实际发的是 <c>"Plain-text auth rejected - use encrypted auth"</c>，
/// 那条分支从来没有命中过。只 grep 客户端自己的代码是发现不了这种问题的。</para>
///
/// <para><b>找不到服务端源码时跳过而不是通过</b>：服务端在另一个仓库，路径因机器而异。
/// 拿不到就明确打印 SKIP —— 把「没验」记成「验过了」是这类校验最坏的失效方式。</para>
/// </summary>
internal static class CloseReasonChecks
{
    /// <summary>
    /// 服务端仓库的候选位置。第一个能读到 Handler 源码的即采用。
    /// </summary>
    private static readonly string[] ServerRepoCandidates =
    [
        @"D:\Sunrise\Coding\CRM\sunrise",
        @"..\..\..\..\..\sunrise",
    ];

    /// <summary>
    /// Handler 源码在服务端仓库内的候选相对路径。
    ///
    /// <para><b>为什么要列两个</b>：服务端仓库尚未随本仓库更名（包仍是
    /// <c>com.crm.sunrise.jdbcproxy</c>、类仍是 <c>JdbcProxyWebSocketHandler</c>）。
    /// 只写更名后的路径会让这项校验**静默退化成永久 SKIP** —— 文件明明在磁盘上，
    /// 却因为找的是一个还不存在的名字而跳过，输出里只剩一行「未找到」，
    /// 看起来像「服务端不在本机」这种正常情况，实际是校验本身失效了。
    /// 这个坑是真发生过的：全仓改名脚本把这里的旧路径也一起替换了
    /// （它的 PROTECT 列表当时只覆盖点号与正斜杠形态，漏了 Windows 反斜杠形态），
    /// 于是本节从 PASS 悄悄掉成 SKIP，而退出码仍是 0。</para>
    ///
    /// <para>列出新旧两条后，服务端迁移前后都能验，迁移当天也不需要改这里。</para>
    /// </summary>
    private static readonly string[] HandlerRelativePathCandidates =
    [
        // 更名后（服务端迁移到 cryptunnel-starter-javax 之后）
        @"src\main\java\com\crm\sunrise\cryptunnel\CryptunnelWebSocketHandler.java",
        // 更名前（当前实际状态）
        @"src\main\java\com\crm\sunrise\jdbcproxy\JdbcProxyWebSocketHandler.java",
    ];

    /// <summary>匹配 <c>CloseStatus.XXX.withReason("....")</c> 里的字面量。</summary>
    private static readonly Regex ReasonLiteral =
        new(@"withReason\(""([^""]+)""\)", RegexOptions.Compiled);

    internal static void Run(Action<string, Func<bool>> check, Action<string> skip)
    {
        Console.WriteLine();
        Console.WriteLine("[13] close reason 映射必须覆盖服务端实际发出的全部原因");

        var mapper = typeof(Cryptunnel.Core.Crypto.CipherRegistry).Assembly
            .GetType("Cryptunnel.Core.Tunnel.CloseReasonMapper", throwOnError: true)!;
        var map = mapper.GetMethod("Map", BindingFlags.NonPublic | BindingFlags.Static)!;

        // TunnelFailure 是 internal record struct，用反射读它的两个属性。
        var failureType = map.ReturnType;
        var msgProp = failureType.GetProperty("Message")!;
        var retryProp = failureType.GetProperty("Retryable")!;

        (string Message, bool Retryable) Map(string? reason)
        {
            var r = map.Invoke(null, [reason])!;
            return ((string)msgProp.GetValue(r)!, (bool)retryProp.GetValue(r)!);
        }

        // ---- 先验不依赖服务端源码的性质 ----

        check("reason 为 null 时判为可重试（网络断开而非服务端拒绝）", () =>
        {
            var (msg, retryable) = Map(null);
            return retryable && msg.Length > 0;
        });

        check("空白 reason 与 null 同等处理", () => Map("   ").Retryable);

        check("未知 reason 保留英文原文（便于用户搜索/反馈）", () =>
        {
            var (msg, retryable) = Map("Some Future Reason");
            return msg.Contains("Some Future Reason", StringComparison.Ordinal) && retryable;
        });

        check("匹配忽略前后空格", () =>
            Map("  Auth key invalid  ").Message == Map("Auth key invalid").Message);

        check("配置类错误判为不可重试（重连只会同样失败）", () =>
            !Map("Auth key invalid").Retryable
            && !Map("Auth format error").Retryable
            && !Map("Auth decrypt failed").Retryable);

        check("Auth decrypt failed 必须同时点出算法与密钥两种可能", () =>
        {
            var msg = Map("Auth decrypt failed").Message;
            return msg.Contains("cipher", StringComparison.OrdinalIgnoreCase)
                && msg.Contains("aesKey", StringComparison.OrdinalIgnoreCase);
        });

        // ---- 再对着服务端源码验覆盖完整性 ----

        var handler = ResolveHandlerPath();
        if (handler == null)
        {
            skip("未找到服务端 WebSocket Handler 源码，跳过覆盖完整性比对"
                 + $"（仓库候选：{string.Join(" / ", ServerRepoCandidates)}"
                 + $"；文件候选：{string.Join(" / ", HandlerRelativePathCandidates)}）");
            return;
        }

        var serverReasons = ReasonLiteral
            .Matches(File.ReadAllText(handler))
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        Console.WriteLine($"  （服务端源码：{handler}）");
        Console.WriteLine($"  （提取到 {serverReasons.Count} 条 close reason）");

        check($"服务端 {serverReasons.Count} 条 reason 全部有专属映射（无一落到未知分支）", () =>
        {
            var unmapped = serverReasons
                .Where(r => Map(r).Message.Contains($"连接被服务端关闭：{r}", StringComparison.Ordinal))
                .ToList();

            if (unmapped.Count > 0)
                Console.WriteLine($"    未映射：{string.Join(", ", unmapped)}");

            return unmapped.Count == 0;
        });

        // 逐条列出，让验收输出本身就是一份可读的对照表。
        foreach (var reason in serverReasons)
        {
            var (msg, retryable) = Map(reason);
            var tag = retryable ? "可重试" : "需人工";
            check($"  {reason} → [{tag}] {Truncate(msg, 34)}", () =>
                !msg.Contains("连接被服务端关闭：", StringComparison.Ordinal) && msg.Length > 0);
        }
    }

    private static string? ResolveHandlerPath()
    {
        foreach (var repo in ServerRepoCandidates)
        {
            foreach (var rel in HandlerRelativePathCandidates)
            {
                try
                {
                    var full = Path.GetFullPath(Path.Combine(repo, rel));
                    if (File.Exists(full)) return full;
                }
                catch (ArgumentException) { /* 路径非法，试下一个 */ }
            }
        }

        return null;
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : string.Concat(s.AsSpan(0, max), "…");
}
