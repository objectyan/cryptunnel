using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Cryptunnel.App.Models;
using Cryptunnel.Core.Logging;

namespace Cryptunnel.App;

public partial class ErrorDetailWindow : Window
{
    public string ProjectName { get; }
    public IReadOnlyList<LogLine> Items { get; }
    public int ErrorCount { get; }
    public int WarnCount { get; }
    public string FilterLabel { get; }
    private readonly string? _logDir;

    public ErrorDetailWindow(string projectName, IEnumerable<LogLine> logs, string? logDir, LogLevel? filter = null)
    {
        ProjectName = projectName;
        _logDir = logDir;
        var filtered = logs
            .Where(l => l.Project == projectName && (filter == null ? (l.Level == LogLevel.Error || l.Level == LogLevel.Warn) : l.Level == filter.Value))
            .OrderByDescending(l => l.Time)
            .ToList();
        Items = filtered;
        ErrorCount = filtered.Count(l => l.Level == LogLevel.Error);
        WarnCount = filtered.Count(l => l.Level == LogLevel.Warn);
        FilterLabel = filter == null ? "" : (filter == LogLevel.Error ? "（仅错误）" : "（仅警告）");
        InitializeComponent();
        DataContext = this;
    }

    private void BtnOpenLog_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_logDir)) return;
        try
        {
            Directory.CreateDirectory(_logDir);
            System.Diagnostics.Process.Start("explorer.exe", _logDir);
        }
        catch
        {
            // 打开失败忽略
        }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    // 标题栏拖动（无边框窗口）
    private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.OriginalSource is Button) return;
        if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed) DragMove();
    }
}
