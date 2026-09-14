using Cryptunnel.Core.Logging;

namespace Cryptunnel.App.Models;

public sealed class LogLine
{
    public DateTime Time { get; }
    public LogLevel Level { get; }
    public string? Project { get; }
    public string Message { get; }

    public string Text => $"{Time:HH:mm:ss} [{(Project ?? "-"),-10}] {Message}";

    public string Color => Level switch
    {
        LogLevel.Error => "#FF8A8A",
        LogLevel.Warn => "#FAC775",
        LogLevel.Debug => "#9a9a9a",
        _ => "#D3D1C7"
    };

    public string LevelText => Level switch
    {
        LogLevel.Error => "错误",
        LogLevel.Warn => "警告",
        LogLevel.Info => "信息",
        _ => "调试"
    };

    public LogLine(LogEntry e)
    {
        Time = e.Time;
        Level = e.Level;
        Project = e.Project;
        Message = e.Message;
    }
}
