using Cryptunnel.Core.Logging;

namespace Cryptunnel.Core.Models;

public enum TransportMode { Auto, WebSocket, Http }

/// <summary>YAML 里单个项目文件的原始结构（含可选覆盖段）。</summary>
public class ProjectFile
{
    public int SchemaVersion { get; set; } = 1;
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

    /// <summary>周期性健康检查。留空回落到 defaults 段，再回落到「关闭」。</summary>
    public HealthSection? Health { get; set; }

    /// <summary>
    /// 隧道加密算法。<c>null</c> 表示未在本文件显式指定，会回落到 defaults 段、
    /// 再回落到内置默认值 —— 这三层缺省链保证老配置文件零变化。
    /// </summary>
    public string? Cipher { get; set; }
}

public class LocalSection
{
    public string? Address { get; set; }
    public int? Port { get; set; }

    /// <summary>
    /// 是否允许把 <see cref="Address"/> 设成非回环地址（0.0.0.0 / :: / 具体网卡 IP）。
    ///
    /// <para><c>null</c> 表示未显式指定，回落到 defaults 段、再回落到内置默认 <c>false</c>。
    /// 也就是说<b>老配置文件不写这一项时，非回环监听会被拒绝</b> —— 这是有意的破坏性变更，
    /// 因为默认放行意味着默认存在一个无需密钥即可被利用的入口。</para>
    /// </summary>
    public bool? AllowNonLoopback { get; set; }
}

public class TransportSection
{
    public string? Mode { get; set; }
    public bool? AllowFallback { get; set; }
}

public class TimeoutsSection
{
    public int? WsConnectMs { get; set; }
    public int? HttpConnectMs { get; set; }
    public int? HttpReadMs { get; set; }
    public int? AuthResponseMs { get; set; }
    public int? ReconnectDelayMs { get; set; }
}

public class ReconnectSection
{
    public bool? Enabled { get; set; }
    public int? MaxAttempts { get; set; }
}

/// <summary>
/// 周期性健康检查配置。
///
/// <para><b>默认关闭（opt-in）</b>。每一次探活都会让服务端真实建立并断开一条
/// MySQL 连接，占用服务端 <c>maxConnections</c> 的一个名额。默认开启意味着
/// 「装上客户端就自动持续消耗服务端连接名额」，而多客户端同时在线时这个消耗是叠加的 ——
/// 一个诊断功能不该有能力把生产环境的连接池吃满。</para>
/// </summary>
public class HealthSection
{
    /// <summary>是否启用周期性自动检查。<c>null</c> 回落到 defaults 段，再回落到 <c>false</c>。</summary>
    public bool? Enabled { get; set; }

    /// <summary>检查间隔（秒）。<c>null</c> 回落到 defaults 段，再回落到 300。</summary>
    public int? IntervalSec { get; set; }
}

public class LoggingSection
{
    public string? Level { get; set; }
    public int? MaxFileSizeMb { get; set; }
    public int? RetainDays { get; set; }
}

/// <summary>defaults 文件：整段作为基线被各项目深度覆盖。</summary>
public class DefaultsFile
{
    public int SchemaVersion { get; set; } = 1;
    public DefaultsSection? Defaults { get; set; }
}

public class DefaultsSection
{
    public LocalSection? Local { get; set; }
    public string? WsPath { get; set; }
    public string? HttpBasePath { get; set; }
    public TransportSection? Transport { get; set; }
    public TimeoutsSection? Timeouts { get; set; }
    public ReconnectSection? Reconnect { get; set; }
    public LoggingSection? Logging { get; set; }
    public int? ChunkSize { get; set; }

    /// <summary>全局缺省的周期性健康检查设置。项目文件里的 <c>health</c> 优先于此项。</summary>
    public HealthSection? Health { get; set; }

    /// <summary>
    /// 全局缺省加密算法。项目文件里的 <c>cipher</c> 优先于此项。
    /// </summary>
    public string? Cipher { get; set; }
}

/// <summary>合并 defaults + 项目文件后，隧道实际使用的配置。</summary>
public class TunnelConfig
{
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string ServerUrl { get; set; } = "";
    public string AesKey { get; set; } = "";
    public string AuthKey { get; set; } = "";
    public string ListenAddress { get; set; } = "127.0.0.1";

