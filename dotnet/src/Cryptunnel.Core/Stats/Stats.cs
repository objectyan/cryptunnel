using System;
using System.Collections.Generic;
using Cryptunnel.Core.Tunnel;

namespace Cryptunnel.Core.Stats;

public class ProjectStats
{
    public int Errors { get; set; }
    public int Warnings { get; set; }
    public int Connections { get; set; }
    public int Reconnects { get; set; }
    public long BytesIn { get; set; }
    public long BytesOut { get; set; }
    public int ActiveConnections { get; set; }
    public DateTime? ConnectedSince { get; set; }
    public string? LastError { get; set; }
    public string? Transport { get; set; }
}

public class StatsCollector
{
    private readonly Dictionary<string, ProjectStats> _map = new();
    private readonly object _lock = new();

    private ProjectStats Get(string name)
    {
        lock (_lock)
        {
            if (!_map.TryGetValue(name, out var s))
            {
                s = new ProjectStats();
                _map[name] = s;
            }
            return s;
        }
    }

    public void RecordError(string name, string message)
    {
        var s = Get(name);
        s.Errors++;
        s.LastError = message;
    }

    public void RecordWarning(string name) => Get(name).Warnings++;

    public void RecordBytes(string name, Direction dir, int n)
    {
        var s = Get(name);
        if (dir == Direction.In) s.BytesIn += n;
        else s.BytesOut += n;
    }

    public void OnConnect(string name, string transport)
    {
        var s = Get(name);
        s.Connections++;
        s.ActiveConnections++;
        s.ConnectedSince = DateTime.Now;
        s.Transport = transport;
    }

    public void OnDisconnect(string name)
    {
        var s = Get(name);
        if (s.ActiveConnections > 0) s.ActiveConnections--;
        s.ConnectedSince = null;
    }

    public void OnReconnect(string name) => Get(name).Reconnects++;

    public IReadOnlyDictionary<string, ProjectStats> Snapshot()
    {
        lock (_lock)
        {
            var copy = new Dictionary<string, ProjectStats>(_map.Count);
            foreach (var kv in _map) copy[kv.Key] = kv.Value;
            return copy;
        }
    }
}
