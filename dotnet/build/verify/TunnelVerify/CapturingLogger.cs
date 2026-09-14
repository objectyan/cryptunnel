using Cryptunnel.Core.Logging;

namespace TunnelVerify;

/// <summary>
/// 捕获隧道日志供断言检查。隧道的很多行为（认证失败原因、安全告警）
/// 唯一的外部可观测出口就是日志，所以必须能验日志内容。
/// </summary>
internal sealed class CapturingLogger : ILogger
{
    private readonly List<LogEntry> _entries = [];
    private readonly object _lock = new();

    public LogLevel MinLevel => LogLevel.Debug;

    public event Action<LogEntry>? OnLog;

    public void Log(LogLevel level, string? project, string message)
    {
        var e = new LogEntry { Time = DateTime.Now, Level = level, Project = project, Message = message };
        lock (_lock) _entries.Add(e);
        OnLog?.Invoke(e);
    }

    internal IReadOnlyList<LogEntry> Entries
    {
        get { lock (_lock) return _entries.ToList(); }
    }

    /// <summary>是否有任何一条日志包含指定片段。</summary>
    internal bool Has(string fragment) =>
        Entries.Any(e => e.Message.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    internal bool HasAtLevel(LogLevel level, string fragment) =>
        Entries.Any(e => e.Level == level
                      && e.Message.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    internal string Dump() =>
        string.Join("\n", Entries.Select(e => $"    [{e.Level}] {e.Message}"));

    internal void Clear() { lock (_lock) _entries.Clear(); }
}
