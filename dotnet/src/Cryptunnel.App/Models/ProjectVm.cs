using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using Cryptunnel.Core.Health;
using Cryptunnel.Core.Models;
using Cryptunnel.Core.Tunnel;

namespace Cryptunnel.App.Models;

public sealed class ProjectVm : INotifyPropertyChanged
{
    private TunnelState _state = TunnelState.Stopped;
    private long _bytesIn;
    private long _bytesOut;
    private int _errors;
    private int _warnings;
    private int _connections;
    private int _reconnects;
    private int _activeConnections;
    private string _transport = "-";
    private bool _running;
    private DateTime? _connectedSince;
    private string? _lastError;
    private bool? _healthy;
    private DateTime? _healthCheckedAt;
    private string? _healthSummary;

    public string Name { get; }
    public string DisplayName { get; set; }
    public int LocalPort { get; }
    public string ServerUrl { get; }
    public string ListenAddress { get; }
    public bool Enabled { get; }

    public TunnelState State
    {
        get => _state;
        set { _state = value; OnChanged(); OnChanged(nameof(StatusText)); }
    }
    public bool Running { get => _running; set { _running = value; OnChanged(); } }
    public long BytesIn { get => _bytesIn; set { _bytesIn = value; OnChanged(); } }
    public long BytesOut { get => _bytesOut; set { _bytesOut = value; OnChanged(); } }
    public int Errors { get => _errors; set { _errors = value; OnChanged(); } }
    public int Warnings { get => _warnings; set { _warnings = value; OnChanged(); } }
    public int Connections { get => _connections; set { _connections = value; OnChanged(); } }
    public int Reconnects { get => _reconnects; set { _reconnects = value; OnChanged(); } }
    public int ActiveConnections { get => _activeConnections; set { _activeConnections = value; OnChanged(); } }
    public string Transport { get => _transport; set { _transport = value; OnChanged(); } }
    public DateTime? ConnectedSince
    {
        get => _connectedSince;
        set { _connectedSince = value; OnChanged(); OnChanged(nameof(DurationText)); }
    }
    public string? LastError { get => _lastError; set { _lastError = value; OnChanged(); } }

    public string StatusText => State switch
    {
        TunnelState.Listening => "监听中",
        TunnelState.WsConnecting => "WS 连接中",
        TunnelState.WsConnected => "WS 已连",
        TunnelState.HttpConnecting => "HTTP 连接中",
        TunnelState.HttpConnected => "HTTP 已连",
        TunnelState.Error => "错误",
        _ => "已停止"
    };

    public string DurationText => ConnectedSince.HasValue
        ? (DateTime.Now - ConnectedSince.Value).ToString(@"hh\:mm\:ss")
        : "-";

    // ── 周期性健康检查的展示 ────────────────────────────────────────────
    //
    // 健康状态与隧道状态是两码事，不能合并到 StatusText 里：
    // 「监听中」只说明本地端口开着，而健康检查说明的是整条链路（网络→认证→MySQL）
    // 此刻是否真的能用。服务端宕机时前者依旧是「监听中」，后者才会变红。
    // 所以这里是独立的一组属性，在状态列下方单独占一行。

    /// <summary>最近一次周期性检查是否通过。<c>null</c> = 从未检查过。</summary>
    public bool? Healthy
    {
        get => _healthy;
        private set
        {
            _healthy = value;
            OnChanged();
            OnChanged(nameof(HealthVisibility));
            OnChanged(nameof(HealthIcon));
            OnChanged(nameof(HealthBrush));
        }
    }

    /// <summary>最近一次检查完成的本地时刻。</summary>
    public DateTime? HealthCheckedAt
    {
        get => _healthCheckedAt;
        private set { _healthCheckedAt = value; OnChanged(); OnChanged(nameof(HealthText)); }
    }

    /// <summary>最近一次检查的一行结论，作为 ToolTip 显示。</summary>
    public string? HealthSummary
    {
        get => _healthSummary;
        private set { _healthSummary = value; OnChanged(); }
    }

    /// <summary>
    /// 从未检查过时整行隐藏。
    /// <para>没开周期性检查的项目不应该在界面上留一块灰色占位 ——
    /// 那会让人以为「检查过但结果未知」，而事实是根本没开这个功能。</para>
    /// </summary>
    public Visibility HealthVisibility =>
        Healthy.HasValue ? Visibility.Visible : Visibility.Collapsed;

    public string HealthIcon => Healthy == true ? "✓" : "✕";

    public Brush HealthBrush => Healthy == true ? HealthOkBrush : HealthBadBrush;

    /// <summary>
    /// 形如「链路正常 21:05」。带上时刻是必要的 ——
    /// 一个孤零零的绿勾无法回答「这是刚才测的还是半小时前的」。
    /// </summary>
    public string HealthText
    {
        get
        {
            if (!HealthCheckedAt.HasValue) return "";
            var t = HealthCheckedAt.Value.ToString("HH:mm");
            return Healthy == true ? $"链路正常 {t}" : $"链路异常 {t}";
        }
    }

    private static readonly Brush HealthOkBrush = FreezeBrush("#3ddc97");
    private static readonly Brush HealthBadBrush = FreezeBrush("#ff5d6c");

    private static Brush FreezeBrush(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    /// <summary>
    /// 应用一次周期性健康检查的结果。由 <c>AppController</c> 在 UI 线程调用。
    ///
    /// <para>这里<b>只更新健康相关的属性</b>，绝不去碰 <see cref="State"/>、
    /// <see cref="Errors"/> 等隧道自身的字段。探活走的是一条与业务隧道完全独立的连接，
    /// 它失败不等于隧道断了（可能只是服务端连接数暂时打满），
    /// 让它去改隧道状态会制造出与真实数据流相矛盾的显示。</para>
    /// </summary>
    public void ApplyHealth(HealthReport report)
    {
        Healthy = report.IsHealthy;
        HealthCheckedAt = report.StartedAt; // 已是本地时间，见 HealthReport.StartedAt 注释
        HealthSummary = report.OneLine();
    }

    /// <summary>由每秒定时器调用，仅刷新「运行时长」列而不触发其它属性。</summary>
    public void RaiseDurationChanged() => OnChanged(nameof(DurationText));

    public ProjectVm(TunnelConfig c)
    {
        Name = c.Name;
        DisplayName = c.DisplayName;
        LocalPort = c.LocalPort;
        ServerUrl = c.ServerUrl;
        ListenAddress = c.ListenAddress;
        Enabled = c.Enabled;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? p = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}
