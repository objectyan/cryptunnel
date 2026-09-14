using System;
using System.Security.Cryptography;
using System.Text;
using Cryptunnel.Core.Crypto;

namespace Cryptunnel.Core.Tunnel;

/// <summary>
/// 认证报文明文的生成器。必须与服务端 <c>CryptunnelWebSocketHandler.handleAuth</c> 的
/// 内联解析逻辑字节级对齐（校验链：段数 &gt;= 4 → authKey 相等 → 时间窗 → nonce 重放）。
///
/// <para>格式（4 段 / 5 段）：</para>
/// <code>
/// AUTH:{authKey}:{unixSeconds}:{nonce}
/// AUTH:{authKey}:{unixSeconds}:{nonce}:{targetId}
/// </code>
///
/// <para><b>职责边界</b>：本类只生成<b>明文</b>，不碰网络、不碰加密。
/// 加密由调用方交给 <see cref="ITunnelCipher.Seal"/> 完成 ——
/// 服务端对明文 <c>AUTH:</c> 前缀是直接拒绝的（历史版本曾允许，现已收紧）。</para>
/// </summary>
internal static class AuthMessageBuilder
{
    /// <summary>nonce 的字节长度。服务端只做字符串相等比较，长度取决于客户端，16 字节与老客户端一致。</summary>
    private const int NonceBytes = 16;

    /// <summary>
    /// 生成认证报文明文。
    /// </summary>
    /// <param name="authKey">认证密钥。<b>不得包含冒号</b>，否则服务端按 ":" 切分会多出段数。</param>
    /// <param name="targetId">
    /// 具名 target 标识。为 <c>null</c>/空白时生成 4 段格式，服务端按缺省 target 路由。
    /// </param>
    /// <exception cref="ArgumentException">
    /// authKey 为空，或 authKey / targetId 含冒号。
    /// <para>这里选择<b>硬失败而非静默容忍</b>：含冒号的 authKey 会让服务端解析出错误的段数，
    /// 表现为 <c>Auth format error</c>。在客户端提前拦住，用户能立刻知道该改配置的哪一项，
    /// 而不是拿着一句服务端英文短语去猜。</para>
    /// </exception>
    internal static string Build(string authKey, string? targetId = null)
    {
        if (string.IsNullOrEmpty(authKey))
            throw new ArgumentException("认证密钥(authKey) 不能为空。", nameof(authKey));

        if (authKey.Contains(':', StringComparison.Ordinal))
            throw new ArgumentException(
                "认证密钥(authKey) 不能包含冒号 ':' —— 认证报文以冒号分段，含冒号会导致服务端解析失败"
                + "（表现为 Auth format error）。请修改配置中的 authKey。",
                nameof(authKey));

        var hasTarget = !string.IsNullOrWhiteSpace(targetId);
        if (hasTarget && targetId!.Contains(':', StringComparison.Ordinal))
            throw new ArgumentException(
                "目标标识(targetId) 不能包含冒号 ':' —— 同上，会导致服务端认证报文分段错误。",
                nameof(targetId));

        // 与 Java 侧 System.currentTimeMillis()/1000 对齐，服务端用 |now-ts| 与时间窗比较。
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var nonce = GenerateNonce();

        var builder = new StringBuilder(96);
        builder.Append("AUTH:").Append(authKey)
               .Append(':').Append(timestamp)
               .Append(':').Append(nonce);

        if (hasTarget)
            builder.Append(':').Append(targetId);

        return builder.ToString();
    }

    /// <summary>
    /// 生成 nonce：16 字节随机数的<b>小写</b>十六进制（32 字符）。
    ///
    /// <para><b>随机源必须是密码学安全的</b>。<see cref="RandomNumberGenerator"/> 而非
    /// <see cref="Random"/> —— nonce 是服务端防重放的唯一凭据，可预测的 nonce 等于
    /// 攻击者能预先占用 nonce 让合法客户端被判为重放（拒绝服务），或在抓到密文后构造重放窗口。</para>
    ///
    /// <para>大小写必须是小写：Java 侧用 <c>String.format("%02x")</c>，服务端 nonce 缓存做
    /// 字符串相等比较。大写会让同一随机值被视为不同 nonce —— 不影响功能但破坏跨语言一致性，
    /// 属于埋雷。</para>
    /// </summary>
    internal static string GenerateNonce()
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        return Convert.ToHexString(nonce).ToLowerInvariant();
    }

    /// <summary>
    /// 生成并加密认证报文，返回可直接写入线路的载荷。
    ///
    /// <para>把"生成 + 加密"收在一处，是为了让所有认证发起点（WS 认证、HTTP connect、
    /// HTTP disconnect）共用同一条路径 —— 三处各自拼装的话，任何一处漏掉加密都会被
    /// 服务端以 <c>Plain-text auth rejected</c> 拒绝，而这类 bug 只在改动后才暴露。</para>
    /// </summary>
    /// <param name="cipher">隧道加密算法，由调用方按 <c>TunnelConfig.Cipher</c> 从注册表取得。</param>
    /// <param name="authKey">认证密钥。</param>
    /// <param name="aesKey">加密原始密钥（由算法内部派生）。</param>
    /// <param name="targetId">可选具名 target。</param>
    internal static string BuildSealed(
        ITunnelCipher cipher, string authKey, string aesKey, string? targetId = null)
    {
        ArgumentNullException.ThrowIfNull(cipher);

        if (string.IsNullOrEmpty(aesKey))
            throw new ArgumentException("加密密钥(aesKey) 不能为空。", nameof(aesKey));

        var plaintext = Build(authKey, targetId);

        // 明文只在本方法栈内存活，绝不写日志（契约 §5：密钥/明文严禁落日志）。
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        try
        {
            return cipher.Seal(bytes, aesKey);
        }
        finally
        {
            // 认证明文含 authKey，尽早从托管堆上抹掉，缩小被内存转储捕获的窗口。
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    /// <summary>
    /// 密钥的可安全打印形式：仅前 6 字符 + 省略号，与老 Java 客户端行为一致。
    /// <para>用于启动日志确认"配置读对了没"，同时不泄漏完整密钥。</para>
    /// </summary>
    internal static string MaskKey(string? key)
    {
        if (string.IsNullOrEmpty(key))
            return "(未配置)";

        return key.Length <= 6 ? "***" : string.Concat(key.AsSpan(0, 6), "...");
    }
}
