using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Cryptunnel.Core.Config;
using Cryptunnel.Core.Crypto;
using Cryptunnel.Core.Logging;
using Cryptunnel.Core.Models;
using Cryptunnel.Core.Stats;
using Cryptunnel.Core.Tunnel;
using Cryptunnel.App.Models;
using Microsoft.VisualBasic.FileIO;

namespace Cryptunnel.App;

public sealed class AppController
{
    private readonly Dispatcher _dispatcher;
    private readonly FileLogger _logger;
    private readonly StatsCollector _stats = new();
    private readonly ITunnelTap _tap = new NullTap();
    private TunnelManager? _manager;
    private readonly HealthScheduler _health;
    private Dictionary<string, TunnelConfig> _lastConfigs = new();
    private readonly string _configDir;
    private readonly string _logDir;
    private readonly string _dataDir;

    public ObservableCollection<ProjectVm> Projects { get; } = new();
    public ObservableCollection<LogLine> Logs { get; } = new();
    public bool IsPortable { get; }
    public string ModeText => IsPortable ? "便携" : "安装";
    public string ConfigDir => _configDir;
    public string LogDir => _logDir;
    public event Action<string>? FatalError;

    /// <summary>
    /// 重载/启动后回报配置结果（有效项目数、错误数）。
    /// 用来杜绝「重载提示成功但列表被静默清空」这类假成功：
    /// 当有效项目为 0 或存在解析错误时，UI 必须给出明确提示，而不是只留一个空列表。
    /// </summary>
    public event Action<int, int>? ConfigReloaded;

    public string VersionText
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            return v == null ? "v1.1.0" : $"v{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    // 开机启动：状态来自注册表，UI（工具栏复选框 + 托盘菜单）双向同步
    public bool AutoStartEnabled => AutoStart.IsEnabled;
    public event Action<bool>? AutoStartChanged;

    public void SetAutoStart(bool on)
    {
        try
        {
            if (on) AutoStart.Enable();
            else AutoStart.Disable();
            AutoStartChanged?.Invoke(on);
        }
        catch (Exception ex)
        {
            LogFatal($"设置开机启动失败：{ex.Message}");
        }
    }

    public void ToggleAutoStart() => SetAutoStart(!AutoStartEnabled);

    public AppController()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        var exeDir = AppContext.BaseDirectory;
        var mode = PathResolver.Resolve(exeDir);
        IsPortable = mode == PathResolver.Mode.Portable;
        _configDir = PathResolver.ConfigDir(exeDir, mode);
        _logDir = PathResolver.LogDir(exeDir, mode);
        _dataDir = PathResolver.DataDir(exeDir, mode);
        Directory.CreateDirectory(_configDir);
        _logger = new FileLogger(_logDir, LogLevel.Info, 10, 7);
        _logger.OnLog += OnLog;

        // 周期性健康检查。只有显式配置了 health.enabled 的项目会被排进来，
        // 未配置的项目零开销（调度表为空时计时器根本不启动）。
        _health = new HealthScheduler(_logger);
        _health.Checked += (name, report) => _dispatcher.Invoke(() =>
        {
            var vm = Find(name);
            if (vm != null) vm.ApplyHealth(report);
        });

        var timer = new DispatcherTimer(TimeSpan.FromSeconds(1.5), DispatcherPriority.Normal, (_, _) => RefreshStats(), _dispatcher);
        timer.Start();

