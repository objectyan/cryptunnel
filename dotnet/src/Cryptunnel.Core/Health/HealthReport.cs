using System;
using System.Collections.Generic;

namespace Cryptunnel.Core.Health;

/// <summary>探活单层的判定结果。</summary>
public enum HealthStatus
{
    /// <summary>该层通过。</summary>
    Pass,

    /// <summary>该层失败。</summary>
    Fail,

    /// <summary>
    /// 未执行。<b>必须与 <see cref="Pass"/> 严格区分</b> ——
    /// 前一层失败后，后续层没有跑过，把它显示成「通过」是在撒谎，
    /// 显示成「失败」则会把用户引向一个其实没被验证过的方向。
    /// </summary>
    Skipped
}

/// <summary>
/// 探活的层次。顺序即执行顺序，也是界面上从上到下的显示顺序。
/// </summary>
public enum HealthStage
{
    /// <summary>与服务端建立 TCP/TLS 并完成 WebSocket 握手。能同时验到网络可达与 WAF 是否放行。</summary>
    Transport,

    /// <summary>加密认证。一次性覆盖 authKey、aesKey、cipher 三项配置是否与服务端一致。</summary>
    Authentication,

    /// <summary>服务端到 MySQL 的可达性。判据是能否收到 MySQL 握手包。</summary>
    Database
}

/// <summary>
/// 单层探活结果。
/// </summary>
/// <param name="Stage">层次。</param>
/// <param name="Status">判定。</param>
/// <param name="ElapsedMs">
/// 本层耗时（毫秒）。<see cref="HealthStatus.Skipped"/> 时为 0。
/// <para>分层计时不是装饰：总耗时 10 秒时，卡在 Transport 说明是网络/WAF 问题，
/// 卡在 Database 说明是服务端连数据库慢 —— 一个总数分不出这两者。</para>
/// </param>
/// <param name="Summary">一句话结论。</param>
/// <param name="Advice">失败时的排查建议；通过时为 <c>null</c>。</param>
public readonly record struct HealthStageResult(
    HealthStage Stage,
    HealthStatus Status,
    long ElapsedMs,
    string Summary,
    string? Advice);

/// <summary>
/// 一次完整探活的结果。
/// </summary>
public sealed class HealthReport
{
    /// <summary>项目名（配置中的 name）。</summary>
    public string ProjectName { get; init; } = "";

    /// <summary>项目显示名。</summary>
    public string DisplayName { get; init; } = "";

    /// <summary>
    /// 探活发起时刻，<b>本地时间</b>（<c>DateTime.Now</c>）。
    /// <para>这里刻意不用 UTC：本字段唯一的用途是显示给用户看
    /// （报告标题、状态列的「链路正常 21:05」），而用户对照的是自己的墙上时钟。
    /// 存 UTC 会让每个消费方都得记着转一次，漏一处就是差 8 小时的错误时间。</para>
    /// </summary>
    public DateTime StartedAt { get; init; }

    /// <summary>各层结果，顺序与 <see cref="HealthStage"/> 一致。</summary>
    public IReadOnlyList<HealthStageResult> Stages { get; init; } = Array.Empty<HealthStageResult>();

    /// <summary>端到端总耗时（毫秒）。</summary>
    public long TotalMs { get; init; }

    /// <summary>探到的 MySQL 版本。未探到时为 <c>null</c>。</summary>
    public string? ServerVersion { get; init; }

    /// <summary>本次探活使用的加密算法标识，用于在报告里直接核对两端配置。</summary>
    public string CipherId { get; init; } = "";

    /// <summary>全部三层都通过才算健康。</summary>
    public bool IsHealthy
    {
        get
        {
            foreach (var s in Stages)
                if (s.Status != HealthStatus.Pass) return false;
            return Stages.Count > 0;
        }
    }

    /// <summary>
    /// 第一个失败的层。全通过时为 <c>null</c>。
    ///
    /// <para>诊断时只有第一个失败点有意义 —— 后面的层要么没跑，要么是它的连带后果。</para>
    /// </summary>
    public HealthStageResult? FirstFailure
    {
        get
        {
            foreach (var s in Stages)
                if (s.Status == HealthStatus.Fail) return s;
            return null;
        }
    }

    /// <summary>供日志与托盘提示使用的一行摘要。</summary>
    public string OneLine()
    {
        if (IsHealthy)
        {
            var ver = string.IsNullOrEmpty(ServerVersion) ? "" : $"，MySQL {ServerVersion}";
            return $"健康检查通过（{TotalMs}ms{ver}）";
        }

        var f = FirstFailure;
        return f.HasValue
            ? $"健康检查失败于「{StageName(f.Value.Stage)}」：{f.Value.Summary}"
            : "健康检查未执行";
    }

    /// <summary>层次的中文名。界面与日志共用，避免两处各写一套措辞。</summary>
    public static string StageName(HealthStage stage) => stage switch
    {
        HealthStage.Transport => "网络连通",
        HealthStage.Authentication => "加密认证",
        HealthStage.Database => "数据库可达",
        _ => stage.ToString()
    };
}
