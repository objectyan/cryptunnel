using System;
using System.Text;

namespace Cryptunnel.Core.Health;

/// <summary>
/// 服务端推来的首个数据帧的解析结果。
/// </summary>
/// <param name="Kind">帧的种类。</param>
/// <param name="ServerVersion">
/// MySQL 版本字符串（如 <c>8.0.36</c>）。仅 <see cref="MySqlFrameKind.Handshake"/> 时有值。
/// </param>
/// <param name="ErrorCode">MySQL 错误码。仅 <see cref="MySqlFrameKind.ErrorPacket"/> 时有值。</param>
/// <param name="ErrorMessage">MySQL 错误文本。仅 <see cref="MySqlFrameKind.ErrorPacket"/> 时有值。</param>
/// <param name="Detail">面向用户的中文说明，任何 Kind 都有值。</param>
public readonly record struct MySqlFrame(
    MySqlFrameKind Kind,
    string? ServerVersion,
    int ErrorCode,
    string? ErrorMessage,
    string Detail);

public enum MySqlFrameKind
{
    /// <summary>握手初始化包（protocol version 10）。服务端已成功连上 MySQL。</summary>
    Handshake,

    /// <summary>
    /// MySQL 的 ERR 包（首字节 0xFF）。
    ///
    /// <para><b>这一类必须与「连不上」区分开</b>：服务端到 MySQL 的 TCP 是通的，
    /// 是 MySQL 自己拒绝了这条连接 —— 最典型的是
    /// <c>1130 Host 'x.x.x.x' is not allowed to connect</c>（服务端 IP 不在 MySQL 授权表里）
    /// 与 <c>1040 Too many connections</c>。两者的排查方向与「网络不通」完全相反，
    /// 混为一谈会把用户带到错误的方向上。</para>
    /// </summary>
    ErrorPacket,

    /// <summary>无法识别的字节序列。</summary>
    Unknown
}

/// <summary>
/// MySQL 首帧解析器。
///
/// <para><b>为什么要解这个包</b>：服务端认证通过后不发任何 ACK，而是直接连 MySQL
/// 并把 MySQL 的握手初始化包原样推给客户端（见 <c>CryptunnelWebSocketHandler:138-141</c>）。
/// 也就是说，<b>收到这个包本身就证明了服务端 → MySQL 这一段是通的</b> ——
/// 这是探活能白拿到的最深一层，无需服务端做任何配合。</para>
///
/// <para><b>本类只读不写，绝不发送任何 MySQL 协议包</b>。再往下（验证账号密码）
/// 需要发登录包，而账密由用户在 DBeaver 里填、客户端根本没有 ——
/// 所以探活的能力上限就到这里，这是架构决定的，不是实现偷懒。</para>
///
/// <para><b>解析必须是防御式的</b>：这段字节来自网络，长度、内容都不可信。
/// 任何越界都要返回 <see cref="MySqlFrameKind.Unknown"/> 而不是抛异常 ——
/// 探活是诊断工具，它自己崩掉就失去了全部意义。</para>
/// </summary>
public static class MySqlHandshake
{
    /// <summary>MySQL 报文头长度：3 字节负载长度（小端） + 1 字节序号。</summary>
    private const int HeaderLength = 4;

    /// <summary>握手初始化包的协议版本号。现代 MySQL / MariaDB 均为 10。</summary>
    private const byte ProtocolVersion10 = 0x0a;

    /// <summary>ERR 包的首字节标记。</summary>
    private const byte ErrPacketMarker = 0xFF;

    /// <summary>server version 字符串的长度上限，纯粹是防御畸形输入的护栏。</summary>
    private const int MaxVersionLength = 128;

