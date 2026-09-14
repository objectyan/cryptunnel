using System;
using System.Security.Cryptography;

namespace Cryptunnel.Core.Crypto;

/// <summary>
/// 隧道加密算法的可插拔接口，与 Java 侧 <c>TunnelCipher</c> 一一对应。
///
/// 设计要点（详见 docs/adr/0003-pluggable-tunnel-cipher.md）：
/// <list type="bullet">
///   <item><b>报文内不含算法标识</b>：<see cref="Id"/> 仅用于本地配置匹配与日志，
///         绝不写入线上载荷——携带明文标识等于向 DPI 宣告「这是可换算法的隧道」。</item>
///   <item><b>传入原始密钥字符串</b>：密钥派生方式由各算法自行决定。</item>
///   <item><b>载荷为 string</b>：现有链路是 WebSocket 文本帧 + Base64。</item>
/// </list>
///
/// <b>兼容性红线</b>：同名算法在 Java / .NET 两端必须字节级一致，
/// 改动前后都要跑跨语言对齐验证（见 docs/replacing-the-cipher.md）。
/// </summary>
public interface ITunnelCipher
{
    /// <summary>算法标识，用于本地配置匹配与日志。该值绝不出现在传输报文中。</summary>
    string Id { get; }

    /// <summary>加密：明文 -> 线上载荷。</summary>
    string Seal(ReadOnlySpan<byte> plaintext, string rawKey);

    /// <summary>
    /// 解密：线上载荷 -> 明文。
    /// </summary>
    /// <exception cref="CryptographicException">载荷格式非法，或完整性校验未通过。</exception>
    byte[] Open(string payload, string rawKey);
}
