using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Cryptunnel.Core.Crypto;
using Cryptunnel.Core.Logging;
using Cryptunnel.Core.Models;
using Cryptunnel.Core.Tunnel;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Cryptunnel.Core.Config;

public class ConfigError
{
    public string File { get; init; } = "";
    public string Message { get; init; } = "";
}

public static class ConfigLoader
{
    private const int SupportedSchema = 1;
    private static readonly Regex NameRe = new("^[a-z0-9-]+$", RegexOptions.Compiled);

    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>
    /// 单文件可同时容纳 defaults 段和项目字段，避免为区分两种文件建两套模型。
    /// </summary>
    private class RootFile
    {
        public int SchemaVersion { get; set; } = 1;
        public DefaultsSection? Defaults { get; set; }
        public string? Name { get; set; }
        public string? DisplayName { get; set; }
        public bool? Enabled { get; set; }
        public string? ServerUrl { get; set; }
        public string? AesKey { get; set; }
        public string? AuthKey { get; set; }
        public LocalSection? Local { get; set; }
        public string? WsPath { get; set; }
        public string? HttpBasePath { get; set; }
        public TransportSection? Transport { get; set; }
        public TimeoutsSection? Timeouts { get; set; }
        public ReconnectSection? Reconnect { get; set; }
        public LoggingSection? Logging { get; set; }
        public int? ChunkSize { get; set; }

        /// <summary>周期性健康检查。留空表示沿用默认（关闭）。</summary>
        public HealthSection? Health { get; set; }

        /// <summary>
        /// 隧道加密算法。留空表示沿用默认（<c>aes-256-cbc-hmac-sha256</c>），
        /// 这保证了不含该键的老配置文件行为完全不变。
        /// </summary>
        public string? Cipher { get; set; }
    }

    public static (List<TunnelConfig> Configs, List<ConfigError> Errors) Load(string configDir, ILogger? logger = null)
    {
        var configs = new List<TunnelConfig>();
        var errors = new List<ConfigError>();

        if (!Directory.Exists(configDir))
        {
            errors.Add(new ConfigError { File = configDir, Message = "配置目录不存在" });
            return (configs, errors);
        }

        var defaults = new DefaultsSection();
        var files = Directory.GetFiles(configDir, "*.yaml")
            .Concat(Directory.GetFiles(configDir, "*.yml"))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // 第一遍：收集 defaults（按文件名顺序叠加）
        foreach (var f in files)
        {
            RootFile? root = TryParse(f, errors);
            if (root?.Defaults != null) MergeInto(defaults, root.Defaults);
        }

        // 第二遍：解析项目
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usedPorts = new HashSet<int>();

        foreach (var f in files)
        {
            RootFile? root = TryParse(f, errors);
            if (root == null) continue;
            if (root.Defaults != null && root.Name == null) continue; // 纯 defaults 文件
            if (root.Name == null)
            {
                errors.Add(new ConfigError { File = Path.GetFileName(f), Message = "缺少 name 字段，且不是 defaults 文件" });
                continue;
            }

            var (cfg, err) = Resolve(root, defaults, usedNames, usedPorts);
            if (err != null)
            {
                errors.Add(new ConfigError { File = Path.GetFileName(f), Message = err });
                continue;
            }
            configs.Add(cfg!);
        }

        logger?.Log(LogLevel.Info, null, $"配置加载完成：{configs.Count} 个有效项目，{errors.Count} 个错误（目录 {configDir}）");
        return (configs, errors);
    }

    private static RootFile? TryParse(string file, List<ConfigError> errors)
    {
        try
        {
            var text = File.ReadAllText(file);
            var root = Yaml.Deserialize<RootFile>(text);
            if (root == null) throw new Exception("空文件");
            if (root.SchemaVersion != SupportedSchema)
                errors.Add(new ConfigError { File = Path.GetFileName(file), Message = $"schemaVersion={root.SchemaVersion} 不被支持（要求 {SupportedSchema}，拒绝加载以免误兼容）" });
            return root;
        }
        catch (Exception ex)
        {
            errors.Add(new ConfigError { File = Path.GetFileName(file), Message = $"解析失败：{ex.Message}" });
            return null;
        }
    }

