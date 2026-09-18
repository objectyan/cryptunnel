using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Cryptunnel.App.Tray;
using Cryptunnel.App.Update;
using Velopack;

namespace Cryptunnel.App;

public partial class App : Application
{
    public AppController Controller { get; } = new();
    private AppTray? _tray;
    private Mutex? _singleInstance;
    private bool _ownsMutex;
    private bool _reallyExiting;

    /// <summary>
    /// 自定义入口（csproj 里 StartupObject 指向这里）。Velopack 必须最先跑——
    /// 它是安装/更新框架，会处理「正在更新、待重启、便携模式」等场景，抢在 WPF 初始化之前。
    /// 之后正常走 WPF 启动：new App + InitializeComponent + Run（App.xaml 无 StartupUri，由 OnStartup 建窗）。
    /// </summary>
    [STAThread]
    private static void Main(string[] args)
    {
        VelopackApp.Build().Run();
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        // 后台隧道工具语义：关闭主窗口 = 缩到托盘、隧道继续运行；
        // 只有托盘「退出」/窗口「退出」按钮才真正退出（见 RequestExit）。
        // 若用默认的 OnLastWindowClose，用户一关窗口就会把隧道一起杀掉，完全反直觉。
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // 全局异常处理：任何未处理异常都必须「看得见」。
        // 历史教训：Run.Text 默认是 TwoWay 绑定，绑到只读属性（ModeText/ConfigDir）会在
        // win.Show() 时抛异常，进程直接退出、NotifyIcon 来不及 Dispose，只在通知区留下
        // 幽灵图标 —— 用户完全感知不到是崩溃，只会觉得「界面没得」。
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        base.OnStartup(e);

        // 单实例保护：同一会话内只允许一个 Cryptunnel 客户端运行
        _singleInstance = new Mutex(true, "CryptunnelClientSingletonMutex", out var created);
        _ownsMutex = created;
        if (!created)
        {
            // 已在运行：把已有窗口提到前台，而不是只弹窗退出
            FocusExistingInstance();
            Shutdown();
            return;
        }

        Controller.Start();

        var win = new MainWindow();
        _tray = new AppTray(Controller, win);

        // 自动更新（Velopack）：注入气泡/确认/重启回调，启动后静默检查。
        // restart 回调里先停隧道再让 Velopack 应用更新并重启，避免带连接强杀。
        UpdateService.Init(
            notify: _tray.NotifyUpdate,
            restart: ApplyUpdateAndRestart,
            confirm: ConfirmRestartForUpdate,
            status: _ => { });
        UpdateService.KickoffSilentCheck();

        // 自定义 Main 下 WPF 不再注入 StartupEventArgs.Args，统一读全局命令行
        var args = Environment.GetCommandLineArgs().Skip(1).ToArray();

        if (args.Length > 0 && File.Exists(args[0]))
            Controller.ImportFile(args[0]);

        // 自启（注册表 Run 键带 --minimized）：直接缩到托盘、不弹主窗口，并给个轻量提示
        var minimized = args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));
        if (minimized)
        {
            win.Hide();
            _tray.NotifyAutoStart();
        }
        else
        {
            win.Show();
        }

        // 关闭主窗口 → 取消关闭、隐藏到托盘，隧道保持运行
        win.Closing += (_, ev) =>
        {
            if (_reallyExiting) return;   // 真退出：放行，让窗口正常关闭
            ev.Cancel = true;
            win.Hide();
            _tray?.NotifyHidden();
        };
    }

    /// <summary>真正退出应用（托盘「退出」与窗口「退出」按钮调用）。</summary>
    public void RequestExit()
    {
        _reallyExiting = true;
        Shutdown();
    }

    /// <summary>下载好更新后弹确认：是否「现在重启」应用更新（否则下次启动时生效）。须在 UI 线程弹。</summary>
    private bool ConfirmRestartForUpdate()
    {
        try
        {
            var disp = System.Windows.Application.Current?.Dispatcher;
            if (disp == null) return false;
            if (!disp.CheckAccess())
                return disp.Invoke(ConfirmRestartForUpdateCore);
            return ConfirmRestartForUpdateCore();
        }
        catch { return false; }
    }

    private bool ConfirmRestartForUpdateCore()
    {
        var r = MessageBox.Show(
            "新版本已下载完成。\n\n是否现在重启 Cryptunnel 应用更新？\n（选择「否」则下次启动时自动生效）",
            "Cryptunnel 更新就绪", MessageBoxButton.YesNo, MessageBoxImage.Question);
        return r == MessageBoxResult.Yes;
    }

    /// <summary>用户选「现在重启」：先停隧道，再让 Velopack 应用更新并重启到新版本。</summary>
    private void ApplyUpdateAndRestart()
    {
        try
        {
            _reallyExiting = true;
            Controller.Stop();   // 优雅停隧道，避免带连接强杀
        }
        catch { }
        try
        {
            UpdateService.ApplyPendingAndRestart();
        }
        catch
        {
            Shutdown();
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr FindWindow(string? lpClassName, string lpWindowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SwRestore = 9;

    /// <summary>第二个实例检测到已在运行时，把已有主窗口（即使缩在托盘）恢复到前台。</summary>
    private static void FocusExistingInstance()
    {
        var hwnd = FindWindow(null, "Cryptunnel 隧道客户端");
        if (hwnd == IntPtr.Zero) return;
        ShowWindow(hwnd, SwRestore);
        SetForegroundWindow(hwnd);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var ex = e.Exception;
        try { Controller.LogFatal($"UI 线程未处理异常：{ex}"); } catch { }
        DisposeTray();   // 清掉托盘图标，避免残留幽灵图标
        try
        {
            MessageBox.Show(
                $"应用遇到未处理异常，即将退出：\n\n{ex.Message}\n\n完整堆栈已写入日志目录。",
                "Cryptunnel 异常", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch { }
        e.Handled = true;   // 阻止默认静默崩溃，改走我们自己的退出路径
        Shutdown();
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        // 非 UI 线程不弹框，只落盘
        try { Controller.LogFatal($"非 UI 线程未处理异常：{e.ExceptionObject}"); } catch { }
    }

    private void DisposeTray()
    {
        try { _tray?.Dispose(); } catch { }
        _tray = null;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Controller.Stop();
        DisposeTray();
        if (_ownsMutex)
        {
            try { _singleInstance?.ReleaseMutex(); } catch { }
        }
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
