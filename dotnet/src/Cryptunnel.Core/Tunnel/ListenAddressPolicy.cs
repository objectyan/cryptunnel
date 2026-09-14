using System.Net;

namespace Cryptunnel.Core.Tunnel;

/// <summary>
/// 本地监听地址的安全策略：判定回环、以及非回环监听是否被显式允许。
///
/// <para><b>为什么单独成类</b>：这套判定有两个调用方 —— 配置加载期的启动闸门
/// （<c>ConfigLoader</c>，决定配置是否被接受）与运行期的安全告警
/// （<c>TunnelManager</c>，决定日志里打不打警示）。两处若各写一份，
/// 迟早会出现「加载期放行、运行期不告警」或反过来的漂移，
/// 而这种漂移的后果是<b>一个无人知晓的开放端口</b>。判定只能有一份。</para>
/// </summary>
internal static class ListenAddressPolicy
{
    /// <summary>
    /// 判断是否为回环地址。
    ///
    /// <para>不能只比对 <c>"127.0.0.1"</c> 字符串：整个 <c>127.0.0.0/8</c> 段都是回环
    /// （<c>127.0.0.5</c> 同样只有本机可达），而 <c>0.0.0.0</c> / <c>::</c> / <c>*</c> /
    /// <c>+</c> 这些通配写法都表示监听全部网卡，必须判为非回环。</para>
    ///
    /// <para>空值判为<b>非</b>回环：空地址会走到 <c>IPAddress.Parse</c> 抛异常，
    /// 与其在这里假定它安全，不如让它落进需要显式许可的那一侧。</para>
    /// </summary>
    internal static bool IsLoopback(string? address)
    {
        var addr = address?.Trim();
        if (string.IsNullOrEmpty(addr)) return false;

        // 通配写法一律视为非回环。
        if (addr is "0.0.0.0" or "::" or "*" or "+" or "[::]") return false;

        return IPAddress.TryParse(addr.Trim('[', ']'), out var ip)
               && IPAddress.IsLoopback(ip);
    }

    /// <summary>
    /// 校验监听地址与许可开关的组合是否可以启动。
    /// 返回 <c>null</c> 表示通过，否则返回面向用户的错误说明。
    ///
    /// <para><b>为什么要两个配置项</b>：填地址与承担风险是两件事。非回环监听会让局域网内
    /// 任意机器连上本地端口，并由本客户端用已配置的密钥替对方完成认证 ——
    /// 对方<b>无需知道 aesKey 或 authKey</b>。这个后果必须被显式承认一次，
    /// 而不是从一个看起来只是「改个地址」的动作里被顺带打开。</para>
    ///
    /// <para><b>为什么在加载期拦而不是启动期</b>：加载期失败会让整个项目配置被拒绝并列出原因，
    /// 用户在界面上直接看到；若拖到 <c>TcpListener.Start()</c> 才拦，端口已经进入
    /// 半初始化状态，错误也只会出现在日志流里。</para>
    /// </summary>
    internal static string? Validate(string? address, bool allowNonLoopback, int port)
    {
        if (IsLoopback(address) || allowNonLoopback) return null;

        return $"local.address=\"{address}\" 不是回环地址，局域网内的其它机器可以连接本机 "
             + $"{port} 端口并通过本隧道访问内网数据库，且对方无需知道 aesKey 或 authKey"
             + "（本客户端会用你配置的密钥替对方完成认证）。"
             + "若确需跨机共享，请显式加上 local.allowNonLoopback: true；"
             + "否则请把 local.address 改回 127.0.0.1。";
    }
}
