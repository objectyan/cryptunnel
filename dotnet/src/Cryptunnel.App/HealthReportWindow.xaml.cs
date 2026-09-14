using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Cryptunnel.App.Models;
using Cryptunnel.Core.Health;
using Cryptunnel.Core.Models;
using Cryptunnel.Core.Tunnel;

namespace Cryptunnel.App;

/// <summary>
/// 健康检查的分层报告窗口。
///
/// <para>窗口自己负责发起探活（而不是由调用方先跑完再把结果传进来），
/// 这样「重新检查」按钮才能复用同一条路径 —— 否则重测逻辑会在
/// AppController 和本窗口各写一份。</para>
/// </summary>
public partial class HealthReportWindow : Window, INotifyPropertyChanged
{
    private static readonly Brush PassBrush = Freeze("#3ddc97");
    private static readonly Brush FailBrush = Freeze("#ff5d6c");
    private static readonly Brush BusyBrush = Freeze("#4c8dff");

    private readonly TunnelConfig _cfg;
    private readonly Action<string>? _log;

    /// <summary>关窗时取消在途探活，避免窗口已销毁还在往 UI 线程回调。</summary>
    private readonly CancellationTokenSource _cts = new();

    private bool _busy;

    public ObservableCollection<HealthStageVm> Stages { get; } = new();

    public string DisplayName { get; }
    public string CipherId { get; private set; } = "";

    private string _verdictText = "正在检查…";
    public string VerdictText { get => _verdictText; private set { _verdictText = value; OnChanged(); } }

    private string _verdictIcon = "…";
    public string VerdictIcon { get => _verdictIcon; private set { _verdictIcon = value; OnChanged(); } }

    private Brush _verdictBrush = BusyBrush;
    public Brush VerdictBrush { get => _verdictBrush; private set { _verdictBrush = value; OnChanged(); } }

    private string _totalText = "—";
    public string TotalText { get => _totalText; private set { _totalText = value; OnChanged(); } }

    private string _versionText = "";
    public string VersionText { get => _versionText; private set { _versionText = value; OnChanged(); } }

    /// <summary>
    /// 成本提示常驻显示。
    ///
    /// <para>每次检查都会让服务端真实建立并断开一条数据库连接。用户有权在点第二次
    /// 「重新检查」之前就知道这件事 —— 尤其在服务端 maxConnections 吃紧时，
    /// 反复点这个按钮本身就会变成故障的一部分。</para>
    /// </summary>
    public string CostHint =>
        "每次检查会在服务端真实建立并断开一条数据库连接。";

    private HealthReport? _last;

    public HealthReportWindow(TunnelConfig cfg, Action<string>? log = null)
    {
        _cfg = cfg;
        _log = log;
        DisplayName = string.IsNullOrWhiteSpace(cfg.DisplayName) ? cfg.Name : cfg.DisplayName;
        CipherId = cfg.Cipher;
        InitializeComponent();
        DataContext = this;
        Loaded += async (_, _) => await RunAsync();
        Closed += (_, _) => { try { _cts.Cancel(); } catch { } _cts.Dispose(); };
    }

    private async Task RunAsync()
    {
        if (_busy) return;
        _busy = true;
        SetBusyUi();

        HealthReport report;
        try
        {
            report = await HealthProbe.RunAsync(_cfg, _cts.Token);
        }
        catch (Exception ex)
        {
            // HealthProbe 契约上不抛异常。真抛了说明是它自己的缺陷 ——
            // 此时静默吞掉会让窗口永远停在「正在检查…」，那比报错更难排查。
            _busy = false;
            VerdictText = $"健康检查自身出错：{ex.Message}";
            VerdictIcon = "!";
            VerdictBrush = FailBrush;
            EnableButtons(true);
            return;
        }

        if (_cts.IsCancellationRequested) return; // 窗口已关闭，不再动 UI

        _last = report;
        Render(report);
        _log?.Invoke(report.OneLine());
        _busy = false;
        EnableButtons(true);
    }

    private void SetBusyUi()
    {
        Stages.Clear();
        VerdictText = "正在检查…";
        VerdictIcon = "…";
        VerdictBrush = BusyBrush;
        TotalText = "—";
        VersionText = "";
        EnableButtons(false);
    }

    private void Render(HealthReport r)
    {
        Stages.Clear();
        foreach (var s in r.Stages) Stages.Add(new HealthStageVm(s));

        CipherId = r.CipherId;
        OnChanged(nameof(CipherId));

        TotalText = $"{r.TotalMs} ms";
        VersionText = string.IsNullOrEmpty(r.ServerVersion) ? "" : $"    MySQL {r.ServerVersion}";

        if (r.IsHealthy)
        {
            VerdictIcon = "✓";
            VerdictBrush = PassBrush;
            VerdictText = "隧道健康，可以正常使用";
        }
        else
        {
            VerdictIcon = "✕";
            VerdictBrush = FailBrush;
            var f = r.FirstFailure;
            VerdictText = f.HasValue
                ? $"检查未通过：{HealthReport.StageName(f.Value.Stage)}"
                : "检查未通过";
        }
    }

    private void EnableButtons(bool on)
    {
        if (BtnRetest != null) BtnRetest.IsEnabled = on;
        if (BtnCopy != null) BtnCopy.IsEnabled = on;
    }

    private async void BtnRetest_Click(object sender, RoutedEventArgs e) => await RunAsync();

    /// <summary>
    /// 把报告复制成纯文本。
    ///
    /// <para>存在的理由很实际：这个窗口里的内容正是用户要发给服务端管理员或 DBA 的东西。
    /// 没有这个按钮，用户只能截图 —— 而截图里的错误码无法被搜索和粘贴。</para>
    /// </summary>
    private void BtnCopy_Click(object sender, RoutedEventArgs e)
    {
        if (_last == null) return;
        var sb = new StringBuilder();
        sb.AppendLine($"Cryptunnel 健康检查报告");
        sb.AppendLine($"项目：{_last.DisplayName}（{_last.ProjectName}）");
        sb.AppendLine($"时间：{_last.StartedAt:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"算法：{_last.CipherId}");
        sb.AppendLine($"结论：{_last.OneLine()}");
        sb.AppendLine($"总耗时：{_last.TotalMs} ms");
        if (!string.IsNullOrEmpty(_last.ServerVersion))
            sb.AppendLine($"MySQL 版本：{_last.ServerVersion}");
        sb.AppendLine();

        foreach (var s in _last.Stages)
        {
            var mark = s.Status switch
            {
                HealthStatus.Pass => "[通过]",
                HealthStatus.Fail => "[失败]",
                _ => "[未执行]"
            };
            var cost = s.Status == HealthStatus.Skipped ? "-" : $"{s.ElapsedMs}ms";
            sb.AppendLine($"{mark} {HealthReport.StageName(s.Stage)}（{cost}）：{s.Summary}");
            if (!string.IsNullOrEmpty(s.Advice))
                sb.AppendLine($"       建议：{s.Advice}");
        }

        try
        {
            Clipboard.SetText(sb.ToString());
            BtnCopy.Content = "已复制";
        }
        catch
        {
            // 剪贴板可能被其它进程占用。复制失败不该弹错误框打断诊断流程。
            BtnCopy.Content = "复制失败";
        }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.OriginalSource is Button) return;
        if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed) DragMove();
    }

    private static Brush Freeze(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? p = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}
