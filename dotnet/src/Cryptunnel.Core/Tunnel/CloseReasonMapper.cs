namespace Cryptunnel.Core.Tunnel;

/// <summary>
/// 隧道失败原因的结构化描述。
/// </summary>
/// <param name="Message">
/// 面向用户的中文提示。经 <see cref="Stats.StatsCollector.RecordError"/> 写入
/// <see cref="Stats.ProjectStats.LastError"/>，最终显示在主界面的项目行与错误详情窗口。
/// </param>
/// <param name="Retryable">
/// 是否值得自动重连。<c>false</c> 表示配置或客户端缺陷 —— 重连只会以同样的原因再失败一次，
/// 除了刷日志没有任何作用，反而会掩盖真正的问题。
/// </param>
internal readonly record struct TunnelFailure(string Message, bool Retryable);

/// <summary>
/// 服务端 WebSocket 关闭原因 → 中文提示的映射。见《隧道运行时层实现契约》§1.2。
///
/// <para><b>为什么必须有这一层</b>：服务端认证失败时不返回任何结构化错误，只调用
/// <c>close(NOT_ACCEPTABLE, reason)</c>，reason 是一句英文短语
/// （<c>CryptunnelWebSocketHandler.java</c> 共 11 种）。这是客户端能拿到的<b>全部</b>诊断信息。
/// 若不映射，用户看到的就是 "Auth decrypt failed" 这类短语，或者更糟 ——
/// 一句 "连接失败"。而这些短语恰好对应着完全不同的排查方向。</para>
///
/// <para><b>字面量必须与服务端逐字节一致</b>。这里是精确匹配，任何一个字差异都会让该分支
/// 静默失效、落到未知分支 —— 而未知分支判定为「可重试」，于是一个本该立即停下来让用户
/// 改配置的错误，会变成无休止的重连。校验方式是拿服务端源码里的
/// <c>withReason("...")</c> 字面量做集合比对，不能只读客户端自身代码。</para>
///
/// <para><b>本类不做任何 I/O，纯查表</b>。</para>
/// </summary>
internal static class CloseReasonMapper
{
    /// <summary>
    /// 服务端认证解密失败。<b>本项目最容易误诊的一条</b>，因此单独提出来。
    ///
    /// <para>服务端在 <c>AesUtil.decrypt(payload, aesKey, hmacKey)</c> 抛异常时就走到这里
    /// （Handler:143-148），此时它<b>还没有解析出任何字段</b>，因此无从判断到底是
    /// 密钥错还是算法错 —— 两种配置错误产生完全相同的 close reason。这是
    /// ADR-0003 方案 B「报文内零算法标识」的必然代价（换来的是抗 DPI）。</para>
    ///
    /// <para>所以提示文案<b>必须同时点出两种可能</b>。只提一种会把用户按在错误的方向上
    /// 反复试 —— 尤其是 CBC 与 SM4 帧结构完全一致（同为 112 字节），算法配错时
    /// 连报文长度都看不出异常。</para>
    /// </summary>
    private const string AuthDecryptFailedMessage =
        "认证解密失败：加密算法(cipher)与服务端不一致，或 AES 密钥(aesKey)不正确。"
        + "服务端无法区分这两种情况，请同时核对两项配置 —— "
        + "注意 aes-256-cbc-hmac-sha256 与 sm4 的报文长度完全相同，算法配错时无法从现象上分辨。";

