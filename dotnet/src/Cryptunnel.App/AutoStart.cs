using System.Diagnostics;
using Microsoft.Win32;

namespace Cryptunnel.App;

/// <summary>
/// 开机启动：写入当前用户登录启动项（HKCU\Software\Microsoft\Windows\CurrentVersion\Run）。
/// 免管理员权限，是托盘类后台工具的标准做法。
/// 自启时带 <c>--minimized</c> 参数，应用直接缩到托盘、不弹主窗口。
/// </summary>
public static class AutoStart
{
    private const string AppName = "Cryptunnel";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>当前 exe 完整路径（单文件发布下也准确）。</summary>
    public static string ExePath =>
        Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
                return key?.GetValue(AppName) is string val && val.Length > 0;
            }
            catch
            {
                return false;
            }
        }
    }

    public static void Enable()
    {
        var exe = ExePath;
        if (string.IsNullOrEmpty(exe)) return;
        var cmd = $"\"{exe}\" --minimized";
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, true)
                       ?? Registry.CurrentUser.CreateSubKey(RunKey);
        key.SetValue(AppName, cmd, RegistryValueKind.String);
    }

    public static void Disable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
            key?.DeleteValue(AppName, false);
        }
        catch
        {
            // 删除失败（如权限/不存在）忽略，不影响主流程
        }
    }
}