        // 运行时长每秒顺滑刷新（不跟 1.5s 统计周期跳变）
        var durTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Normal,
            (_, _) => NotifyDurations(), _dispatcher);
        durTimer.Start();
    }

    public void Start()
    {
        _logger.Log(LogLevel.Info, null, $"启动模式：{(IsPortable ? "便携" : "安装")}，配置目录 {_configDir}");
        var (configs, errors) = ConfigLoader.Load(_configDir, _logger);
        foreach (var e in errors)
            _logger.Log(LogLevel.Error, e.File, e.Message);
        _lastConfigs = configs.ToDictionary(c => c.Name);

        _manager = new TunnelManager(_logger, _tap, _stats);
        _manager.StateChanged += (name, state) => _dispatcher.Invoke(() =>
        {
            var vm = Find(name);
            if (vm != null)
            {
                vm.State = state;
                vm.Running = state is TunnelState.Listening or TunnelState.WsConnecting or TunnelState.WsConnected or TunnelState.HttpConnecting or TunnelState.HttpConnected;
                if (!vm.Running) vm.Transport = "-";
            }
        });
        _manager.FatalError += name => { _logger.Log(LogLevel.Error, name, "该隧道因致命错误停止（如端口被占用）"); FatalError?.Invoke(name); };

        _dispatcher.Invoke(() =>
        {
            Projects.Clear();
            foreach (var c in configs)
            {
                var vm = new ProjectVm(c);
                if (c.Enabled) { vm.State = TunnelState.Listening; vm.Running = true; } // 预期会监听
                Projects.Add(vm);
            }
        });
        _manager.StartAll(configs);
        _health.Configure(configs);
        ConfigReloaded?.Invoke(configs.Count, errors.Count);
    }

    /// <summary>
    /// 增量重载：对比新旧配置，只处理变化的部分，避免编辑一个项目时把其他项目的隧道全打断。
    /// - 已删除的项目：停隧道 + 移除 VM
    /// - 新增的项目：建 VM + 起隧道
    /// - 改动的项目（地址/密钥/端口/启用等）：重建隧道
    /// - 未变的项目：保持连接
    /// </summary>
    public void Reload()
    {
        var (configs, errors) = ConfigLoader.Load(_configDir, _logger);
        foreach (var e in errors)
            _logger.Log(LogLevel.Error, e.File, e.Message);
        var newConfigs = configs.ToDictionary(c => c.Name);

        if (_manager == null)
        {
            _lastConfigs = newConfigs;
            Start();
            return;
        }

        var newNames = new HashSet<string>(newConfigs.Keys);

        // 1) 删除已不存在的项目
        foreach (var name in _manager.Tunnels.Keys.ToList())
            if (!newNames.Contains(name)) { StopOne(name); RemoveVm(name); }

        // 2) 新增 / 改动
        foreach (var c in configs)
        {
            var vm = Find(c.Name);
            if (vm != null && !string.Equals(vm.DisplayName, c.DisplayName, StringComparison.Ordinal))
                vm.DisplayName = c.DisplayName;

            if (_manager.Tunnels.ContainsKey(c.Name))
            {
                var prev = _lastConfigs.TryGetValue(c.Name, out var p) ? p : null;
                if (prev != null && ConfigChanged(prev, c))
                {
                    StopOne(c.Name);
                    StartOne(c);
                }
                // 未变：保持运行，不动
            }
            else
            {
                StartOne(c);
            }
        }

        _lastConfigs = newConfigs;

        // 调度表按新配置整体重建：这一步不能漏。漏了的后果是
        // 用户在编辑器里关掉了 health.enabled，后台却继续按老间隔探活，
        // 而界面上没有任何迹象说明它还在跑。
        _health.Configure(configs);

        // 回报结果：0 个项目或存在错误时，UI 会弹出明确提示，
        // 避免「重载看似成功、列表却被静默清空」的假成功。
        ConfigReloaded?.Invoke(configs.Count, errors.Count);
    }

    private static bool ConfigChanged(TunnelConfig a, TunnelConfig b)
    {
        return !string.Equals(a.ServerUrl, b.ServerUrl, StringComparison.Ordinal)
            || !string.Equals(a.WsPath, b.WsPath, StringComparison.Ordinal)
            || !string.Equals(a.AesKey, b.AesKey, StringComparison.Ordinal)
            || !string.Equals(a.AuthKey, b.AuthKey, StringComparison.Ordinal)
            || !string.Equals(a.ListenAddress, b.ListenAddress, StringComparison.Ordinal)
            || a.LocalPort != b.LocalPort
            || a.Enabled != b.Enabled;
    }

    private void EnsureVm(TunnelConfig c)
    {
        if (Find(c.Name) != null) return;
        _dispatcher.Invoke(() =>
        {
            var vm = new ProjectVm(c);
            if (c.Enabled) { vm.State = TunnelState.Listening; vm.Running = true; }
            Projects.Add(vm);
        });
    }

    private void RemoveVm(string name)
    {
        _dispatcher.Invoke(() =>
        {
            var vm = Find(name);
            if (vm != null) Projects.Remove(vm);
        });
        _lastConfigs.Remove(name);
    }

    public void ClearLogs() => _dispatcher.Invoke(() =>
    {
        Logs.Clear();
        _logger.Log(LogLevel.Info, null, "已清空实时日志");
    });

    private void NotifyDurations()
    {
        foreach (var vm in Projects)
            if (vm.ConnectedSince.HasValue) vm.RaiseDurationChanged();
    }

    public void StopOne(string name)
    {
        _manager?.StopOne(name);
        var vm = Find(name);
        if (vm != null) { vm.Running = false; vm.State = TunnelState.Stopped; vm.Transport = "-"; }
    }

    public void StartOne(TunnelConfig cfg)
    {
        EnsureVm(cfg);
        if (cfg.Enabled) _manager?.StartOne(cfg);
        var vm = Find(cfg.Name);
        if (vm != null) { vm.State = TunnelState.Listening; vm.Running = true; vm.Transport = "-"; }
    }

    public void ToggleProject(string name)
    {
        if (_manager == null) return;
        if (_manager.Tunnels.ContainsKey(name))
            StopOne(name);
        else if (_lastConfigs.TryGetValue(name, out var c))
            StartOne(c);
    }

    /// <summary>拖拽 / --import 调用：把单个 yaml 复制到配置目录后重载。</summary>
    public void ImportFile(string sourcePath)
    {
        try
        {
            var stem = Path.GetFileNameWithoutExtension(sourcePath);
            var safe = string.Concat(stem.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_')) is var s && s.Length > 0 ? s : "imported";
            var dest = Path.Combine(_configDir, $"{safe}.yaml");
            if (File.Exists(dest)) dest = Path.Combine(_configDir, $"{safe}-{DateTime.Now:yyyyMMddHHmmss}.yaml");
            File.Copy(sourcePath, dest, true);
            _logger.Log(LogLevel.Info, null, $"已导入配置：{dest}，即将重载");
        }
        catch (Exception ex)
        {
            _logger.Log(LogLevel.Error, null, $"导入配置失败：{ex.Message}");
        }
        Reload();
    }

    /// <summary>新建项目配置。返回错误消息，成功返回 null。</summary>
    public string? TryCreate(ProjectFile pf)
    {
        var path = Path.Combine(_configDir, (pf.Name ?? "") + ".yaml");
        if (File.Exists(path)) return $"项目 {pf.Name} 已存在：{Path.GetFileName(path)}";
        var err = WriteProjectYaml(pf);
        if (err != null) return err;
        _logger.Log(LogLevel.Info, pf.Name, $"新建项目配置：{Path.GetFileName(path)}");
        Reload();
        return null;
    }

    /// <summary>更新项目配置（项目名称不可改）。返回错误消息，成功返回 null。</summary>
    public string? TryUpdate(ProjectFile pf)
    {
        var path = Path.Combine(_configDir, (pf.Name ?? "") + ".yaml");
        if (!File.Exists(path)) return $"配置文件不存在：{path}";
        var err = WriteProjectYaml(pf);
        if (err != null) return err;
        _logger.Log(LogLevel.Info, pf.Name, $"更新项目配置：{Path.GetFileName(path)}");
        Reload();
        return null;
    }

    /// <summary>删除项目配置（移到回收站）。返回错误消息，成功返回 null。</summary>
    public string? TryDelete(string name)
    {
        var path = Path.Combine(_configDir, name + ".yaml");
        if (!File.Exists(path)) return $"配置文件不存在：{path}";
        try
        {
            FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
        }
        catch (Exception ex)
        {
            return $"删除失败：{ex.Message}";
        }
        _logger.Log(LogLevel.Info, name, $"项目配置已移到回收站：{path}");
        Reload();
        return null;
    }

    /// <summary>读取项目原始配置（用于编辑回填，包含 aesKey/authKey）。</summary>
    public ProjectFile? TryReadProjectFile(string name)
    {
        var path = Path.Combine(_configDir, name + ".yaml");
        var (pf, err) = ConfigLoader.TryReadFile(path);
        if (err != null)
        {
            _logger.Log(LogLevel.Warn, name, $"读取项目配置失败：{err}");
            return null;
        }
        return pf;
    }

    private string? WriteProjectYaml(ProjectFile pf)
    {
        var path = Path.Combine(_configDir, (pf.Name ?? "") + ".yaml");
        try
        {
            Directory.CreateDirectory(_configDir);
            File.WriteAllText(path, BuildProjectYaml(pf));
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private static string BuildProjectYaml(ProjectFile pf)
    {
        var sb = new StringBuilder();
        sb.AppendLine("schemaVersion: 1");
        sb.AppendLine();
        sb.AppendLine($"name: {pf.Name}");
        if (!string.IsNullOrWhiteSpace(pf.DisplayName))
            sb.AppendLine($"displayName: {YamlStr(pf.DisplayName)}");
        sb.AppendLine($"enabled: {(pf.Enabled ?? true ? "true" : "false")}");
        sb.AppendLine();
        sb.AppendLine($"serverUrl: {YamlStr(pf.ServerUrl)}");
        const string defaultWsPath = "/ws-cryptunnel";
        if (!string.IsNullOrWhiteSpace(pf.WsPath) && pf.WsPath != defaultWsPath)
            sb.AppendLine($"wsPath: {YamlStr(pf.WsPath)}");
        sb.AppendLine($"aesKey: {YamlStr(pf.AesKey)}");
        sb.AppendLine($"authKey: {YamlStr(pf.AuthKey)}");

        // 只在非默认时写这一行。
        //
        // 老配置文件里没有 cipher 键，若无条件写回，用户只是改个端口号也会让文件多出一行 —
        // 对于会 diff 配置、或把 config.d 纳入版本管理的用户，这是凭空的噪声。
        // 省略与显式写默认值在语义上等价（ConfigLoader 的缺省链会回落到同一个算法）。
        var cipherId = CipherRegistry.Normalize(pf.Cipher);
        if (cipherId != CipherRegistry.Default.Id)
            sb.AppendLine($"cipher: {cipherId}");

        sb.AppendLine();
        sb.AppendLine("local:");
        sb.AppendLine($"  port: {pf.Local?.Port}");
        if (!string.IsNullOrWhiteSpace(pf.Local?.Address))
            sb.AppendLine($"  address: {pf.Local.Address}");

        // 同 cipher：只在开启时写。默认 false，省略与显式写 false 等价。
        //
        // 这一项不能因为「地址是非回环」就自动补上 —— 那等于让程序替用户做风险承诺。
        // 它只反映用户在编辑器里勾选的结果。
        if (pf.Local?.AllowNonLoopback == true)
            sb.AppendLine("  allowNonLoopback: true");

        // health 段必须原样保留。
        //
        // 编辑器界面上没有这一项，但它可能已经存在于用户手写的 yaml 里 ——
        // 回写时不写就等于「改了个端口号，顺手把周期性健康检查关掉了」，
        // 而且不会有任何提示。配置回写吞掉界面未覆盖的字段是最隐蔽的一类数据丢失。
        if (pf.Health is { } health && (health.Enabled.HasValue || health.IntervalSec.HasValue))
        {
            sb.AppendLine();
            sb.AppendLine("health:");
            if (health.Enabled.HasValue)
                sb.AppendLine($"  enabled: {(health.Enabled.Value ? "true" : "false")}");
            if (health.IntervalSec.HasValue)
                sb.AppendLine($"  intervalSec: {health.IntervalSec.Value}");
        }

        return sb.ToString();
    }

    private static string YamlStr(string? s)
    {
        if (s == null) return "\"\"";
        return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    /// <summary>
    /// 取项目的完整运行配置（含密钥），供健康检查使用。
    ///
    /// <para>界面上的 <see cref="ProjectVm"/> 只有展示字段，没有 aesKey / authKey / cipher ——
    /// 而探活要走真实认证链路，这三项缺一不可。</para>
    /// </summary>
    public TunnelConfig? GetConfig(string name) =>
        _lastConfigs.TryGetValue(name, out var c) ? c : null;

    /// <summary>
    /// 健康检查（探活）。
    ///
    /// <para><b>此前这里是一个假指示灯</b>：老实现只用 <c>TcpClient</c> 连本进程自己的
    /// 监听端口，判定等价于界面上那三个字「监听中」。authKey 配错、cipher 配错、
    /// 服务端宕机、MySQL 挂掉 —— 这四种真实故障它<b>全部报成功</b>。</para>
    ///
    /// <para>它还有副作用：连自己的监听端口会真的触发一次隧道建连
    /// （<c>AcceptLoop</c> → <c>TryWebSocketMode</c>），服务端因此多开一条 MySQL 连接，
    /// Connections 统计 +1，日志里留下「DBeaver 已连接」和「写入握手包失败」两条
    /// 与事实不符的记录 —— 而真正探到的结论全被丢弃了。</para>
    ///
    /// <para>现在改为走 <see cref="HealthProbe"/> 的真实链路，并把分层结论呈现出来。</para>
    /// </summary>
    /// <returns>找不到配置时返回 <c>null</c>，调用方据此提示用户先重载配置。</returns>
    public TunnelConfig? PrepareHealthCheck(string name)
    {
        var cfg = GetConfig(name);
        if (cfg == null)
            _logger.Log(LogLevel.Warn, name, "健康检查：找不到该项目的配置，请先重载配置。");
        return cfg;
    }

    /// <summary>把健康检查的一行结论写入日志（供弹窗回调）。</summary>
    public void LogHealth(string name, string line) =>
        _logger.Log(LogLevel.Info, name, line);

    public void OpenConfigDir() => TryOpen(_configDir);
    public void OpenLogDir() => TryOpen(_logDir);

    private void TryOpen(string dir)
    {
        try { Directory.CreateDirectory(dir); System.Diagnostics.Process.Start("explorer.exe", dir); }
        catch (Exception ex) { _logger.Log(LogLevel.Warn, null, $"打开目录失败：{ex.Message}"); }
    }

    private void RefreshStats()
    {
        var snap = _stats.Snapshot();
        foreach (var vm in Projects)
            if (snap.TryGetValue(vm.Name, out var s))
            {
                vm.BytesIn = s.BytesIn;
                vm.BytesOut = s.BytesOut;
                vm.Errors = s.Errors;
                vm.Warnings = s.Warnings;
                vm.Connections = s.Connections;
                vm.Reconnects = s.Reconnects;
                vm.ActiveConnections = s.ActiveConnections;
                vm.ConnectedSince = s.ConnectedSince;
                vm.LastError = s.LastError;
                vm.Transport = s.ActiveConnections > 0 ? (s.Transport ?? "-") : "-";
            }
    }

    private ProjectVm? Find(string name)
    {
        foreach (var p in Projects) if (p.Name == name) return p;
        return null;
    }

    private void OnLog(LogEntry e)
    {
        _dispatcher.Invoke(() =>
        {
            Logs.Add(new LogLine(e));
            while (Logs.Count > 1000) Logs.RemoveAt(0);
        });
    }

    /// <summary>
    /// 退出时的收尾。
    /// <para>先停调度器再停隧道：反过来的话，最后一拍可能刚好排出一次探活，
    /// 而此时隧道已停，用户会在退出瞬间看到一条毫无意义的「健康检查失败」。</para>
    /// </summary>
    public void Stop()
    {
        _health.Dispose();
        _manager?.StopAll();
    }

    /// <summary>记录致命异常（供 App 全局异常处理调用，避免崩溃无声无息）。</summary>
    public void LogFatal(string message) => _logger.Log(LogLevel.Error, null, message);
}
