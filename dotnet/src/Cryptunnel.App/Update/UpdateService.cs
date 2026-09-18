using System;
using System.Threading;
using System.Threading.Tasks;
using Cryptunnel.App.Tray;
using Velopack;
using Velopack.Sources;

namespace Cryptunnel.App.Update;

/// <summary>
/// Velopack 自动更新服务（静态）。
/// 两路触发：① 启动后静默检查（发现更新后台下载好、只弹气泡，不打断用户）；
///          ② 托盘「检查更新」手动检查（明确反馈 检查中/已是最新/可更新）。
/// 下载完成后统一走「现在重启（应用更新并重启）/ 稍后（下次启动时自动应用）」。
/// 全程只读 Release，不改隧道运行状态；更新只在用户选「现在重启」或下次启动时生效。
/// </summary>
public static class UpdateService
{
    private const string RepoUrl = "https://github.com/objectyan/cryptunnel";

    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(8);

    private static UpdateManager? _mgr;
    // 已下载、待应用的更新。在 CheckAsync（后台 Task）里赋值，CS0649 是编译器看不到跨 Task 赋值的误报。
    private static UpdateInfo? _pending = null;
    private static AppTray.TrayNotifier? _notify;
    private static Action? _restart;
    private static Func<bool>? _confirm;
    private static Action<string>? _status;
    private static int _checking;   // 0/1，防止手动与静默并发触发

    private enum Mode { Silent, Manual }

    /// <summary>安装服务。四个回调都由 App/托盘注入，本类不直接弹 UI。</summary>
    /// <param name="notify">气泡提示（标题, 正文, 是否错误）。</param>
    /// <param name="restart">用户确认后执行的「应用更新并重启」。</param>
    /// <param name="confirm">弹「现在重启?」确认框，返回 true=立即重启。</param>
    /// <param name="status">可选：回写「正在检查更新…」之类的状态文案。</param>
    public static void Init(AppTray.TrayNotifier notify, Action restart, Func<bool> confirm, Action<string>? status = null)
    {
        _notify = notify;
        _restart = restart;
        _confirm = confirm;
        _status = status;
        try
        {
            // 非安装（便携/开发）模式下 UpdateManager 构造会抛，降级为「更新不可用」而不是崩进程
            _mgr = new UpdateManager(new GithubSource(RepoUrl, accessToken: null, prerelease: false));
        }
        catch
        {
            _mgr = null;
        }
    }

    /// <summary>启动静默检查：延时后后台跑，发现更新静默下载好，只气泡提示，不打断。</summary>
    public static void KickoffSilentCheck()
    {
        if (_mgr == null) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(StartupDelay).ConfigureAwait(false);
            await CheckAsync(Mode.Silent).ConfigureAwait(false);
        });
    }

    /// <summary>托盘手动「检查更新」：立即检查，明确反馈结果。</summary>
    public static void CheckNowInteractive()
    {
        if (_mgr == null)
        {
            Notify("更新不可用", "当前为便携/开发模式（非 Velopack 安装），无法自动更新。", error: true);
            return;
        }
        _ = Task.Run(() => CheckAsync(Mode.Manual));
    }

    /// <summary>应用已下载的更新并重启。由 App 的 restart 回调调用（先停隧道再来这）。</summary>
    public static void ApplyPendingAndRestart()
    {
        if (_mgr == null || _pending == null) return;
        _mgr.ApplyUpdatesAndRestart(_pending);
    }

    private static async Task CheckAsync(Mode mode)
    {
        if (Interlocked.Exchange(ref _checking, 1) == 1)
        {
            if (mode == Mode.Manual) Notify("检查更新", "正在检查更新，请稍候…", error: false);
            return;
        }
        try
        {
            Status("正在检查更新…");
            var info = await _mgr!.CheckForUpdatesAsync().ConfigureAwait(false);
            if (info == null)
            {
                Status(string.Empty);
                if (mode == Mode.Manual)
                    Notify("已是最新", $"当前版本 v{CurrentVersion()} 已是最新版本。", error: false);
                return;
            }

            var newVer = info.TargetFullRelease?.Version?.ToString() ?? "新版本";
            Status($"发现新版本 v{newVer}，正在下载…");
            if (mode == Mode.Manual)
                Notify("发现新版本", $"检测到新版本 v{newVer}，正在后台下载…", error: false);

            await _mgr.DownloadUpdatesAsync(info).ConfigureAwait(false);
            Status(string.Empty);

            var apply = _confirm?.Invoke() ?? false;   // 「现在重启应用更新?」
            if (apply)
            {
                _restart?.Invoke();   // ApplyUpdatesAndRestart：应用更新并退出，Velopack 拉起新版本
            }
            else
            {
                Notify("更新已就绪",
                    $"新版本 v{newVer} 已下载完成，将在下次启动时自动生效；也可点托盘「检查更新」后选择立即重启。",
                    error: false);
            }
        }
        catch (Exception ex)
        {
            Status(string.Empty);
            if (mode == Mode.Manual)
                Notify("检查更新失败", ex.Message, error: true);
            // 静默模式失败不打扰用户（离线、无 Release、网络抖动都很正常）
        }
        finally
        {
            Interlocked.Exchange(ref _checking, 0);
        }
    }

    private static string CurrentVersion()
    {
        try
        {
            var v = typeof(UpdateService).Assembly.GetName().Version;
            return v == null ? "?" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
        catch { return "?"; }
    }

    private static void Notify(string title, string text, bool error)
    {
        try { _notify?.Invoke(title, text, error); } catch { }
    }

    private static void Status(string s)
    {
        try { _status?.Invoke(s); } catch { }
    }
}
