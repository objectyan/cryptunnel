using System;
using System.IO;
using System.Threading;

namespace Cryptunnel.Core.Logging;

public enum LogLevel { Debug, Info, Warn, Error }

public class LogEntry
{
    public DateTime Time { get; init; }
    public LogLevel Level { get; init; }
    public string? Project { get; init; }
    public string Message { get; init; } = "";
}

public interface ILogger
{
    event Action<LogEntry>? OnLog;
    void Log(LogLevel level, string? project, string message);
    LogLevel MinLevel { get; }
}

/// <summary>
/// 滚动文件日志 + 事件广播。UI 订阅 OnLog 自行编组到 UI 线程。
/// 大小超限时滚动 proxy.log -> proxy.1.log ... 保留 retainDays 份。
/// </summary>
public sealed class FileLogger : ILogger
{
    private readonly string _dir;
    private readonly object _lock = new();
    private readonly long _maxBytes;
    private readonly int _retainDays;

    public FileLogger(string dir, LogLevel minLevel = LogLevel.Info, int maxFileSizeMb = 10, int retainDays = 7)
    {
        _dir = dir;
        MinLevel = minLevel;
        _maxBytes = (long)maxFileSizeMb * 1024 * 1024;
        _retainDays = Math.Max(1, retainDays);
        try
        {
            Directory.CreateDirectory(_dir);
            CleanupOld();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[日志] 初始化日志目录失败: {ex.Message}");
        }
    }

    public LogLevel MinLevel { get; }

    public event Action<LogEntry>? OnLog;

    public void Log(LogLevel level, string? project, string message)
    {
        if (level < MinLevel) return;
        var entry = new LogEntry { Time = DateTime.Now, Level = level, Project = project, Message = message };
        OnLog?.Invoke(entry);
        try
        {
            var line = $"{entry.Time:yyyy-MM-dd HH:mm:ss.fff} [{level.ToString().ToUpper()}]{((project != null) ? " [" + project + "]" : "")} {message}";
            AppendToFile(line);
        }
        catch
        {
            // 日志写失败不应影响隧道
        }
    }

    private void AppendToFile(string line)
    {
        var path = Path.Combine(_dir, "proxy.log");
        lock (_lock)
        {
            try
            {
                if (File.Exists(path) && new FileInfo(path).Length > _maxBytes)
                    Roll(path);
                using var w = new StreamWriter(path, true);
                w.WriteLine(line);
            }
            catch
            {
            }
        }
    }

    private void Roll(string path)
    {
        for (var i = _retainDays - 1; i >= 1; i--)
        {
            var src = i == 1 ? path : path.Replace(".log", $".{i}.log");
            var dst = path.Replace(".log", $".{i + 1}.log");
            try
            {
                if (File.Exists(src)) File.Move(src, dst, true);
            }
            catch
            {
            }
        }
    }

    private void CleanupOld()
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-_retainDays);
            foreach (var f in Directory.GetFiles(_dir, "proxy*.log"))
            {
                if (File.GetLastWriteTime(f) < cutoff) File.Delete(f);
            }
        }
        catch
        {
        }
    }
}