    private static (TunnelConfig? Cfg, string? Error) Resolve(RootFile r, DefaultsSection d, HashSet<string> usedNames, HashSet<int> usedPorts)
    {
        var name = r.Name!;
        if (!NameRe.IsMatch(name))
            return (null, "name 只能是小写字母/数字/连字符 [a-z0-9-]");
        if (!usedNames.Add(name))
            return (null, "name 重复");
        var serverUrl = (r.ServerUrl ?? "").TrimEnd('/');
        if (string.IsNullOrWhiteSpace(serverUrl) || (!serverUrl.StartsWith("http://") && !serverUrl.StartsWith("https://")))
            return (null, "serverUrl 必填且以 http:// 或 https:// 开头");
        if (string.IsNullOrWhiteSpace(r.AesKey) || string.IsNullOrWhiteSpace(r.AuthKey))
            return (null, "aesKey 与 authKey 必填且非空");
        var port = r.Local?.Port ?? d.Local?.Port ?? 0;
        if (port < 1024 || port > 65535)
            return (null, $"local.port={port} 超出范围（1024-65535）");
        if (!usedPorts.Add(port))
            return (null, $"local.port={port} 与其他项目冲突");

        // 监听地址闸门。
        //
        // 非回环监听是本客户端唯一一个能让本机之外的人穿透防火墙的配置：局域网内任意机器
        // 都能连上这个端口，而隧道会用本地配置的密钥替对方完成认证 —— 对方不需要知道
        // aesKey 或 authKey。所以这里默认拒绝，必须由 local.allowNonLoopback: true 显式放行。
        //
        // 这是有意的破坏性变更：老配置里写了 0.0.0.0 但没加开关的，升级后会启动失败并看到
        // 一行明确的改法说明。相比「默认存在一个无需密钥即可利用的入口」，让用户补一行配置
        // 是更便宜的代价，而且这一次补配置的动作本身就是一次风险确认。
        var listenAddress = r.Local?.Address ?? d.Local?.Address ?? "127.0.0.1";
        var allowNonLoopback = r.Local?.AllowNonLoopback ?? d.Local?.AllowNonLoopback ?? false;
        var addrErr = ListenAddressPolicy.Validate(listenAddress, allowNonLoopback, port);
        if (addrErr != null) return (null, addrErr);

        var mode = ParseModeSafe(r.Transport?.Mode ?? d.Transport?.Mode, out var modeErr);
        if (modeErr != null) return (null, modeErr);

        // 分片大小必须在这里就拦住。
        // chunkSize 配大了不会在启动时出任何异常，而是等到传输一个较大的结果集时，
        // 某一帧加密+Base64 后超过接收端 maxTextMessageBufferSize（默认 8192），
        // 对端直接 WS 1009 CLOSE_TOO_BIG —— 用户看到的是 DBeaver 报
        // 「08S01 Communications link failure」，与真正的网络故障无法区分。
        // ADR-0001 为此付过两轮线上故障的代价，所以要把它提前成「启动即报错」。
        var chunkSize = r.ChunkSize ?? d.ChunkSize ?? 4096;
        var chunkErr = TunnelFraming.ValidateChunkSize(chunkSize);
        if (chunkErr != null) return (null, chunkErr);

        // 加密算法同样必须在这里拦住，而且理由比 chunkSize 更强。
        //
        // 按 ADR-0003 方案 B，报文内不含任何算法标识（防 DPI），两端纯靠配置字符串约定
        // 对齐。所以算法名写错时没有任何协商或纠错的余地：客户端会用一个服务端不认的算法
        // 加密认证报文，服务端解不开，回 close reason「Auth decrypt failed」——
        // 而这条 reason 与 aesKey 配错完全无法区分，是全链路最难诊断的一种失败。
        //
        // 把它提前成「启动即报错」，用户看到的是本地一行明确的「不是支持的加密算法，可用：…」，
        // 而不是隧道起来了、DBeaver 连不上、日志里一句语焉不详的解密失败。
        var (cipherId, cipherErr) = ResolveCipher(r.Cipher ?? d.Cipher);
        if (cipherErr != null) return (null, cipherErr);

        // 周期性健康检查的间隔同样在这里拦。
        //
        // 这一项与 chunkSize / cipher 的性质不同：写小了不会让隧道不可用，
        // 而是会安静地按那个频率消耗服务端连接名额 —— 填 1 秒的用户不会看到任何异常，
        // 只有服务端管理员会在几天后发现连接数莫名其妙地满了，且极难溯源到某个客户端。
        // 所以必须启动即拒绝，而不是「悄悄夹到下限」：后者会让配置文件里写的数字
        // 与实际行为不一致，下次排查时又多一层误导。
        var healthEnabled = r.Health?.Enabled ?? d.Health?.Enabled ?? false;
        var healthInterval = r.Health?.IntervalSec ?? d.Health?.IntervalSec
                             ?? TunnelConfig.HealthIntervalDefaultSec;
        if (healthEnabled && healthInterval < TunnelConfig.HealthIntervalMinSec)
            return (null, $"health.intervalSec={healthInterval} 小于允许的最小值 "
                          + $"{TunnelConfig.HealthIntervalMinSec} 秒。每次健康检查都会在服务端"
                          + "真实建立并断开一条数据库连接，过于频繁会占满服务端连接数。");

        var cfg = new TunnelConfig
        {
            Name = name,
            DisplayName = r.DisplayName ?? name,
            Enabled = r.Enabled ?? true,
            ServerUrl = serverUrl,
            AesKey = r.AesKey!,
            AuthKey = r.AuthKey!,
            ListenAddress = listenAddress,
            AllowNonLoopback = allowNonLoopback,
            LocalPort = port,
            WsPath = r.WsPath ?? d.WsPath ?? "/ws-cryptunnel",
            HttpBasePath = NormalizeBasePath(r.HttpBasePath ?? d.HttpBasePath ?? "/cryptunnel"),
            Mode = mode,
            AllowFallback = r.Transport?.AllowFallback ?? d.Transport?.AllowFallback ?? true,
            WsConnectMs = r.Timeouts?.WsConnectMs ?? d.Timeouts?.WsConnectMs ?? 10000,
            HttpConnectMs = r.Timeouts?.HttpConnectMs ?? d.Timeouts?.HttpConnectMs ?? 10000,
            HttpReadMs = r.Timeouts?.HttpReadMs ?? d.Timeouts?.HttpReadMs ?? 30000,
            AuthResponseMs = r.Timeouts?.AuthResponseMs ?? d.Timeouts?.AuthResponseMs ?? 3000,
            ReconnectDelayMs = r.Timeouts?.ReconnectDelayMs ?? d.Timeouts?.ReconnectDelayMs ?? 3000,
            ReconnectEnabled = r.Reconnect?.Enabled ?? d.Reconnect?.Enabled ?? true,
            ReconnectMaxAttempts = r.Reconnect?.MaxAttempts ?? d.Reconnect?.MaxAttempts ?? 0,
            LogLevel = ParseLevel(r.Logging?.Level ?? d.Logging?.Level) ?? LogLevel.Info,
            ChunkSize = chunkSize,
            Cipher = cipherId,
            HealthCheckEnabled = healthEnabled,
            HealthIntervalSec = healthInterval,
        };
        return (cfg, null);
    }

