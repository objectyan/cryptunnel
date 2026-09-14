using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using Cryptunnel.App.Converters;
using Cryptunnel.App.Models;
using Cryptunnel.App.Tray;
using Cryptunnel.Core.Logging;
using Cryptunnel.Core.Models;
using Cryptunnel.Core.Tunnel;

namespace Cryptunnel.App;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private AppController Controller => ((App)Application.Current).Controller;

    // 实时日志视图：筛选 + 自动滚动
    private ICollectionView _logView = null!;
    private bool _autoScroll = true;
    private Regex? _searchRe;
    private const string AllProjectsTag = "__all__";

    // 顶部健康总览：订阅各项目状态变化以实时刷新
    private readonly List<ProjectVm> _wired = new();
    private readonly BytesConverter _bytesConv = new();

    public MainWindow()
    {
        InitializeComponent();
        // 任务栏/标题栏图标：与托盘「J」保持一致（默认绿色=正常态）
        Icon = AppTray.BuildWindowIconImageSource();
        DataContext = Controller;
        // 重载/启动结果回报：0 个项目或解析错误时给出明确提示，避免静默空列表（假成功）。
        Controller.ConfigReloaded += OnConfigReloaded;
        // 开机启动开关：回填当前注册表状态，并订阅变化以双向同步（托盘菜单也会触发）
        ChkAutoStart.IsChecked = Controller.AutoStartEnabled;
        Controller.AutoStartChanged += v => ChkAutoStart.IsChecked = v;

        // 日志筛选视图
        _logView = CollectionViewSource.GetDefaultView(Controller.Logs);
        _logView.Filter = LogFilter;
        LogBox.ItemsSource = _logView;
        ApplyLogFilter(); // 应用初始筛选
        Controller.Logs.CollectionChanged += OnLogsChanged;
        FillProjectCombo();
        ScrollLogToEnd(); // 初始滚动到底

        // 级别下拉默认「全部」（不再在 XAML 设 SelectedIndex，避免初始化期触发筛选崩溃）
        CmbLevel.SelectedIndex = 0;
        // 自动滚动开关默认开（与 _autoScroll 字段一致）
        ChkAutoScroll.IsChecked = true;

        // 顶部健康总览：集合变化或任一项目属性变化即刷新
        Controller.Projects.CollectionChanged += (_, _) => WireOverview();
        WireOverview();

        // 无边框窗口：最大化限制到工作区（不挡任务栏）+ 最小化尺寸由 XAML MinWidth/MinHeight 控制
        SourceInitialized += OnSourceInitialized;

        // 主操作快捷键：Ctrl+N 新建 / Ctrl+R 重载 / Ctrl+L 定位日志
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            switch (e.Key)
            {
                case Key.N: e.Handled = true; BtnNewProject_Click(this, new RoutedEventArgs()); break;
                case Key.R: e.Handled = true; BtnReload_Click(this, new RoutedEventArgs()); break;
                case Key.L: e.Handled = true; BtnRailLog_Click(this, new RoutedEventArgs()); break;
            }
        }
    }

    // ---- 无边框窗口最大化不遮挡任务栏（WM_GETMINMAXINFO） ----
    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var source = HwndSource.FromHwnd(hwnd);
        if (source != null) source.AddHook(WndProc);
    }

    private const int WM_GETMINMAXINFO = 0x0024;
    private const int MONITOR_DEFAULTTONEAREST = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor, rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_GETMINMAXINFO)
        {
            var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
            var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (monitor != IntPtr.Zero)
            {
                var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (GetMonitorInfo(monitor, ref mi))
                {
                    // rcWork 已排除任务栏：把最大化位置/尺寸限制到工作区
                    mmi.ptMaxPosition.x = mi.rcWork.Left;
                    mmi.ptMaxPosition.y = mi.rcWork.Top;
                    mmi.ptMaxSize.x = mi.rcWork.Right - mi.rcWork.Left;
                    mmi.ptMaxSize.y = mi.rcWork.Bottom - mi.rcWork.Top;
                }
            }
            Marshal.StructureToPtr(mmi, lParam, true);
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void ChkAutoStart_Changed(object sender, RoutedEventArgs e)
    {
        if (ChkAutoStart.IsChecked == null) return;
        Controller.SetAutoStart(ChkAutoStart.IsChecked == true);
    }

    private void BtnReload_Click(object sender, RoutedEventArgs e) => Controller.Reload();

    /// <summary>
    /// 重载/启动结果回报。仅在「出问题」时打扰用户：
    /// - 0 个有效项目：列表会被清空，必须明确告知原因（yaml 格式/字段问题）。
    /// - 有解析错误：提示去查日志。
    /// 正常情况（≥1 项目且 0 错误）不打断，列表本身的刷新即是最好的反馈。
    /// </summary>
    private void OnConfigReloaded(int valid, int errors)
    {
        if (valid == 0)
        {
            MessageBox.Show(
                "配置目录中没有有效的项目（0 个），列表已清空。\n\n" +
                "常见原因：\n" +
                "  • yaml 格式错误（缩进 / 缺少 name / 端口写成了顶层 localPort）\n" +
                "  • 字段名拼写错误（如 serverUrl 写成了 server）\n" +
                "请检查 Cryptunnel\\config.d 下的文件，修正后点「↻ 重载配置」。",
                "重载配置：无有效项目", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        else if (errors > 0)
        {
            MessageBox.Show(
                $"已加载 {valid} 个项目，但有 {errors} 个配置文件存在错误（详见日志）。\n" +
                "列表仅显示有效的项目。",
                "重载配置：存在错误", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BtnOpenConfig_Click(object sender, RoutedEventArgs e) => Controller.OpenConfigDir();

    private void BtnOpenLog_Click(object sender, RoutedEventArgs e) => Controller.OpenLogDir();

    // 真正退出（关窗口是缩到托盘，见 App.OnStartup 的 Closing 处理）
    private void BtnExit_Click(object sender, RoutedEventArgs e) => ((App)Application.Current).RequestExit();

    // ---- 窗口控制（自定义标题栏 / 无边框） ----
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 点到标题栏上的按钮时不拖动
        if (e.OriginalSource is Button) return;
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
    private void BtnMin_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void BtnMax_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close(); // 经 App.Closing → 隐藏到托盘
    private void BtnRailLog_Click(object sender, RoutedEventArgs e) => ScrollLogToEnd();

    // ---- 顶部健康总览：随集合与各项状态刷新 ----
    private void WireOverview()
    {
        foreach (var vm in _wired) vm.PropertyChanged -= OnVmChanged;
        _wired.Clear();
        foreach (var vm in Controller.Projects)
        {
            vm.PropertyChanged += OnVmChanged;
            _wired.Add(vm);
        }
        RefreshOverview();
    }
    private void OnVmChanged(object? sender, PropertyChangedEventArgs e) => RefreshOverview();
    private void RefreshOverview()
    {
        if (OvTotal == null) return;
        var projects = Controller.Projects;
        OvTotal.Text = projects.Count.ToString();
        OvConnected.Text = projects.Count(p => p.Running).ToString();
        if (OvTotalSub != null) OvTotalSub.Text = $"运行中 {projects.Count(p => p.Running)}";
        if (OvConnSub != null) OvConnSub.Text = $"共 {projects.Count} 项";
        OvError.Text = projects.Count(p => p.State == TunnelState.Error || p.Errors > 0).ToString();
        long inB = 0, outB = 0;
        foreach (var p in projects) { inB += p.BytesIn; outB += p.BytesOut; }
        OvTraffic.Text = $"↓ {_bytesConv.Convert(inB, typeof(string), null!, CultureInfo.CurrentCulture)}   ↑ {_bytesConv.Convert(outB, typeof(string), null!, CultureInfo.CurrentCulture)}";
    }

    private void BtnToggle_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ProjectVm vm)
            Controller.ToggleProject(vm.Name);
    }

    private void BtnShowErrors_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ProjectVm vm)
            new ErrorDetailWindow(vm.Name, Controller.Logs, Controller.LogDir, LogLevel.Error) { Owner = this }.ShowDialog();
    }

    private void BtnShowWarnings_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ProjectVm vm)
            new ErrorDetailWindow(vm.Name, Controller.Logs, Controller.LogDir, LogLevel.Warn) { Owner = this }.ShowDialog();
    }

    // ---- 实时日志筛选 / 自动滚动 / 清空 ----

    private void FillProjectCombo()
    {
        var selName = (CmbProject.SelectedItem as ComboBoxItem)?.Tag as string;
        CmbProject.Items.Clear();
        CmbProject.Items.Add(new ComboBoxItem { Content = "（全部项目）", Tag = AllProjectsTag });
        foreach (var p in Controller.Projects.OrderBy(x => x.DisplayName))
            CmbProject.Items.Add(new ComboBoxItem { Content = p.DisplayName, Tag = p.Name });
        CmbProject.SelectedIndex = 0;
        if (selName != null && selName != AllProjectsTag)
        {
            for (var i = 1; i < CmbProject.Items.Count; i++)
            {
                if ((CmbProject.Items[i] as ComboBoxItem)?.Tag as string == selName)
                {
                    CmbProject.SelectedIndex = i;
                    break;
                }
            }
        }
    }

    private void CmbProject_DropDownOpened(object sender, EventArgs e) => FillProjectCombo();

    private void LogFilter_Changed(object sender, SelectionChangedEventArgs e) => ApplyLogFilter();

    private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        var q = TxtSearch.Text.Trim();
        _searchRe = null;
        if (q.Length > 0)
        {
            try { _searchRe = new Regex(q, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant); }
            catch (ArgumentException) { _searchRe = null; } // 非法正则 → 退化为子串匹配
        }
        ApplyLogFilter();
    }

    private void ApplyLogFilter()
    {
        // XAML 初始化阶段（如 CmbLevel SelectedIndex=0）会触发 SelectionChanged，
        // 但 _logView 尚未赋值，跳过即可；真正刷新会在构造末尾显式调用。
        if (_logView is null) return;
        _logView.Refresh();
    }

    private bool LogFilter(object item)
    {
        if (item is not LogLine line) return false;

        // 项目筛选（按 Name 匹配日志里的 Project 字段，下拉显示 DisplayName）
        var selName = (CmbProject.SelectedItem as ComboBoxItem)?.Tag as string;
        if (!string.IsNullOrEmpty(selName) && selName != AllProjectsTag)
        {
            if (!string.Equals(line.Project ?? "", selName, StringComparison.Ordinal)) return false;
        }

        // 级别筛选：0=全部 1=信息 2=警告 3=错误（枚举值 Debug<Info<Warn<Error）
        var minLevel = CmbLevel.SelectedIndex;
        if (minLevel > 0 && (int)line.Level < minLevel) return false;

        // 搜索：正则优先，失败退化为子串（忽略大小写）
        var q = TxtSearch.Text.Trim();
        if (q.Length > 0)
        {
            if (_searchRe != null)
            {
                if (!_searchRe.IsMatch(line.Message) && !_searchRe.IsMatch(line.Project ?? "")) return false;
            }
            else if (line.Message.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0
                     && (line.Project ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }
        }
        return true;
    }

    private void OnLogsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_autoScroll && e.Action == NotifyCollectionChangedAction.Add) ScrollLogToEnd();
    }

    private void LogAutoScroll_Changed(object sender, RoutedEventArgs e)
    {
        _autoScroll = ChkAutoScroll.IsChecked == true;
        if (_autoScroll) ScrollLogToEnd();
    }

    private void ScrollLogToEnd()
    {
        // 可能被 XAML 初始化阶段触发，此时 LogBox 尚未赋值
        if (LogBox?.Items.Count > 0)
            LogBox.ScrollIntoView(LogBox.Items.GetItemAt(LogBox.Items.Count - 1));
    }

    private void BtnClearLog_Click(object sender, RoutedEventArgs e) => Controller.ClearLogs();

    private void BtnTest_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ProjectVm vm) return;

        var cfg = Controller.PrepareHealthCheck(vm.Name);
        if (cfg == null)
        {
            MessageBox.Show(
                $"找不到项目 [{vm.DisplayName}] 的配置，无法执行健康检查。\n\n"
                + "请先点击「重载」重新加载配置目录。",
                "健康检查", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        new HealthReportWindow(cfg, line => Controller.LogHealth(vm.Name, line)) { Owner = this }
            .ShowDialog();
    }

    private void BtnNewProject_Click(object sender, RoutedEventArgs e) => EditProject(null);

    private void Grid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // 仅在选中行时打开编辑；列头/滚动条双击忽略
        if (Grid.SelectedItem is ProjectVm vm) EditProject(vm);
    }

    private void BtnDelete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ProjectVm vm) return;
        var path = Path.Combine(Controller.ConfigDir, vm.Name + ".yaml");
        var confirm = MessageBox.Show(
            $"确定删除项目 [{vm.DisplayName}] 吗？\n\n配置文件将移到回收站：\n{path}",
            "确认删除", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK) return;
        var err = Controller.TryDelete(vm.Name);
        if (err != null)
            MessageBox.Show(err, "删除失败", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private int _dragOverRef;
    private void Window_DragEnter(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            _dragOverRef++;
            if (DropOverlay != null) DropOverlay.Visibility = Visibility.Visible;
        }
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private void Window_DragLeave(object sender, DragEventArgs e)
    {
        if (_dragOverRef > 0) _dragOverRef--;
        if (_dragOverRef <= 0 && DropOverlay != null)
        {
            _dragOverRef = 0;
            DropOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        _dragOverRef = 0;
        if (DropOverlay != null) DropOverlay.Visibility = Visibility.Collapsed;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
        var files = (string[])e.Data.GetData(DataFormats.FileDrop);
        foreach (var f in files)
        {
            var ext = Path.GetExtension(f);
            if (ext.Equals(".yaml", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".yml", StringComparison.OrdinalIgnoreCase))
                Controller.ImportFile(f);
        }
    }

    private void EditProject(ProjectVm? vm)
    {
        ProjectFile? existing = null;
        if (vm != null)
        {
            existing = Controller.TryReadProjectFile(vm.Name);
            if (existing == null)
            {
                MessageBox.Show("无法读取项目原始配置（可能已被外部修改），已取消编辑。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }
        var win = new ProjectEditorWindow(existing) { Owner = this };
        if (win.ShowDialog() != true || win.Result == null) return;
        var err = vm == null
            ? Controller.TryCreate(win.Result)
            : Controller.TryUpdate(win.Result);
        if (err != null)
            MessageBox.Show(err, "保存失败", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
