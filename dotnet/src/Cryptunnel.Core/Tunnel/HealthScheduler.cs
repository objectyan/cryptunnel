using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Cryptunnel.Core.Health;
using Cryptunnel.Core.Logging;
using Cryptunnel.Core.Models;

namespace Cryptunnel.Core.Tunnel;

/// <summary>
/// 周期性健康检查的调度器。
///
/// <para><b>默认不启用</b>：只有配置里显式写了 <c>health.enabled: true</c> 的项目
/// 才会被排进来。理由见 <see cref="TunnelConfig.HealthCheckEnabled"/> ——
/// 每次检查都会真实占用服务端一个连接名额。</para>
///
/// <para><b>日志策略：只在状态发生变化时打</b>。这是本类最重要的设计约束。
/// 一个每 5 分钟打一行「健康检查通过」的功能，会在一天内往日志里堆 288 行噪声，
/// 把真正的错误淹没掉 —— 用户排查问题时最不需要的就是翻过几百行「一切正常」。
/// 所以：<b>持续健康时完全静默</b>，只在 健康→故障 和 故障→恢复 这两个跳变点发声。</para>
/// </summary>
public sealed class HealthScheduler : IDisposable
{
    private readonly ILogger _logger;
    private readonly object _lock = new();
    private readonly Dictionary<string, Entry> _entries = new();
    private readonly CancellationTokenSource _cts = new();
    private Timer? _timer;

    /// <summary>调度器的轮询节拍。各项目按自己的间隔在这个节拍上判断是否该跑。</summary>
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 探活完成后的回调（项目名, 报告）。UI 层用它更新界面。
    /// <para>回调在后台线程触发，订阅方自行编组到 UI 线程。</para>
    /// </summary>
    public event Action<string, HealthReport>? Checked;

    private sealed class Entry
    {
        public TunnelConfig Config = null!;
        public DateTime NextRun;

        /// <summary>上一次的健康状态。<c>null</c> 表示还没跑过。</summary>
        public bool? LastHealthy;

        /// <summary>本项目是否正有一次探活在途，防止慢探活堆叠。</summary>
        public int InFlight;
    }

    public HealthScheduler(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// 按最新配置重建调度表。
    ///
    /// <para>每次配置重载都整体重建，而不是做增量 diff：调度状态很轻
    /// （下次运行时刻 + 上次结果），重建的代价只是丢掉「上次是否健康」，
    /// 最坏结果是恢复后多打一行日志。为省这一行而引入增量 diff 的分支，
    /// 换来的是「改了配置但调度没更新」这类难查的问题。</para>
    /// </summary>
    public void Configure(IEnumerable<TunnelConfig> configs)
    {
        lock (_lock)
        {
            var keep = new HashSet<string>(StringComparer.Ordinal);

            foreach (var c in configs)
            {
                if (!c.Enabled || !c.HealthCheckEnabled) continue;
                keep.Add(c.Name);

                if (_entries.TryGetValue(c.Name, out var e))
                {
                    // 已存在：只换配置，保留 NextRun 与 LastHealthy，
                    // 避免用户每保存一次配置就立刻触发一轮探活。
                    e.Config = c;
                }
                else
                {
                    _entries[c.Name] = new Entry
                    {
                        Config = c,
                        // 首次不立刻跑：应用刚启动时隧道还在建立，
                        // 此刻探活大概率失败并吓到用户。等一个间隔再说。
                        NextRun = DateTime.UtcNow.AddSeconds(c.HealthIntervalSec),
                        LastHealthy = null
                    };
                    _logger.Log(LogLevel.Info, c.Name,
                        $"已启用周期性健康检查，每 {c.HealthIntervalSec} 秒一次"
                        + "（每次会在服务端建立并断开一条数据库连接）");
                }
            }

            foreach (var name in new List<string>(_entries.Keys))
                if (!keep.Contains(name))
                    _entries.Remove(name);

            if (_entries.Count > 0 && _timer == null)
                _timer = new Timer(OnTick, null, Tick, Tick);
            else if (_entries.Count == 0 && _timer != null)
            {
                // 没有项目需要检查时停掉计时器，而不是让它空转 ——
                // 空转会阻止进程在空闲时降低唤醒频率，对笔记本电池是可见的浪费。
                _timer.Dispose();
                _timer = null;
            }
        }
    }

    private void OnTick(object? _)
    {
        if (_cts.IsCancellationRequested) return;

        List<Entry> due = new();
        var now = DateTime.UtcNow;

        lock (_lock)
        {
            foreach (var e in _entries.Values)
                if (e.NextRun <= now && Volatile.Read(ref e.InFlight) == 0)
                    due.Add(e);
        }

        foreach (var e in due)
        {
            // 防重入：探活最长可能耗到 WsConnectMs + AuthResponseMs。
            // 若间隔被配得比这还短，没有这道锁就会不断叠加在途请求，
            // 把服务端连接数吃满 —— 而那正是本功能最该避免的后果。
            if (Interlocked.CompareExchange(ref e.InFlight, 1, 0) != 0) continue;
            _ = RunOneAsync(e);
        }
    }

    private async Task RunOneAsync(Entry e)
    {
        var cfg = e.Config;
        try
        {
            var report = await HealthProbe.RunAsync(cfg, _cts.Token);
            if (_cts.IsCancellationRequested) return;

            var healthy = report.IsHealthy;
            var prev = e.LastHealthy;
            e.LastHealthy = healthy;

            // 只在跳变点发声。持续健康时一个字都不打。
            if (prev == null)
            {
                // 首次结果：健康就静默建立基线，有问题才说。
                if (!healthy)
                    _logger.Log(LogLevel.Warn, cfg.Name, $"周期性健康检查：{report.OneLine()}");
            }
            else if (prev.Value && !healthy)
            {
                _logger.Log(LogLevel.Warn, cfg.Name, $"周期性健康检查：状态由正常转为异常。{report.OneLine()}");
            }
            else if (!prev.Value && healthy)
            {
                _logger.Log(LogLevel.Info, cfg.Name, $"周期性健康检查：已恢复正常（{report.TotalMs}ms）");
            }

            Checked?.Invoke(cfg.Name, report);
        }
        catch (Exception ex)
        {
            // 调度器绝不能因单个项目的异常而停摆 —— 那会让其余项目的检查一起消失，
            // 且没有任何迹象。
            _logger.Log(LogLevel.Warn, cfg.Name, $"周期性健康检查执行异常：{ex.Message}");
        }
        finally
        {
            // 从「本次结束」而非「本次开始」计时。探活耗时 10 秒时，
            // 按开始计时会让实际间隔缩水，慢链路上会越跑越密。
            e.NextRun = DateTime.UtcNow.AddSeconds(Math.Max(
                TunnelConfig.HealthIntervalMinSec, cfg.HealthIntervalSec));
            Volatile.Write(ref e.InFlight, 0);
        }
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        lock (_lock)
        {
            _timer?.Dispose();
            _timer = null;
            _entries.Clear();
        }
        _cts.Dispose();
    }
}
