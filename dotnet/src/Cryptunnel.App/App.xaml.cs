using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Cryptunnel.App.Tray;

namespace Cryptunnel.App;

public partial class App : Application
{
    public AppController Controller { get; } = new();
    private AppTray? _tray;
    private Mutex? _singleInstance;
    private bool _ownsMutex;
    private bool _reallyExiting;

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

        if (e.Args.Length > 0 && File.Exists(e.Args[0]))
            Controller.ImportFile(e.Args[0]);

        // 自启（注册表 Run 键带 --minimized）：直接缩到托盘、不弹主窗口，并给个轻量提示
        var minimized = e.Args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));
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