    /// <summary>
    /// 是否显式允许非回环监听。默认 <c>false</c>。
    ///
    /// <para>这不是「要不要打告警」的开关，而是<b>启动闸门</b>：<see cref="ListenAddress"/>
    /// 为非回环地址而本项为 <c>false</c> 时，配置在加载阶段就被拒绝，隧道根本不会启动。</para>
    ///
    /// <para>之所以要两个配置项而不是「填了 0.0.0.0 就等于同意」，是因为填地址与承担风险
    /// 是两件事：非回环监听会让局域网内任意机器连上本地端口，并由本客户端用你配置的密钥
    /// 替对方完成认证 —— <b>对方无需知道 aesKey 或 authKey</b>。这个后果必须被显式承认一次，
    /// 而不是从一个看起来只是「改个地址」的动作里被顺带打开。</para>
    /// </summary>
    public bool AllowNonLoopback { get; set; }
    public int LocalPort { get; set; }
    public string WsPath { get; set; } = "/ws-cryptunnel";

    /// <summary>
    /// HTTP 降级通道的基础路径，对应服务端 <c>@RequestMapping</c> 的值。
    /// 三个端点由它拼出：{HttpBasePath}/connect、/tunnel、/disconnect。
    ///
    /// <para>为什么要做成可配：服务端那三个端点是硬编码在注解里的，
    /// 改品牌名时若客户端也硬编码，两端就只能靠「同时改代码 + 同时发版」
    /// 才连得上。做成配置项后，对接旧服务端只需在 yml 里写
    /// <c>httpBasePath: /jdbc-proxy</c>，不必回滚代码。</para>
    /// </summary>
    public string HttpBasePath { get; set; } = "/cryptunnel";
    public TransportMode Mode { get; set; } = TransportMode.Auto;
    public bool AllowFallback { get; set; } = true;
    public int WsConnectMs { get; set; } = 10000;
    public int HttpConnectMs { get; set; } = 10000;
    public int HttpReadMs { get; set; } = 30000;
    public int AuthResponseMs { get; set; } = 3000;
    public int ReconnectDelayMs { get; set; } = 3000;
    public bool ReconnectEnabled { get; set; } = true;
    public int ReconnectMaxAttempts { get; set; }
    public LogLevel LogLevel { get; set; } = LogLevel.Info;
    public int ChunkSize { get; set; } = 4096;

    /// <summary>
    /// 是否启用周期性健康检查。默认 <c>false</c>。
    ///
    /// <para>关闭是默认值而非保守姿态：每次检查会在服务端真实建立并断开一条数据库连接。
    /// 开着它的客户端越多，服务端 <c>maxConnections</c> 被持续占用得越厉害 ——
    /// 诊断功能不该有能力把生产连接池吃满。</para>
    /// </summary>
    public bool HealthCheckEnabled { get; set; }

    /// <summary>
    /// 周期性健康检查的间隔（秒）。默认 300。
    ///
    /// <para>下限由 <see cref="HealthIntervalMinSec"/> 约束。间隔过短没有任何诊断价值，
    /// 只是在按频率消耗服务端连接名额 —— 隧道断了本来就有重连日志，
    /// 探活的价值在于「长时间没人用时也能提前发现服务端挂了」，那是分钟级的需求。</para>
    /// </summary>
    public int HealthIntervalSec { get; set; } = 300;

    /// <summary>周期性检查的最小间隔（秒）。</summary>
    public const int HealthIntervalMinSec = 30;

    /// <summary>周期性检查的默认间隔（秒）。</summary>
    public const int HealthIntervalDefaultSec = 300;

    /// <summary>
    /// 隧道加密算法标识，取值见 <c>Crypto.CipherRegistry.Ids</c>。
    ///
    /// <para>默认 <c>aes-256-cbc-hmac-sha256</c>，与老客户端及现有服务端部署字节级兼容 ——
    /// 老配置文件不写这一项时行为完全不变。</para>
    ///
    /// <para><b>两端必须配成同一个值</b>。报文内不含算法标识（ADR-0003 方案 B，防 DPI），
    /// 因此无法自动协商，配错的表现是服务端 <c>Auth decrypt failed</c>，
    /// 而该错误无法与 aesKey 配错相区分。</para>
    /// </summary>
    public string Cipher { get; set; } = "aes-256-cbc-hmac-sha256";
}