    /// <summary>
    /// 校验并归一加密算法标识。
    ///
    /// <para><b>归一（<see cref="CipherRegistry.Normalize"/>）是必须的</b>，不能只做 Contains 检查后
    /// 原样存下去。<c>sm4</c> 是 <c>sm4-cbc-hmac-sha256</c> 的合法别名，若原样保留，
    /// 界面展示、YAML 回写、日志输出三处会出现同一算法的两种写法，
    /// 用户核对两端配置时会误以为不一致。</para>
    ///
    /// <para>留空返回默认算法标识 —— 老配置文件不写 cipher 时走这条路径。</para>
    /// </summary>
    /// <summary>
    /// 归一化 HTTP 基础路径：保证以 '/' 开头、不以 '/' 结尾。
    ///
    /// <para>为什么必须归一：三个端点是 <c>基础路径 + "/connect"</c> 这样拼出来的。
    /// 用户很自然会写成 <c>cryptunnel</c>（漏开头斜杠）或 <c>/cryptunnel/</c>
    /// （多结尾斜杠），拼出来就成了 <c>serverUrlcryptunnel/connect</c> 或
    /// <c>/cryptunnel//connect</c> —— 前者路径拼错、后者被服务端当成不同路径，
    /// 两种都是 404，而且报错信息完全看不出是配置写法问题。</para>
    /// </summary>
    private static string NormalizeBasePath(string raw)
    {
        var s = (raw ?? string.Empty).Trim();
        if (s.Length == 0) return "/cryptunnel";
        if (s[0] != '/') s = "/" + s;
        while (s.Length > 1 && s[s.Length - 1] == '/') s = s.Substring(0, s.Length - 1);
        return s;
    }