    /// <summary>
    /// 解析服务端推来的首个明文数据帧。
    /// </summary>
    /// <param name="frame">已解密的首帧字节。可为空。</param>
    public static MySqlFrame Parse(ReadOnlySpan<byte> frame)
    {
        if (frame.Length == 0)
            return Unknown("服务端返回了空数据帧。");

        if (frame.Length < HeaderLength + 1)
            return Unknown($"数据帧过短（{frame.Length} 字节），不足以构成一个 MySQL 报文。");

        // 负载长度是 3 字节小端。这里读出来只用于一致性校验：
        // 对不上说明这不是 MySQL 协议流，继续往下解析毫无意义。
        int payloadLength = frame[0] | (frame[1] << 8) | (frame[2] << 16);
        int available = frame.Length - HeaderLength;
        if (payloadLength > available)
            return Unknown(
                $"MySQL 报文头声明负载 {payloadLength} 字节，实际只有 {available} 字节，报文被截断。");

        var payload = frame.Slice(HeaderLength, payloadLength);
        if (payload.Length == 0)
            return Unknown("MySQL 报文负载为空。");

        if (payload[0] == ErrPacketMarker)
            return ParseErrorPacket(payload);

        if (payload[0] == ProtocolVersion10)
            return ParseHandshakeV10(payload);

        return Unknown(
            $"无法识别的 MySQL 报文类型（首字节 0x{payload[0]:X2}），"
            + "期望握手包(0x0A)或错误包(0xFF)。");
    }

    /// <summary>
    /// 解析握手初始化包 v10：<c>[1]protocol · [NUL 结尾]server version · …</c>
    ///
    /// <para>只取到 server version 为止。后面的 thread id、能力标志位、盐值等字段
    /// 对探活没有价值，多解一个字段就多一处可能解错的地方。</para>
    /// </summary>
    private static MySqlFrame ParseHandshakeV10(ReadOnlySpan<byte> payload)
    {
        var rest = payload.Slice(1);
        int nul = rest.IndexOf((byte)0);

        if (nul < 0)
            return Unknown("握手包中的版本字符串没有 NUL 结束符，报文可能被截断。");

        if (nul == 0)
            return new MySqlFrame(MySqlFrameKind.Handshake, "(未知)", 0, null,
                "已收到 MySQL 握手包，但版本字符串为空。服务端到数据库的连接是通的。");

        if (nul > MaxVersionLength)
            return Unknown($"握手包中的版本字符串异常长（{nul} 字节），报文可疑。");

        var version = Encoding.ASCII.GetString(rest.Slice(0, nul));
        return new MySqlFrame(MySqlFrameKind.Handshake, version, 0, null,
            $"已收到 MySQL 握手包，数据库版本 {version}。");
    }

    /// <summary>
    /// 解析 ERR 包：<c>[1]0xFF · [2]错误码(小端) · [可选 6 字节 SQL State] · 错误文本</c>
    ///
    /// <para>SQL State 段以 <c>'#'</c> 开头且固定 6 字节，只在客户端声明了
    /// CLIENT_PROTOCOL_41 时出现。握手阶段的 ERR 包通常<b>没有</b>这一段
    /// （此时客户端还没来得及声明能力位），所以必须按首字节动态判断，
    /// 不能无条件跳过 6 字节 —— 那会把错误文本的前 6 个字符吃掉。</para>
    /// </summary>
    private static MySqlFrame ParseErrorPacket(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 3)
            return Unknown("MySQL 错误包过短，无法读出错误码。");

        int code = payload[1] | (payload[2] << 8);
        var rest = payload.Slice(3);

        if (rest.Length >= 6 && rest[0] == (byte)'#')
            rest = rest.Slice(6);

        var text = rest.Length > 0
            ? Encoding.UTF8.GetString(rest).TrimEnd('\0')
            : "(无错误文本)";

        return new MySqlFrame(MySqlFrameKind.ErrorPacket, null, code, text,
            $"服务端已连上 MySQL，但被 MySQL 拒绝：[{code}] {text}");
    }

    private static MySqlFrame Unknown(string detail) =>
        new(MySqlFrameKind.Unknown, null, 0, null, detail);
}