    /// <summary>
    /// 把服务端 close reason 映射为中文提示。
    ///
    /// <para>匹配用 <see cref="StringComparison.OrdinalIgnoreCase"/> 且做 Trim，
    /// 不假设服务端未来不会调整大小写或前后空格。</para>
    /// </summary>
    /// <param name="reason">
    /// <c>WebSocketReceiveResult.CloseStatusDescription</c>。可能为 <c>null</c> ——
    /// 网络层异常断开时服务端来不及发 close 帧。
    /// </param>
    internal static TunnelFailure Map(string? reason)
    {
        var key = reason?.Trim();

        if (string.IsNullOrEmpty(key))
        {
            // 没有 reason 说明不是服务端主动拒绝，而是链路断了（WAF 掐断、网络抖动、服务重启）。
            // 这类是典型可重试场景。
            return new TunnelFailure(
                "连接被关闭且未收到原因，通常是网络中断、代理/WAF 掐断或服务端重启。", true);
        }

        return key switch
        {
            // ==== 客户端缺陷：重连无意义，必须改代码 ====
            // 注意字面量含后半句 " - use encrypted auth"：服务端 Handler:73 就是这么写的。
            // 早先这里只写了 "Plain-text auth rejected"，精确匹配永远命中不了，是一条死分支。
            "Plain-text auth rejected - use encrypted auth" => new TunnelFailure(
                "认证报文未加密，被服务端拒绝。这是客户端缺陷，请反馈给维护者。", false),

            "First message must be auth" => new TunnelFailure(
                "首条消息不是认证报文，被服务端拒绝。这是客户端缺陷，请反馈给维护者。", false),

            // ==== 配置错误：重连无意义，必须改配置 ====
            "Auth format error" => new TunnelFailure(
                "认证报文格式错误。最常见原因是认证密钥(authKey)中含有冒号 ':' —— "
                + "认证报文以冒号分段，含冒号会导致服务端切分出错误的段数。", false),

            "Auth key invalid" => new TunnelFailure(
                "认证密钥(authKey)不正确，请核对配置与服务端 cryptunnel 配置是否一致。", false),

            "Auth decrypt failed" => new TunnelFailure(AuthDecryptFailedMessage, false),

            // ==== 环境问题：修正后可重试 ====
            "Auth timestamp expired" => new TunnelFailure(
                "认证时间戳超出服务端允许的时间窗，通常是本机时钟与服务器偏差过大。"
                + "请校准系统时间（建议开启自动同步）后重试。", true),

            "Auth nonce replay detected" => new TunnelFailure(
                "认证随机数被判定为重放，通常是极短时间内重复重连所致。稍后会自动重试。", true),

            // ==== 服务端侧故障：可重试 ====
            // 三条 MySQL 相关的 reason 触发点各不相同，不能合并：
            //   lost   → Handler:153，本会话要发数据时发现 socket 已失效
            //   closed → Handler:263，reader 线程读到 EOF（MySQL 主动断开）
            //   failed → 认证通过后建连失败，压根没连上
            "MySQL connection lost" => new TunnelFailure(
                "服务端与 MySQL 之间的连接已断开。可能是数据库重启、空闲超时或网络抖动。", true),

            "MySQL connection closed" => new TunnelFailure(
                "MySQL 主动关闭了连接。最常见的原因是连接空闲时间超过数据库的 wait_timeout"
                + "（MySQL 默认 8 小时），长时间不操作后再查询就会遇到。稍后会自动重连。", true),

            "MySQL connection failed" => new TunnelFailure(
                "服务端无法连接到 MySQL。请确认数据库是否可用，以及服务端白名单中的目标地址是否正确。", true),

            // ==== 服务端容量限制：可重试 ====
            // 在 afterConnectionEstablished 阶段就被拒（Handler:55），此时认证还没开始，
            // 因此与任何密钥/算法配置都无关 —— 文案要把用户从"是不是我配错了"引开。
            "Max connections exceeded" => new TunnelFailure(
                "服务端连接数已达上限，本次连接被拒绝。这与本地配置无关 —— "
                + "请关闭暂时不用的数据库会话，或联系服务端管理员调高 cryptunnel 的 maxConnections。", true),

            // ==== 未知 reason ====
            // 服务端未来新增的关闭原因会走到这里。原样带上英文短语 ——
            // 保留原文比替换成"未知错误"有用得多，用户至少能拿它去搜或来问。
            // 保守判为可重试：宁可多重连一次，也不要在服务端加了新原因后
            // 让客户端从此彻底不再重连。
            _ => new TunnelFailure($"连接被服务端关闭：{key}", true),
        };
    }
}
