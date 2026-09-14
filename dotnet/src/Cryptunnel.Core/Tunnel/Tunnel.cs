using System;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Cryptunnel.Core.Crypto;
using Cryptunnel.Core.Logging;
using Cryptunnel.Core.Models;
using Cryptunnel.Core.Stats;

namespace Cryptunnel.Core.Tunnel;

public enum TunnelState
{
    Stopped,
    Listening,
    WsConnecting,
    WsConnected,
    HttpConnecting,
    HttpConnected,
    Error
}

public sealed class Tunnel : IDisposable
{
    private readonly TunnelConfig _cfg;
    private readonly ITunnelCipher _cipher;
    private readonly ILogger _logger;
    private readonly ITunnelTap _tap;
    private readonly StatsCollector _stats;
    private readonly HttpClient _http;

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private int _running;

    public string Name => _cfg.Name;
    public TunnelConfig Config => _cfg;

    public event Action<string, TunnelState>? StateChanged;
    public event Action<string>? FatalError; // 端口占用等无法启动的致命错误

    public Tunnel(TunnelConfig cfg, ILogger logger, ITunnelTap tap, StatsCollector stats)
    {
        _cfg = cfg;

        // 加密算法由配置决定，两端必须约定一致 —— 报文内不携带任何算法标识
        // （ADR-0003 方案 B，为的是不给 DPI 设备留特征）。因此算法配错时无法自动协商，
        // 表现为服务端 "Auth decrypt failed"，且该错误无法与 aesKey 配错相区分。
        //
        // 注意：AES/HMAC 密钥都由 aesKey 派生而非 authKey，由各 cipher 内部完成。
        // authKey 仅作为认证报文的明文内容，不参与密钥派生 —— 服务端按同样约定验签。
        _cipher = CipherRegistry.Get(cfg.Cipher);

        _logger = logger;
        _tap = tap;
        _stats = stats;
        _http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(cfg.HttpReadMs), BaseAddress = null };
    }

    public void Start()
    {
        if (Interlocked.Exchange(ref _running, 1) == 1) return;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => AcceptLoop(_cts.Token));
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        _running = 0;
        StateChanged?.Invoke(Name, TunnelState.Stopped);
    }

    private async Task AcceptLoop(CancellationToken ct)
    {
        try
        {
            _listener = new TcpListener(IPAddress.Parse(_cfg.ListenAddress), _cfg.LocalPort);
            _listener.Start();
        }
        catch (Exception ex)
        {
            _logger.Log(LogLevel.Error, Name, $"监听 {_cfg.ListenAddress}:{_cfg.LocalPort} 失败：{ex.Message}");
            _stats.RecordError(Name, $"监听失败: {ex.Message}");
            StateChanged?.Invoke(Name, TunnelState.Error);
            FatalError?.Invoke(Name);
            return;
        }

        _logger.Log(LogLevel.Info, Name, $"监听 localhost:{_cfg.LocalPort}，DBeaver 请连接 localhost:{_cfg.LocalPort}");
        StateChanged?.Invoke(Name, TunnelState.Listening);

        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                break;
            }

            _logger.Log(LogLevel.Info, Name, "DBeaver 已连接，建立隧道…");
            _ = Task.Run(() => HandleConnection(client, ct));
        }
    }

    private async Task HandleConnection(TcpClient client, CancellationToken ct)
    {
        try
        {
            bool ok = false;
            if (_cfg.Mode != TransportMode.Http)
            {
                ok = await TryWebSocketMode(client, ct);
            }
            if (!ok && _cfg.Mode != TransportMode.WebSocket && _cfg.AllowFallback)
            {
                ok = await TryHttpMode(client, ct);
            }
            if (!ok)
            {
                _logger.Log(LogLevel.Error, Name, "隧道建立失败，请检查服务器地址与网络");
                _stats.RecordError(Name, "隧道建立失败");
                StateChanged?.Invoke(Name, TunnelState.Error);
            }
        }
        finally
        {
            try { client.Close(); } catch { }
            _stats.OnDisconnect(Name);
            // 连接结束后回到监听状态（仍运行时）；停止/取消时 _running=0，不再改回 Listening
            if (_running == 1) StateChanged?.Invoke(Name, TunnelState.Listening);
        }
    }

    // =========================================================
    // WebSocket 主模式
    // =========================================================
    private async Task<bool> TryWebSocketMode(TcpClient client, CancellationToken ct)
    {
        var wsUrl = _cfg.ServerUrl.Replace("http://", "ws://").Replace("https://", "wss://") + _cfg.WsPath;
        StateChanged?.Invoke(Name, TunnelState.WsConnecting);

        using var ws = new ClientWebSocket();
        var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectCts.CancelAfter(_cfg.WsConnectMs);

        try
        {
            await ws.ConnectAsync(new Uri(wsUrl), connectCts.Token);
        }
        catch (Exception ex)
        {
            _logger.Log(LogLevel.Warn, Name, $"WebSocket 连接失败（可能被 WAF 拦截）：{ex.Message}");
            return false;
        }
        finally
        {
            connectCts.Dispose();
        }

        if (ws.State != WebSocketState.Open) return false;

        // 加密认证。首帧必须是密文 —— 服务端对明文 "AUTH:" 前缀直接拒绝。
        var auth = AuthMessageBuilder.BuildSealed(_cipher, _cfg.AuthKey, _cfg.AesKey);
        if (!await SendFrameAsync(ws, auth, "认证报文", CancellationToken.None)) return false;

        // 等待服务端的真实响应，而不是「睡一会儿看连接还在不在」。
        var (authOk, firstFrame) = await AwaitAuthResultAsync(ws, ct);
        if (!authOk) return false;

        _logger.Log(LogLevel.Info, Name, "WebSocket 加密认证成功");
        _stats.OnConnect(Name, "WebSocket");
        _tap.OnState(Name, "WS");
        StateChanged?.Invoke(Name, TunnelState.WsConnected);

        using var stream = client.GetStream();

        // 认证成功的信号就是服务端推来的 MySQL 握手包本身，它必须交给 DBeaver。
        // 丢掉它 DBeaver 会一直等握手而超时。
        if (firstFrame is { Length: > 0 })
        {
            _tap.OnBytes(Name, Direction.In, firstFrame.AsSpan());
            try
            {
                await stream.WriteAsync(firstFrame, ct);
                _stats.RecordBytes(Name, Direction.In, firstFrame.Length);
            }
            catch (Exception ex)
            {
                _logger.Log(LogLevel.Warn, Name, $"写入握手包失败：{ex.Message}");
                return true;
            }
        }

        var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var recvTask = WsReceiveLoop(ws, stream, pumpCts.Token);
        var sendTask = LocalToWsLoop(client, stream, ws, pumpCts.Token);
        await Task.WhenAny(recvTask, sendTask);
        pumpCts.Cancel();
        try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch { }
        return true;
    }

    /// <summary>
    /// 等待并判定认证结果。
    ///
    /// <para><b>为什么不能用 <c>Task.Delay</c> 判定</b>：早先的实现是「发完认证睡
    /// <c>AuthResponseMs</c>，连接还开着就算成功」。它在理想网络下碰巧能工作，
    /// 但有三个真实的失效模式：
    /// (1) 服务端的 close 帧晚于超时时间到达 —— 客户端已宣布认证成功并进入转发循环，
    ///     DBeaver 拿到的是一条已死的连接；
    /// (2) 每条连接都固定阻塞整个超时时长，而认证本可以在几十毫秒内确认；
    /// (3) close reason 被整个丢弃 —— 服务端区分了 11 种失败原因，全被压成一个
    ///     「连接还开着吗」的布尔值，用户永远只看到「隧道建立失败」。</para>
    ///
    /// <para><b>成功的信号是第一个数据帧</b>：服务端认证通过后<b>不发任何 ACK</b>
    /// （见 CryptunnelWebSocketHandler:138-141），而是直接打开 MySQL 连接、
    /// 由 reader 线程把 MySQL 握手包推过来。所以收到数据 = 认证已过。
    /// 这个首帧是 MySQL 协议的一部分，<b>必须回传给调用方</b>转发给 DBeaver。</para>
    /// </summary>
    /// <returns>
    /// <c>Ok</c> 为是否认证成功；<c>FirstFrame</c> 是随认证成功一并收到的首个
    /// 明文数据帧（MySQL 握手包），失败时为 <c>null</c>。
    /// </returns>
    private async Task<(bool Ok, byte[]? FirstFrame)> AwaitAuthResultAsync(
        ClientWebSocket ws, CancellationToken ct)
    {
        using var authCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        authCts.CancelAfter(_cfg.AuthResponseMs);

        using var ms = new MemoryStream();
        var buf = new byte[1 << 16];

        try
        {
            while (true)
            {
                var r = await ws.ReceiveAsync(buf, authCts.Token);

                if (r.MessageType == WebSocketMessageType.Close)
                {
                    // 这里是整个诊断链路的关键：把服务端那句英文短语翻译成可操作的中文。
                    var failure = CloseReasonMapper.Map(ws.CloseStatusDescription);
                    _logger.Log(LogLevel.Error, Name, $"认证失败：{failure.Message}");
                    _stats.RecordError(Name, failure.Message);

                    if (!failure.Retryable)
                        _logger.Log(LogLevel.Error, Name,
                            "该错误重连也不会成功，请先按上述提示修改配置。");

                    return (false, null);
                }

                if (r.MessageType == WebSocketMessageType.Binary)
                {
                    // 服务端只发 TextMessage（handleBinaryMessage 仅打 warn 不处理）。
                    // 收到 Binary 说明对端不是预期的服务端 —— 可能是中间设备伪造响应。
                    _logger.Log(LogLevel.Error, Name,
                        "认证阶段收到二进制帧，但服务端只使用文本帧。"
                        + "可能连接到了非预期的服务，或中间有设备改写了流量。");
                    _stats.RecordError(Name, "认证响应帧类型异常（收到二进制帧）");
                    return (false, null);
                }

                ms.Write(buf, 0, r.Count);
                if (!r.EndOfMessage) continue;

                var text = Encoding.UTF8.GetString(ms.ToArray());
                ms.SetLength(0);

                byte[] plain;
                try
                {
                    plain = _cipher.Open(text, _cfg.AesKey);
                }
                catch (Exception ex)
                {
                    // 认证已过但首帧解不开，说明双方密钥/算法不一致却恰好通过了服务端校验，
                    // 或链路上有改写。继续用这条隧道只会把错误数据送进 DBeaver。
                    _logger.Log(LogLevel.Error, Name,
                        $"认证后首个数据帧解密失败：{ex.Message}。"
                        + $"请核对加密算法(cipher={_cipher.Id})与密钥(aesKey)是否与服务端一致。");
                    _stats.RecordError(Name, "认证后首帧解密失败");
                    return (false, null);
                }

                return (true, plain);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // 超时。服务端既没拒绝也没推握手包。
            _logger.Log(LogLevel.Error, Name,
                $"认证超时（{_cfg.AuthResponseMs}ms 内未收到服务端响应）。"
                + "服务端可能无法连接到目标数据库，或网络存在中间设备缓冲。");
            _stats.RecordError(Name, "认证超时");
            return (false, null);
        }
        catch (OperationCanceledException)
        {
            return (false, null);
        }
        catch (Exception ex)
        {
            _logger.Log(LogLevel.Error, Name, $"认证过程中连接异常：{ex.Message}");
            _stats.RecordError(Name, $"认证异常: {ex.Message}");
            return (false, null);
        }
    }

    /// <summary>
    /// WebSocket 发送的<b>唯一入口</b>。所有出站帧都必须经过这里。
    ///
    /// <para><b>为什么要收成单一入口</b>：帧上限检查散落到各调用点，意味着任何新增的
    /// 发送路径都可能漏掉检查 —— 而漏掉的那条，正好就是将来会出事的那条。
    /// 收在这里之后，「发送」和「检查帧上限」在物理上无法分开。</para>
    ///
    /// <para>超限时<b>不发送、不重试</b>：这属于客户端 bug（chunkSize 配置校验本应在
    /// 加载阶段就拦住），发出去的结果是对端 WS 1009 CLOSE_TOO_BIG，
    /// 用户看到 DBeaver 报「08S01 Communications link failure」，
    /// 与真正的网络故障无法区分。ADR-0001 为此付过两轮线上故障的代价。</para>
    /// </summary>
    /// <param name="purpose">用于日志的用途描述，例如「认证报文」「数据帧」。</param>
    /// <returns>发送成功返回 <c>true</c>；超限或发送异常返回 <c>false</c>（调用方应终止本次连接）。</returns>
    private async Task<bool> SendFrameAsync(
        ClientWebSocket ws, string payload, string purpose, CancellationToken ct, int plaintextLength = 0)
    {
        try
        {
            TunnelFraming.EnsureWithinFrameLimit(payload, plaintextLength, _cipher.Id);
        }
        catch (TunnelFrameTooLargeException ex)
        {
            _logger.Log(LogLevel.Error, Name, $"{purpose}超出帧上限，已中止发送：{ex.Message}");
            _stats.RecordError(Name, $"{purpose}超出帧上限");
            return false;
        }

        try
        {
            await ws.SendAsync(Encoding.UTF8.GetBytes(payload), WebSocketMessageType.Text, true, ct);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger.Log(LogLevel.Warn, Name, $"发送{purpose}失败：{ex.Message}");
            return false;
        }
    }

    private async Task WsReceiveLoop(ClientWebSocket ws, NetworkStream stream, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buf = new byte[1 << 16];
        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            WebSocketReceiveResult r;
            try { r = await ws.ReceiveAsync(buf, ct); }
            catch { break; }

            if (r.MessageType == WebSocketMessageType.Close)
            {
                // 连接中途被服务端关闭，同样要把 reason 翻译出来。
                // 最常见的是 MySQL connection closed（wait_timeout 空闲断开）。
                var failure = CloseReasonMapper.Map(ws.CloseStatusDescription);
                _logger.Log(failure.Retryable ? LogLevel.Warn : LogLevel.Error, Name,
                    $"连接已关闭：{failure.Message}");
                _stats.RecordError(Name, failure.Message);
                break;
            }

            if (r.MessageType == WebSocketMessageType.Binary)
            {
                // 服务端只处理/发送 TextMessage。收到二进制帧说明对端行为不符合协议约定。
                // 不能按文本去解，那会得到乱码并走进解密失败分支，掩盖真正的问题。
                _logger.Log(LogLevel.Error, Name,
                    "收到二进制帧，但本协议只使用文本帧。连接可能被中间设备改写，已断开。");
                _stats.RecordError(Name, "收到非预期的二进制帧");
                break;
            }

            ms.Write(buf, 0, r.Count);
            if (!r.EndOfMessage) continue;

            var text = Encoding.UTF8.GetString(ms.ToArray());
            ms.SetLength(0);

            byte[] decrypted;
            try { decrypted = _cipher.Open(text, _cfg.AesKey); }
            catch (Exception ex)
            {
                // 必须断开而不是 continue。
                // MySQL 协议是有状态的字节流，丢掉一帧之后所有后续包都会错位，
                // DBeaver 那边表现为各种离奇的协议错误，排查方向会被彻底带偏。
                // 断开让重连逻辑接手，是唯一能回到一致状态的做法。
                _logger.Log(LogLevel.Error, Name,
                    $"数据帧解密失败，已断开连接：{ex.Message}。"
                    + "继续传输会导致 MySQL 协议流错位。");
                _stats.RecordError(Name, $"解密失败: {ex.Message}");
                break;
            }

            _tap.OnBytes(Name, Direction.In, decrypted.AsSpan());
            try { await stream.WriteAsync(decrypted, ct); _stats.RecordBytes(Name, Direction.In, decrypted.Length); }
            catch { break; }
        }
    }

    private async Task LocalToWsLoop(TcpClient client, NetworkStream stream, ClientWebSocket ws, CancellationToken ct)
    {
        var buf = new byte[_cfg.ChunkSize];
        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open && client.Connected)
        {
            int n;
            try { n = await stream.ReadAsync(buf, ct); }
            catch { break; }
            if (n <= 0) break;

            var chunk = new byte[n];
            Buffer.BlockCopy(buf, 0, chunk, 0, n);
            _tap.OnBytes(Name, Direction.Out, chunk);

            string enc;
            try { enc = _cipher.Seal(chunk, _cfg.AesKey); }
            catch (Exception ex)
            {
                // 与接收侧同理：跳过这一片会让 MySQL 协议流错位。
                _logger.Log(LogLevel.Error, Name,
                    $"数据帧加密失败，已断开连接：{ex.Message}");
                _stats.RecordError(Name, $"加密失败: {ex.Message}");
                break;
            }

            if (!await SendFrameAsync(ws, enc, "数据帧", ct, plaintextLength: n)) break;
            _stats.RecordBytes(Name, Direction.Out, n);
        }
    }

    // =========================================================
    // HTTP 长轮询降级模式
    // =========================================================
    private async Task<bool> TryHttpMode(TcpClient client, CancellationToken ct)
    {
        StateChanged?.Invoke(Name, TunnelState.HttpConnecting);
        _logger.Log(LogLevel.Info, Name, "降级到 HTTP 长轮询模式…");

        try
        {
            var auth = AuthMessageBuilder.BuildSealed(_cipher, _cfg.AuthKey, _cfg.AesKey);
            var connectResp = await PostAsync(_cfg.ServerUrl + _cfg.HttpBasePath + "/connect", auth, null, ct);
            if (string.IsNullOrEmpty(connectResp) || connectResp.StartsWith("4"))
            {
                _logger.Log(LogLevel.Error, Name, $"HTTP 连接建立失败：{connectResp}");
                return false;
            }

            var parts = connectResp.Split(':', 2);
            if (parts.Length < 2) { _logger.Log(LogLevel.Error, Name, "HTTP 连接响应格式错误"); return false; }
            var connId = parts[0];
            var encHandshake = parts[1];

            var handshake = _cipher.Open(encHandshake, _cfg.AesKey);
            using var stream = client.GetStream();
            await stream.WriteAsync(handshake, ct);

            _logger.Log(LogLevel.Info, Name, $"HTTP 握手包已发送，连接ID={connId}");
            _stats.OnConnect(Name, "HTTP");
            _tap.OnState(Name, "HTTP");
            StateChanged?.Invoke(Name, TunnelState.HttpConnected);

            var buf = new byte[1 << 13];
            while (client.Connected && !ct.IsCancellationRequested)
            {
                int n = await stream.ReadAsync(buf, ct);
                if (n <= 0) break;

                var chunk = new byte[n];
                Buffer.BlockCopy(buf, 0, chunk, 0, n);
                _tap.OnBytes(Name, Direction.Out, chunk.AsSpan());

                var enc = _cipher.Seal(chunk, _cfg.AesKey);
                var tunnelResp = await PostAsync(_cfg.ServerUrl + _cfg.HttpBasePath + "/tunnel", enc, connId, ct);
                if (string.IsNullOrEmpty(tunnelResp) || tunnelResp.StartsWith("4") || tunnelResp.StartsWith("5"))
                {
                    _logger.Log(LogLevel.Error, Name, $"HTTP 隧道传输失败：{tunnelResp}");
                    _stats.RecordError(Name, $"隧道传输失败: {tunnelResp}");
                    break;
                }
                var decrypted = _cipher.Open(tunnelResp, _cfg.AesKey);
                _tap.OnBytes(Name, Direction.In, decrypted.AsSpan());
                await stream.WriteAsync(decrypted, ct);
                _stats.RecordBytes(Name, Direction.In, decrypted.Length);
            }

            await PostAsync(_cfg.ServerUrl + _cfg.HttpBasePath + "/disconnect", auth, connId, ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Log(LogLevel.Error, Name, $"HTTP 模式传输错误：{ex.Message}");
            _stats.RecordError(Name, $"HTTP 错误: {ex.Message}");
            return false;
        }
    }

    private async Task<string?> PostAsync(string url, string body, string? connId, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/plain")
            };
            if (connId != null) req.Headers.Add("X-Conn-Id", connId);
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return ((int)resp.StatusCode).ToString();
            return await resp.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.Log(LogLevel.Warn, Name, $"HTTP 请求失败：{ex.Message}");
            return null;
        }
    }

    public void Dispose()
    {
        Stop();
        _http.Dispose();
        _cts?.Dispose();
    }
}
