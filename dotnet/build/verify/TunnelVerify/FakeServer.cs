using System.Net;
using System.Net.WebSockets;
using Cryptunnel.Core.Crypto;

namespace TunnelVerify;

/// <summary>服务端在认证后的行为。</summary>
internal enum ServerBehavior
{
    /// <summary>认证通过：不发 ACK，直接推 MySQL 握手包（真实服务端就是这样）。</summary>
    AcceptAndPushHandshake,

    /// <summary>认证失败：以指定 reason 关闭连接。</summary>
    RejectWithReason,

    /// <summary>收到认证后既不回应也不关闭，用于验证客户端的超时处理。</summary>
    Silent,

    /// <summary>认证后推一个二进制帧，用于验证客户端拒绝非文本帧。</summary>
    PushBinaryFrame,

    /// <summary>认证后推一个无法解密的文本帧。</summary>
    PushUndecryptable,
}

/// <summary>
/// 模拟服务端。精确复现 CryptunnelWebSocketHandler 的关键协议行为：
/// <list type="bullet">
/// <item>只处理 TextMessage</item>
/// <item>首帧必须是密文；明文 "AUTH:" 前缀直接拒绝</item>
/// <item><b>认证成功不发任何 ACK</b>，直接推 MySQL 握手包</item>
/// <item>失败时 close(NOT_ACCEPTABLE, reason)，reason 是英文短语</item>
/// </list>
///
/// <para>用真实的 <see cref="HttpListener"/> 而不是把 Tunnel 拆成可注入的抽象 ——
/// 验的就是真实 WebSocket 握手、帧类型、close 状态码这些细节，
/// 换成 mock 就把要验的东西一起 mock 掉了。</para>
/// </summary>
internal sealed class FakeServer : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly ITunnelCipher _cipher;
    private readonly string _aesKey;
    private readonly ServerBehavior _behavior;
    private readonly string _rejectReason;
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptLoop;

    /// <summary>收到的认证明文，供断言检查格式。</summary>
    internal string? ReceivedAuthPlaintext { get; private set; }

    /// <summary>认证是否成功解密。</summary>
    internal bool AuthDecrypted { get; private set; }

    /// <summary>服务端收到的数据帧解密后内容（认证帧之后的）。</summary>
    internal List<byte[]> ReceivedDataFrames { get; } = [];

    internal int Port { get; }

    internal string WsPath => "/ws-cryptunnel";

    internal FakeServer(
        ITunnelCipher cipher,
        string aesKey,
        ServerBehavior behavior = ServerBehavior.AcceptAndPushHandshake,
        string rejectReason = "Auth key invalid")
    {
        _cipher = cipher;
        _aesKey = aesKey;
        _behavior = behavior;
        _rejectReason = rejectReason;
        Port = FreePort();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
    }

    internal void Start()
    {
        _listener.Start();
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    internal string ServerUrl => $"http://127.0.0.1:{Port}";

    /// <summary>MySQL 握手包的模拟内容。真实握手包以协议版本 0x0a 开头。</summary>
    internal static readonly byte[] HandshakePacket =
        [0x0a, 0x38, 0x2e, 0x30, 0x2e, 0x33, 0x35, 0x00, 0xde, 0xad, 0xbe, 0xef];

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { break; }

            if (!ctx.Request.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = 400;
                ctx.Response.Close();
                continue;
            }

            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        WebSocket ws;
        try
        {
            var wsCtx = await ctx.AcceptWebSocketAsync(subProtocol: null);
            ws = wsCtx.WebSocket;
        }
        catch { return; }

        try
        {
            var buf = new byte[1 << 16];

            // ---- 首帧：认证 ----
            var r = await ws.ReceiveAsync(buf, _cts.Token);
            if (r.MessageType != WebSocketMessageType.Text)
            {
                await ws.CloseAsync(WebSocketCloseStatus.InvalidMessageType,
                    "First message must be auth", CancellationToken.None);
                return;
            }

            var payload = Encoding.UTF8.GetString(buf, 0, r.Count);

            // 真实服务端：明文 AUTH: 前缀直接拒绝。
            if (payload.StartsWith("AUTH:", StringComparison.Ordinal))
            {
                await ws.CloseAsync(WebSocketCloseStatus.InvalidPayloadData,
                    "Plain-text auth rejected - use encrypted auth", CancellationToken.None);
                return;
            }

            try
            {
                ReceivedAuthPlaintext = Encoding.UTF8.GetString(_cipher.Open(payload, _aesKey));
                AuthDecrypted = true;
            }
            catch
            {
                await ws.CloseAsync(WebSocketCloseStatus.InvalidPayloadData,
                    "Auth decrypt failed", CancellationToken.None);
                return;
            }

            switch (_behavior)
            {
                case ServerBehavior.RejectWithReason:
                    await ws.CloseAsync(WebSocketCloseStatus.InvalidPayloadData,
                        _rejectReason, CancellationToken.None);
                    return;

                case ServerBehavior.Silent:
                    // 什么都不做，让客户端超时。
                    await Task.Delay(Timeout.Infinite, _cts.Token);
                    return;

                case ServerBehavior.PushBinaryFrame:
                    await ws.SendAsync(new byte[] { 1, 2, 3, 4 },
                        WebSocketMessageType.Binary, true, CancellationToken.None);
                    break;

                case ServerBehavior.PushUndecryptable:
                    await ws.SendAsync(Encoding.UTF8.GetBytes("not-a-valid-ciphertext"),
                        WebSocketMessageType.Text, true, CancellationToken.None);
                    break;

                case ServerBehavior.AcceptAndPushHandshake:
                default:
                    // 关键：认证成功不发 ACK，直接推握手包。
                    var sealed_ = _cipher.Seal(HandshakePacket, _aesKey);
                    await ws.SendAsync(Encoding.UTF8.GetBytes(sealed_),
                        WebSocketMessageType.Text, true, CancellationToken.None);
                    break;
            }

            // ---- 后续：回显收到的数据帧 ----
            while (ws.State == WebSocketState.Open && !_cts.IsCancellationRequested)
            {
                var dr = await ws.ReceiveAsync(buf, _cts.Token);
                if (dr.MessageType == WebSocketMessageType.Close) break;
                if (dr.MessageType != WebSocketMessageType.Text) continue;

                var body = Encoding.UTF8.GetString(buf, 0, dr.Count);
                try
                {
                    var plain = _cipher.Open(body, _aesKey);
                    lock (ReceivedDataFrames) ReceivedDataFrames.Add(plain);

                    // 回显（前缀 0xFF 便于区分方向）
                    var echo = new byte[plain.Length + 1];
                    echo[0] = 0xFF;
                    plain.CopyTo(echo, 1);
                    await ws.SendAsync(Encoding.UTF8.GetBytes(_cipher.Seal(echo, _aesKey)),
                        WebSocketMessageType.Text, true, CancellationToken.None);
                }
                catch
                {
                    await ws.CloseAsync(WebSocketCloseStatus.InvalidPayloadData,
                        "Auth decrypt failed", CancellationToken.None);
                    break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch { }
        finally
        {
            try { ws.Dispose(); } catch { }
        }
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public async ValueTask DisposeAsync()
    {
        try { _cts.Cancel(); } catch { }
        try { _listener.Stop(); } catch { }
        try { _listener.Close(); } catch { }
        if (_acceptLoop != null)
        {
            try { await _acceptLoop.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        }
        _cts.Dispose();
    }
}
