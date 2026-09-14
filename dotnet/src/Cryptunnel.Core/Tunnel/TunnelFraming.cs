using System;

namespace Cryptunnel.Core.Tunnel;

/// <summary>
/// 帧大小不变量的唯一守卫处。见 ADR-0001 与《隧道运行时层实现契约》§2。
///
/// <para><b>为什么需要它</b>：加密后的载荷是 Base64 文本，长度约为明文的 1.36 倍再加算法头。
/// 接收端（Tomcat）的 <c>maxTextMessageBufferSize</c> 默认 8192，超限会直接
/// WS 1009 CLOSE_TOO_BIG —— 表现给用户是 DBeaver 报 <c>08S01 Communications link failure</c>，
/// 与真正的网络故障无法区分。ADR-0001 为此付出了两轮线上故障的代价。</para>
///
/// <para><b>它不是"防越界"，而是"防未来"</b>：当前三种算法在 ChunkSize=4096 下载荷都在
/// 5.5KB 上下，余量充足。断言的意义在于——如果日后有人调大 ChunkSize，或换入膨胀率
/// 更高的算法，故障必须在开发期以异常的形式炸出来，而不是留到用户的 DBeaver 上。</para>
///
/// <para>本类<b>不碰网络</b>，只做纯计算与断言。</para>
/// </summary>
internal static class TunnelFraming
{
    /// <summary>
    /// 单个文本帧载荷的字符数上限，取 Tomcat <c>maxTextMessageBufferSize</c> 的默认值。
    ///
    /// <para><b>刻意与 <see cref="Models.TunnelConfig.ChunkSize"/> 分离定义</b>：
    /// 写成 <c>ChunkSize * 2</c> 之类的表达式会制造隐式耦合 —— 调大 ChunkSize 时上限
    /// 跟着变大，断言就永远不会触发，保险丝等于被短路。这个值属于<b>对端的能力</b>，
    /// 与本端的读缓冲大小在概念上无关，必须各自独立。</para>
    /// </summary>
    internal const int MaxFramePayloadChars = 8192;

    /// <summary>
    /// 允许的最大明文分片长度，由 <see cref="MaxFramePayloadChars"/> 反推。
    ///
    /// <para>推导（CBC 族膨胀率最高，故以它为准）：明文 PKCS7 补齐到 16 字节边界，
    /// 再加 IV(16)+HMAC(32)，最后 Base64 膨胀 4/3：
    /// <c>ceil((48 + pad16(n)) / 3) * 4 &lt;= 8192</c>。
    /// 实算的关键点位：
    /// <c>n=4096</c> → 5528 字符；<c>n=6000</c> → 8088 字符；
    /// <c>n=6095</c> → 8192 字符（<b>恰好等于上限，仍合法</b>）；
    /// <c>n=6096</c> → 8216 字符（<b>首个越界值</b>，注意 6096 是 16 的整数倍，
    /// PKCS7 会额外补满一整块，故跨度比直觉大）。
    /// 因此安全上界是 6095，这里取 6000 留出余量。</para>
    ///
    /// <para>用途：配置加载后可提前校验 ChunkSize，把"运行到一半才炸"提前成"启动就报错"。</para>
    /// </summary>
    internal const int MaxPlaintextChunkBytes = 6000;

    /// <summary>
    /// 发送前的强制断言。<b>必须在发送函数的唯一入口处调用</b>，不得散落到各调用点 ——
    /// 散落意味着新增的发送路径会漏掉检查，而漏掉的那条路径正是会出事的那条。
    /// </summary>
    /// <param name="payload">即将写入文本帧的完整载荷（已加密、已 Base64）。</param>
    /// <param name="plaintextLength">该载荷对应的明文字节数，仅用于组装诊断信息。</param>
    /// <param name="cipherId">算法标识。本地配置标识，非机密，可安全出现在异常与日志中。</param>
    /// <exception cref="TunnelFrameTooLargeException">
    /// 载荷超过 <see cref="MaxFramePayloadChars"/>。这是<b>客户端 bug</b>（分片逻辑或
    /// 配置校验失职），不是可重试的运行时错误，因此用异常而非返回值表达。
    /// </exception>
    internal static void EnsureWithinFrameLimit(string payload, int plaintextLength, string cipherId)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (payload.Length <= MaxFramePayloadChars)
            return;

        throw new TunnelFrameTooLargeException(
            payload.Length, plaintextLength, cipherId);
    }

    /// <summary>
    /// 校验 ChunkSize 是否落在安全区间。配置或隧道启动阶段调用，
    /// 把潜在的帧超限从"传大结果集时随机断线"提前暴露成"启动即报错"。
    /// </summary>
    /// <returns>不安全时返回中文原因；安全时返回 <c>null</c>。</returns>
    internal static string? ValidateChunkSize(int chunkSize)
    {
        if (chunkSize <= 0)
            return $"分片大小(chunkSize) 必须为正数，当前为 {chunkSize}。";

        if (chunkSize > MaxPlaintextChunkBytes)
            return $"分片大小(chunkSize) {chunkSize} 字节过大：加密并 Base64 后可能超过接收端单帧上限 "
                 + $"{MaxFramePayloadChars} 字符，会触发 WebSocket 1009 断线。请调整为不超过 "
                 + $"{MaxPlaintextChunkBytes}（推荐保留默认值 4096）。";

        return null;
    }
}

/// <summary>
/// 帧载荷超过接收端单帧上限。<b>语义是客户端 bug，不是运行时故障</b> ——
/// 因此不参与重连退避，捕获方应当直接终止该隧道并把完整诊断信息写入日志。
/// </summary>
internal sealed class TunnelFrameTooLargeException : InvalidOperationException
{
    internal TunnelFrameTooLargeException(int payloadChars, int plaintextLength, string cipherId)
        : base($"帧载荷 {payloadChars} 字符超过上限 {TunnelFraming.MaxFramePayloadChars}"
             + $"（明文 {plaintextLength} 字节，算法 {cipherId}）。"
             + "这是客户端分片逻辑或 chunkSize 配置的缺陷，继续发送必然触发对端 1009 断连。")
    {
        PayloadChars = payloadChars;
        PlaintextLength = plaintextLength;
        CipherId = cipherId;
    }

    internal int PayloadChars { get; }

    internal int PlaintextLength { get; }

    /// <summary>算法标识。本地配置标识，非机密（ADR-0003：标识不上线，但可用于本地诊断）。</summary>
    internal string CipherId { get; }
}
