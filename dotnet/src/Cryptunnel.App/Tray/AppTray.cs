using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Cryptunnel.App.Models;

namespace Cryptunnel.App.Tray;

public sealed class AppTray : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly AppController _controller;
    private readonly Window _window;
    private readonly System.Windows.Forms.Timer _tooltipTimer;
    private System.Drawing.Color _lastIconColor = System.Drawing.Color.Empty;

    public AppTray(AppController controller, Window window)
    {
        _controller = controller;
        _window = window;
        _icon = new NotifyIcon
        {
            Icon = BuildIcon(StatusColor.AllRunning),
            Text = "Cryptunnel 隧道客户端",
            Visible = true
        };
        _icon.DoubleClick += (_, _) => ShowWindow();
        _icon.ContextMenuStrip = BuildMenu();
        _controller.FatalError += OnFatal;

        _tooltipTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        _tooltipTimer.Tick += (_, _) => UpdateTrayStatus();
        _tooltipTimer.Start();
    }

    private ContextMenuStrip BuildMenu()
    {
        var m = new ContextMenuStrip();
        m.Items.Add("显示主窗口", null, (_, _) => ShowWindow());
        m.Items.Add("重载配置", null, (_, _) => _controller.Reload());
        m.Items.Add("打开配置目录", null, (_, _) => _controller.OpenConfigDir());
        var autoStartItem = new ToolStripMenuItem("开机启动") { Checked = _controller.AutoStartEnabled };
        autoStartItem.Click += (_, _) => _controller.ToggleAutoStart();
        _controller.AutoStartChanged += v => autoStartItem.Checked = v;
        m.Items.Add(autoStartItem);
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("关于", null, (_, _) => ShowAbout());
        m.Items.Add("退出", null, (_, _) => ((App)System.Windows.Application.Current).RequestExit());
        return m;
    }

    /// <summary>关窗口缩到托盘时的气泡提示，避免用户以为程序没了。</summary>
    public void NotifyHidden()
    {
        _icon.BalloonTipTitle = "已最小化到托盘";
        _icon.BalloonTipText = "Cryptunnel 仍在后台运行，隧道保持连接。双击托盘图标可重新打开窗口。";
        _icon.BalloonTipIcon = ToolTipIcon.Info;
        _icon.ShowBalloonTip(4000);
    }

    /// <summary>随系统登录自动启动（带 --minimized）后给个轻量提示，让用户知道它在后台。</summary>
    public void NotifyAutoStart()
    {
        _icon.BalloonTipTitle = "Cryptunnel 已启动";
        _icon.BalloonTipText = "已随系统登录自动启动，隧道在后台运行。双击托盘图标可打开主窗口。";
        _icon.BalloonTipIcon = ToolTipIcon.Info;
        _icon.ShowBalloonTip(3000);
    }

    private void ShowWindow()
    {
        _window.Show();
        _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void ShowAbout()
    {
        // 自定义玻璃霓虹「关于」窗口（替换默认 MessageBox，保持视觉一致）
        var owner = System.Windows.Application.Current?.MainWindow ?? _window;
        var about = new AboutWindow(_controller.VersionText, _controller.ConfigDir, _controller.LogDir)
        {
            Owner = owner
        };
        about.ShowDialog();
    }

    private void OnFatal(string name)
    {
        _icon.BalloonTipTitle = "隧道启动失败";
        _icon.BalloonTipText = $"项目 [{name}] 无法启动（端口被占用或地址无效），其余项目不受影响";
        _icon.BalloonTipIcon = ToolTipIcon.Error;
        _icon.ShowBalloonTip(8000);
    }

    private void UpdateTrayStatus()
    {
        var projects = _controller.Projects;
        int total = projects.Count;
        int running = 0;
        int activeConn = 0;
        int errors = 0;
        foreach (var p in projects)
        {
            if (p.Running) running++;
            activeConn += p.ActiveConnections;
            errors += p.Errors;
        }

        StatusColor status;
        if (running == 0 && total > 0) status = StatusColor.Stopped;
        else if (running < total) status = StatusColor.Partial;
        else if (errors > 0) status = StatusColor.Warning;
        else status = StatusColor.AllRunning;

        var color = status switch
        {
            StatusColor.Stopped => System.Drawing.Color.FromArgb(0x88, 0x87, 0x80),
            StatusColor.Partial => System.Drawing.Color.FromArgb(0xBA, 0x75, 0x17),
            StatusColor.Warning => System.Drawing.Color.FromArgb(0xE2, 0x4B, 0x4A),
            _ => System.Drawing.Color.FromArgb(0x1D, 0x9E, 0x75)
        };

        if (_lastIconColor != color)
        {
            _lastIconColor = color;
            var old = _icon.Icon;
            _icon.Icon = BuildIcon(status);
            old?.Dispose();
        }

        var errPart = errors > 0 ? $" | 错误{errors}" : "";
        var stateText = status switch
        {
            StatusColor.Stopped => "已停止",
            StatusColor.Partial => "部分运行",
            StatusColor.Warning => "有错误",
            _ => "正常"
        };
        _icon.Text = $"Cryptunnel {stateText} | {running}/{total} | {activeConn}连接{errPart}";
    }

    private enum StatusColor { AllRunning, Partial, Warning, Stopped }

    private static System.Drawing.Color StatusToColor(StatusColor status) => status switch
    {
        StatusColor.Stopped => System.Drawing.Color.FromArgb(0x88, 0x87, 0x80),
        StatusColor.Partial => System.Drawing.Color.FromArgb(0xBA, 0x75, 0x17),
        StatusColor.Warning => System.Drawing.Color.FromArgb(0xE2, 0x4B, 0x4A),
        _ => System.Drawing.Color.FromArgb(0x1D, 0x9E, 0x75)
    };

    /// <summary>把绿色「J」画到一张 64×64 位图上（托盘与窗口图标共用）。</summary>
    private static void DrawJIcon(Graphics g, System.Drawing.Color color)
    {
        using var brush = new SolidBrush(color);
        using var pen = new SolidBrush(Color.White);
        FillRoundRect(g, brush, 4, 4, 56, 56, 14);
        using var font = new Font("Segoe UI", 32, System.Drawing.FontStyle.Bold);
        using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString("J", font, pen, new RectangleF(0, 0, 64, 64), sf);
    }

    private static Icon BuildIcon(StatusColor status)
    {
        using var bmp = new Bitmap(64, 64);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        DrawJIcon(g, StatusToColor(status));
        return Icon.FromHandle(bmp.GetHicon());
    }

    /// <summary>
    /// 生成与托盘同款的「J」图标，转成 WPF ImageSource，供 MainWindow 任务栏/标题栏使用。
    /// 用 GetHbitmap + CreateBitmapSourceFromHBitmap 会把像素拷进 WPF 表面，
    /// 之后即可释放 GDI 位图，避免 HIcon 句柄生命周期问题。
    /// </summary>
    public static System.Windows.Media.ImageSource BuildWindowIconImageSource()
    {
        using var bmp = new Bitmap(64, 64);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            DrawJIcon(g, StatusToColor(StatusColor.AllRunning));
        }
        var hBmp = bmp.GetHbitmap();
        try
        {
            var source = Imaging.CreateBitmapSourceFromHBitmap(
                hBmp, IntPtr.Zero, Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            DeleteObject(hBmp);
        }
    }

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteObject(IntPtr hObject);

    private static void FillRoundRect(Graphics g, System.Drawing.Brush b, int x, int y, int w, int h, int r)
    {
        using var path = new GraphicsPath();
        path.AddArc(x, y, r, r, 180, 90);
        path.AddArc(x + w - r, y, r, r, 270, 90);
        path.AddArc(x + w - r, y + h - r, r, r, 0, 90);
        path.AddArc(x, y + h - r, r, r, 90, 90);
        path.CloseFigure();
        g.FillPath(b, path);
    }

    public void Dispose()
    {
        _tooltipTimer?.Stop();
        _tooltipTimer?.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
    }
}
