using System;
using System.Collections.Generic;
using Cryptunnel.Core.Logging;
using Cryptunnel.Core.Models;
using Cryptunnel.Core.Stats;

namespace Cryptunnel.Core.Tunnel;

public sealed class TunnelManager : IDisposable
{
    private readonly ILogger _logger;
    private readonly ITunnelTap _tap;
    private readonly StatsCollector _stats;
    private readonly Dictionary<string, Tunnel> _tunnels = new();
    private readonly object _lock = new();

    public event Action<string, TunnelState>? StateChanged;
    public event Action<string>? FatalError;

    public TunnelManager(ILogger logger, ITunnelTap tap, StatsCollector stats)
    {
        _logger = logger;
        _tap = tap;
        _stats = stats;
    }

    public void StartAll(IEnumerable<TunnelConfig> configs)
    {
        foreach (var cfg in configs)
        {
            if (!cfg.Enabled)
            {
                _logger.Log(LogLevel.Info, cfg.Name, "已禁用（enabled:false），不启动");
                continue;
            }
            WarnIfNonLoopback(cfg);

            var t = new Tunnel(cfg, _logger, _tap, _stats);
            t.StateChanged += (n, s) => StateChanged?.Invoke(n, s);
            t.FatalError += OnFatal;
            lock (_lock) _tunnels[cfg.Name] = t;
            t.Start();
        }
    }

    /// <summary>
    /// 非回环监听地址的安全告警。
    ///
    /// <para><b>这是本客户端唯一一个能让本机之外的人穿透防火墙的配置项</b>。
    /// 监听 0.0.0.0 / :: / 具体网卡地址时，局域网内任意机器都能连上这个端口，
    /// 而隧道会用本地配置好的密钥替对方完成认证 —— 也就是说，
    /// <b>攻击者不需要知道 aesKey 或 authKey，就能访问内网数据库</b>。</para>
    ///
    /// <para>能走到这里，说明配置里已经显式写了 <c>local.allowNonLoopback: true</c> ——
    /// 未授权的非回环配置在 <c>ConfigLoader</c> 加载阶段就被拒绝了，隧道根本不会启动。
    /// 所以这里的措辞是「你已开启，请确认风险」，而不是「请改回去」：
    /// <b>对一个用户刚刚显式做出的选择反复说教，只会训练用户忽略告警</b>。
    /// 但它仍必须每次启动都打印 —— 开关是一次性动作，风险是持续存在的。</para>
    ///
    /// <para><b>StartAll 与 StartOne 两条路径都要调用</b>。此前只有 StartAll 检查，
    /// 从界面单独启动一个项目时告警会完全消失，而那恰恰是最常用的启动方式。</para>
    /// </summary>
    private void WarnIfNonLoopback(TunnelConfig cfg)
    {
        if (ListenAddressPolicy.IsLoopback(cfg.ListenAddress)) return;

        _logger.Log(LogLevel.Warn, cfg.Name,
            "========== 安全警告 ==========");
        _logger.Log(LogLevel.Warn, cfg.Name,
            $"监听地址为 {cfg.ListenAddress}（已由 local.allowNonLoopback 显式授权）。"
            + $"局域网内的其它机器可以直接连接本机 {cfg.LocalPort} 端口，并通过本隧道访问内网数据库。");
        _logger.Log(LogLevel.Warn, cfg.Name,
            "对方无需知道 aesKey 或 authKey —— 本客户端会用你配置的密钥替对方完成认证。");
        _logger.Log(LogLevel.Warn, cfg.Name,
            "请确认本机所在网络可信；不再需要跨机访问时，请移除该开关并把 local.address 改回 127.0.0.1。");
        _logger.Log(LogLevel.Warn, cfg.Name,
            "==============================");
    }

    private void OnFatal(string name)
    {
        _logger.Log(LogLevel.Error, name, "隧道因致命错误停止，其余项目不受影响继续运行（故障隔离）");
        FatalError?.Invoke(name);
    }

    public void StopOne(string name)
    {
        lock (_lock)
        {
            if (_tunnels.TryGetValue(name, out var t))
            {
                t.Dispose();
                _tunnels.Remove(name);
            }
        }
    }

    public void StartOne(TunnelConfig cfg)
    {
        lock (_lock)
        {
            if (_tunnels.ContainsKey(cfg.Name)) return;
            WarnIfNonLoopback(cfg);
            var t = new Tunnel(cfg, _logger, _tap, _stats);
            t.StateChanged += (n, s) => StateChanged?.Invoke(n, s);
            t.FatalError += OnFatal;
            _tunnels[cfg.Name] = t;
            t.Start();
        }
    }

    public void StopAll()
    {
        lock (_lock)
        {
            foreach (var t in _tunnels.Values) t.Dispose();
            _tunnels.Clear();
        }
    }

    public IReadOnlyDictionary<string, Tunnel> Tunnels
    {
        get { lock (_lock) return new Dictionary<string, Tunnel>(_tunnels); }
    }

    public void Dispose() => StopAll();
}