    private static (string Id, string? Error) ResolveCipher(string? configured)
    {
        var raw = configured?.Trim();
        if (string.IsNullOrEmpty(raw))
            return (CipherRegistry.Default.Id, null);
        if (!CipherRegistry.Contains(raw))
            return ("", $"cipher=\"{raw}\" 不是支持的加密算法，可用：{string.Join(" / ", CipherRegistry.Ids)}"
                      + "（此值必须与服务端 cryptunnel.cipher 完全一致，报文内不含算法标识、无法自动协商）");
        return (CipherRegistry.Normalize(raw), null);
    }

    private static TransportMode ParseMode(string? s, out string? error)
    {
        error = null;
        return s?.ToLowerInvariant() switch
        {
            null or "" or "auto" => TransportMode.Auto,
            "websocket" or "ws" => TransportMode.WebSocket,
            "http" => TransportMode.Http,
            _ => throw new InvalidOperationException($"未知 mode: {s}")
        };
    }

    // 防止上个 switch 表达式在未来扩展时漏处理，统一在调用处兜底
    private static TransportMode ParseModeSafe(string? s, out string? error)
    {
        try
        {
            return ParseMode(s, out error);
        }
        catch (InvalidOperationException)
        {
            error = $"未知 transport.mode: {s}（可选 auto/websocket/http）";
            return TransportMode.Auto;
        }
    }

    private static LogLevel? ParseLevel(string? s)
    {
        return s?.ToLowerInvariant() switch
        {
            null or "" => null,
            "debug" => LogLevel.Debug,
            "info" => LogLevel.Info,
            "warn" or "warning" => LogLevel.Warn,
            "error" => LogLevel.Error,
            _ => null
        };
    }

    private static void MergeInto(DefaultsSection target, DefaultsSection src)
    {
        target.Local ??= src.Local;
        target.WsPath ??= src.WsPath;
        target.HttpBasePath ??= src.HttpBasePath;
        target.Transport ??= src.Transport;
        target.Timeouts ??= src.Timeouts;
        target.Reconnect ??= src.Reconnect;
        target.Logging ??= src.Logging;
        target.ChunkSize ??= src.ChunkSize;
        target.Cipher ??= src.Cipher;
        target.Health ??= src.Health;
    }

    /// <summary>
    /// 读取单个项目配置文件（不验证），用于编辑回填。仅返回项目字段（不含 defaults 段）。
    /// </summary>
    public static (ProjectFile? Pf, string? Error) TryReadFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return (null, $"文件不存在：{path}");
            var text = File.ReadAllText(path);
            var root = Yaml.Deserialize<RootFile>(text);
            if (root == null) return (null, "空文件");
            var pf = new ProjectFile
            {
                SchemaVersion = root.SchemaVersion,
                Name = root.Name,
                DisplayName = root.DisplayName,
                Enabled = root.Enabled,
                ServerUrl = root.ServerUrl,
                AesKey = root.AesKey,
                AuthKey = root.AuthKey,
                Local = root.Local,
                WsPath = root.WsPath,
                HttpBasePath = root.HttpBasePath,
                Transport = root.Transport,
                Timeouts = root.Timeouts,
                Reconnect = root.Reconnect,
                Logging = root.Logging,
                ChunkSize = root.ChunkSize,
                Cipher = root.Cipher,
                Health = root.Health,
            };
            return (pf, null);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }
}
