using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Cryptunnel.Core.Crypto;
using Cryptunnel.Core.Models;

namespace Cryptunnel.Core.Tunnel;

using Cryptunnel.Core.Health;

/// <summary>
/// 隧道健康检查（探活）。
///
/// <para><b>它验的是什么</b>：按真实隧道的同一条路径走一遍 ——
/// 连 WebSocket → 发加密认证报文 → 等服务端推来的 MySQL 握手包 → 解析版本 → 主动关闭。
/// 三层结论一次拿全：网络通不通、密钥/算法配得对不对、服务端能不能连上数据库。</para>
///
/// <para><b>为什么必须是真实链路而不是「连一下端口」</b>：本项目此前的「测试」按钮
/// 只用 TcpClient 连本进程自己的监听端口，authKey 配错、cipher 配错、服务端宕机、
/// MySQL 挂掉这四种故障它<b>全部报成功</b> —— 一个在所有真实故障下都亮绿灯的指示灯，
/// 比没有指示灯更危险，因为它会让用户排除掉本该首先怀疑的方向。</para>
///
/// <para><b>与 <see cref="Tunnel"/> 的关系</b>：本类<b>不复用</b> Tunnel 实例，而是自己开一条
/// 独立的短连接。原因是 Tunnel 的生命周期绑定在一个 DBeaver 连接上 ——
/// 借用它意味着探活会干扰用户正在跑的查询，或者反过来被用户的查询干扰。
/// 认证报文的构造则<b>必须复用</b> <see cref="AuthMessageBuilder"/>：
/// 探活如果自己拼一套认证格式，它验过的就不是真实链路，而是探活自己那一套。</para>
///
/// <para><b>刻意不写统计</b>：本类不碰 <c>StatsCollector</c>。探活是诊断动作而非业务连接，
/// 计进 Connections / BytesIn 会污染「今天真实连了多少次」这个用户唯一能依赖的数字 ——
/// 尤其在周期性探活开启后，那个计数会变成一个只反映探活频率的噪声。</para>
///
/// <para><b>成本必须让调用方知道</b>：每次探活都会让服务端真实建立并断开一条 MySQL 连接，
/// 占用服务端 <c>maxConnections</c> 的一个名额。这就是周期性探活默认关闭的原因。</para>
/// </summary>
public static class HealthProbe
{
    /// <summary>
    /// 执行一次完整探活。<b>本方法不抛异常</b> —— 任何失败都表达为报告里的
    /// <see cref="HealthStatus.Fail"/>。诊断工具自己崩掉就失去了全部意义。
    /// </summary>
    /// <param name="cfg">项目配置。使用与真实隧道完全相同的地址、密钥、算法与超时。</param>
    /// <param name="ct">取消令牌（界面关闭 / 应用退出）。</param>
    public static async Task<HealthReport> RunAsync(TunnelConfig cfg, CancellationToken ct = default)
    {
        var stages = new List<HealthStageResult>(3);
        var total = Stopwatch.StartNew();
        var startedAt = DateTime.Now;

        string cipherId;
        ITunnelCipher cipher;
        try
        {
            cipher = CipherRegistry.Get(cfg.Cipher);
            cipherId = cipher.Id;
        }
        catch (Exception ex)
        {
            // 算法名非法在配置加载期本应已被拦下。走到这里说明配置绕过了校验，
            // 三层全部无法执行 —— 报成 Transport 失败会误导用户去查网络。
            total.Stop();
            stages.Add(new HealthStageResult(HealthStage.Transport, HealthStatus.Fail, 0,
                $"加密算法配置非法：{ex.Message}",
                "请修改项目配置中的 cipher 项，取值必须是服务端也支持的算法标识。"));
            AddSkipped(stages, HealthStage.Authentication, "上一层失败，未执行");
            AddSkipped(stages, HealthStage.Database, "上一层失败，未执行");
            return Build(cfg, startedAt, stages, total.ElapsedMilliseconds, null, cfg.Cipher);
        }

        using var ws = new ClientWebSocket();

        // ---------- 第 1 层：传输 ----------
        var sw = Stopwatch.StartNew();
        var wsUrl = cfg.ServerUrl
            .Replace("http://", "ws://")
            .Replace("https://", "wss://") + cfg.WsPath;

        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(cfg.WsConnectMs);
            await ws.ConnectAsync(new Uri(wsUrl), connectCts.Token);
        }
        catch (Exception ex)
        {
            sw.Stop();
            total.Stop();
            stages.Add(new HealthStageResult(HealthStage.Transport, HealthStatus.Fail,
                sw.ElapsedMilliseconds,
                $"无法建立 WebSocket 连接：{Describe(ex, ct)}",
                "请确认服务端地址(serverUrl)与路径(wsPath)正确、服务端已启动，"
                + "且中间的反向代理 / WAF 允许 WebSocket 升级。"
                + "若此层始终失败而 HTTP 降级可用，通常是代理未放行 Upgrade 头。"));
            AddSkipped(stages, HealthStage.Authentication, "网络未连通，未执行");
            AddSkipped(stages, HealthStage.Database, "网络未连通，未执行");
            return Build(cfg, startedAt, stages, total.ElapsedMilliseconds, null, cipherId);
        }

        sw.Stop();
        stages.Add(new HealthStageResult(HealthStage.Transport, HealthStatus.Pass,
            sw.ElapsedMilliseconds, $"WebSocket 已连接：{wsUrl}", null));

        string? version;
        try
        {
            // ---------- 第 2、3 层：认证 + 数据库 ----------
            version = await ProbeAuthAndDatabaseAsync(cfg, cipher, ws, stages, ct);
        }
        finally
        {
            // 必须主动关闭。探活留下的连接会一直占着服务端的连接名额和一条 MySQL 连接，
            // 周期性探活下会稳定地把服务端连接数吃满。
            await CloseQuietlyAsync(ws);
        }

        total.Stop();
        return Build(cfg, startedAt, stages, total.ElapsedMilliseconds, version, cipherId);
    }

    /// <summary>
    /// 认证与数据库两层。
    ///
    /// <para><b>这两层为什么无法拆开单独计时</b>：服务端认证通过后不发 ACK，
    /// 而是直接去连 MySQL、再把 MySQL 握手包推过来（<c>CryptunnelWebSocketHandler:138-141</c>）。
    /// 客户端能观察到的只有「收到了第一个数据帧」这一个事件 ——
    /// 认证成功的时刻在线路上<b>没有任何可观测的信号</b>。</para>
    ///
    /// <para>所以这里的处理是：收到首帧时，认证层记为通过且耗时记 0，
    /// 把这段等待<b>全部计入数据库层</b>，并在文案里说明。宁可让一层的耗时偏大，
    /// 也不能凭空编一个认证耗时 —— 那个数字会被用来做性能判断。</para>
    /// </summary>
    /// <returns>探到的 MySQL 版本号；未探到时为 <c>null</c>。</returns>
    private static async Task<string?> ProbeAuthAndDatabaseAsync(
        TunnelConfig cfg,
        ITunnelCipher cipher,
        ClientWebSocket ws,
        List<HealthStageResult> stages,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        // 认证报文必须由 AuthMessageBuilder 构造 —— 与真实隧道同一条代码路径。
        // 探活自己拼一套的话，它验证的就不再是用户实际会走的链路。
        string auth;
        try
        {
            auth = AuthMessageBuilder.BuildSealed(cipher, cfg.AuthKey, cfg.AesKey);
        }
        catch (Exception ex)
        {
            // 典型是 authKey 含冒号或 aesKey 为空 —— 配置问题，在本地就能判定。
            sw.Stop();
            stages.Add(new HealthStageResult(HealthStage.Authentication, HealthStatus.Fail,
                sw.ElapsedMilliseconds, $"认证报文构造失败：{ex.Message}",
                "这是本地配置问题，与服务端无关。请按提示修改 authKey / aesKey。"));
            AddSkipped(stages, HealthStage.Database, "认证未发出，未执行");
            return null;
        }

        try
        {
            await ws.SendAsync(Encoding.UTF8.GetBytes(auth), WebSocketMessageType.Text, true, ct);
        }
        catch (Exception ex)
        {
            sw.Stop();
            stages.Add(new HealthStageResult(HealthStage.Authentication, HealthStatus.Fail,
                sw.ElapsedMilliseconds, $"认证报文发送失败：{Describe(ex, ct)}",
                "连接刚建立就无法发送数据，通常是中间设备在 WebSocket 升级后立刻断开了连接。"));
            AddSkipped(stages, HealthStage.Database, "认证未完成，未执行");
            return null;
        }

        // 等服务端响应。三种可能：close 帧（认证被拒）、数据帧（认证通过 + MySQL 已连上）、超时。
        var (outcome, frame, reason) = await ReceiveFirstFrameAsync(cfg, cipher, ws, ct);
        sw.Stop();

        switch (outcome)
        {
            case ProbeOutcome.Rejected:
            {
                // 服务端的 close reason 已有 11 种中文映射，直接复用 —— 探活不该再造一套措辞。
                var failure = CloseReasonMapper.Map(reason);
                stages.Add(new HealthStageResult(HealthStage.Authentication, HealthStatus.Fail,
                    sw.ElapsedMilliseconds, failure.Message,
                    failure.Retryable
                        ? "该问题通常是暂时的，稍后重试即可。"
                        : "重连不会成功，必须先按上述提示修改配置。"));
                AddSkipped(stages, HealthStage.Database, "认证未通过，未执行");
                return null;
            }

            case ProbeOutcome.Timeout:
            {
                // 认证到底过没过，这里是真的无法判断 —— 服务端沉默同时符合两种情况。
                // 把认证层判成失败是在猜，判成通过更是在猜；只能如实说明。
                stages.Add(new HealthStageResult(HealthStage.Authentication, HealthStatus.Fail,
                    sw.ElapsedMilliseconds,
                    $"等待服务端响应超时（{cfg.AuthResponseMs}ms）",
                    "服务端既未拒绝也未推送数据。常见原因："
                    + "① 服务端能认证但连不上数据库，卡在建库连接上；"
                    + "② 中间代理缓冲了 WebSocket 帧。"
                    + $"若数据库建连本身较慢，可调大 timeouts.authResponseMs（当前 {cfg.AuthResponseMs}ms）。"));
                AddSkipped(stages, HealthStage.Database, "未收到服务端响应，无法判定");
                return null;
            }

            case ProbeOutcome.ProtocolViolation:
            {
                stages.Add(new HealthStageResult(HealthStage.Authentication, HealthStatus.Fail,
                    sw.ElapsedMilliseconds, reason ?? "服务端响应不符合协议约定",
                    "对端行为与预期的服务端不一致，可能连接到了错误的服务，"
                    + "或中间设备改写了流量。请核对 serverUrl 指向的确实是 Cryptunnel 服务端。"));
                AddSkipped(stages, HealthStage.Database, "响应异常，未执行");
                return null;
            }

            case ProbeOutcome.DecryptFailed:
            {
                // 认证被服务端放行了，但对方发回来的东西我们解不开。
                // 这在 CBC/SM4 之间尤其容易出现：两者帧结构与 HMAC 密钥派生完全相同，
                // HMAC 校验会通过，只有 PKCS7 去填充能拦下 —— 而它有约 1/256 的概率漏过。
                stages.Add(new HealthStageResult(HealthStage.Authentication, HealthStatus.Fail,
                    sw.ElapsedMilliseconds,
                    $"服务端已响应，但返回的数据无法解密：{reason}",
                    $"当前算法 cipher={cipher.Id}。这说明双方密钥或算法不一致。"
                    + "请核对服务端启动日志中打印的隧道加密算法，"
                    + "并确认 aesKey 两端完全相同。"));
                AddSkipped(stages, HealthStage.Database, "数据无法解密，未执行");
                return null;
            }
        }

        // 走到这里 = 收到了可解密的首帧 = 认证已通过且服务端已连上 MySQL。
        stages.Add(new HealthStageResult(HealthStage.Authentication, HealthStatus.Pass, 0,
            $"认证通过（authKey / aesKey / cipher={cipher.Id} 均与服务端一致）", null));

        var parsed = MySqlHandshake.Parse(frame);
        switch (parsed.Kind)
        {
            case MySqlFrameKind.Handshake:
                stages.Add(new HealthStageResult(HealthStage.Database, HealthStatus.Pass,
                    sw.ElapsedMilliseconds, parsed.Detail, null));
                return parsed.ServerVersion;

            case MySqlFrameKind.ErrorPacket:
                // 服务端→MySQL 的网络是通的，是 MySQL 主动拒绝。排查方向与「连不上」完全相反。
                stages.Add(new HealthStageResult(HealthStage.Database, HealthStatus.Fail,
                    sw.ElapsedMilliseconds, parsed.Detail,
                    AdviceForMySqlError(parsed.ErrorCode)));
                return null;

            default:
                stages.Add(new HealthStageResult(HealthStage.Database, HealthStatus.Fail,
                    sw.ElapsedMilliseconds, parsed.Detail,
                    "首帧能正常解密（说明密钥与算法都对），但内容不是 MySQL 报文。"
                    + "请确认服务端白名单里配置的目标端口确实是 MySQL。"));
                return null;
        }
    }

    /// <summary>
    /// MySQL 错误码 → 排查建议。
    ///
    /// <para>只列在本场景下真实高频的三种。<b>不做大而全的错误码表</b> ——
    /// 那种表的绝大多数条目永远不会被命中，却要求每次 MySQL 版本变化都去核对一遍。
    /// 未列出的错误码原样展示 MySQL 自己的文本，那比一句错误的猜测有用。</para>
    /// </summary>
    private static string AdviceForMySqlError(int code) => code switch
    {
        1130 => "MySQL 拒绝了来自服务端的连接（Host not allowed）。"
                + "这是数据库侧的授权问题：需要为服务端所在主机的 IP 授权，"
                + "而不是修改本客户端的任何配置。",

        1040 => "MySQL 连接数已满（Too many connections）。"
                + "请等待现有连接释放，或联系 DBA 调大 max_connections。",

        1045 => "MySQL 认证失败（Access denied）。"
                + "注意这是<服务端连接数据库>用的账号密码，不是你在 DBeaver 里填的那组 —— "
                + "需要服务端管理员核对其数据源配置。",

        _ => "该错误由 MySQL 直接返回，服务端到数据库的网络是通的。"
             + "请把上面的错误码与文本提供给数据库管理员。"
    };

    private enum ProbeOutcome
    {
        /// <summary>收到可解密的数据帧。</summary>
        Received,

        /// <summary>服务端发来 close 帧。</summary>
        Rejected,

        /// <summary>超时未收到任何响应。</summary>
        Timeout,

        /// <summary>响应类型不符合协议约定（例如二进制帧）。</summary>
        ProtocolViolation,

        /// <summary>收到数据但解不开。</summary>
        DecryptFailed
    }

    /// <summary>
    /// 接收并解密服务端推来的第一个数据帧。
    ///
    /// <para>逻辑与 <see cref="Tunnel"/> 的 <c>AwaitAuthResultAsync</c> 一致，
    /// 但<b>不打日志、不写统计</b>：探活的输出是结构化报告，
    /// 让它同时往日志里塞一份会让实时日志区被周期性探活刷屏。</para>
    /// </summary>
    private static async Task<(ProbeOutcome Outcome, byte[] Frame, string? Reason)>
        ReceiveFirstFrameAsync(
            TunnelConfig cfg, ITunnelCipher cipher, ClientWebSocket ws, CancellationToken ct)
    {
        using var authCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        authCts.CancelAfter(cfg.AuthResponseMs);

        using var ms = new MemoryStream();
        var buf = new byte[1 << 16];

        try
        {
            while (true)
            {
                var r = await ws.ReceiveAsync(buf, authCts.Token);

                if (r.MessageType == WebSocketMessageType.Close)
                    return (ProbeOutcome.Rejected, Array.Empty<byte>(), ws.CloseStatusDescription);

                if (r.MessageType == WebSocketMessageType.Binary)
                    return (ProbeOutcome.ProtocolViolation, Array.Empty<byte>(),
                        "收到二进制帧，但服务端只使用文本帧。");

                ms.Write(buf, 0, r.Count);
                if (!r.EndOfMessage) continue;

                var text = Encoding.UTF8.GetString(ms.ToArray());
                try
                {
                    return (ProbeOutcome.Received, cipher.Open(text, cfg.AesKey), null);
                }
                catch (Exception ex)
                {
                    return (ProbeOutcome.DecryptFailed, Array.Empty<byte>(), ex.Message);
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (ProbeOutcome.Timeout, Array.Empty<byte>(), null);
        }
        catch (OperationCanceledException)
        {
            // 外层取消（关窗 / 退出）。不是故障，按超时呈现即可，调用方会丢弃结果。
            return (ProbeOutcome.Timeout, Array.Empty<byte>(), null);
        }
        catch (Exception ex)
        {
            return (ProbeOutcome.ProtocolViolation, Array.Empty<byte>(),
                $"接收服务端响应时连接异常：{ex.Message}");
        }
    }

    /// <summary>
    /// 主动关闭探活连接，忽略一切异常。
    ///
    /// <para>用独立的短超时而非调用方的 <c>ct</c>：调用方取消（用户关窗）时
    /// 如果把已取消的令牌传进去，CloseAsync 会立刻放弃，
    /// 连接就得留给服务端自己超时回收 —— 而<b>正是取消场景下最需要干净地还回连接</b>。</para>
    /// </summary>
    private static async Task CloseQuietlyAsync(ClientWebSocket ws)
    {
        if (ws.State != WebSocketState.Open) return;
        try
        {
            using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "health-probe", closeCts.Token);
        }
        catch
        {
            // 关闭失败不影响探活结论；服务端会在 socket 断开后自行回收。
        }
    }

    /// <summary>
    /// 异常 → 面向用户的文本。
    ///
    /// <para>单独处理超时：<see cref="OperationCanceledException"/> 的 Message 是
    /// "A task was canceled."，直接展示等于什么都没说 —— 而超时恰恰是这里最常见的失败。</para>
    /// </summary>
    private static string Describe(Exception ex, CancellationToken ct)
    {
        if (ex is OperationCanceledException)
            return ct.IsCancellationRequested ? "操作已取消" : "连接超时";

        // WebSocketException 的 InnerException 往往才是真正有用的那句
        // （如「由于目标计算机积极拒绝，无法连接」）。
        return ex.InnerException is { } inner ? $"{ex.Message}（{inner.Message}）" : ex.Message;
    }

    private static void AddSkipped(List<HealthStageResult> stages, HealthStage stage, string why) =>
        stages.Add(new HealthStageResult(stage, HealthStatus.Skipped, 0, why, null));

    private static HealthReport Build(
        TunnelConfig cfg, DateTime startedAt, List<HealthStageResult> stages,
        long totalMs, string? version, string cipherId) =>
        new()
        {
            ProjectName = cfg.Name,
            DisplayName = string.IsNullOrWhiteSpace(cfg.DisplayName) ? cfg.Name : cfg.DisplayName,
            StartedAt = startedAt,
            Stages = stages,
            TotalMs = totalMs,
            ServerVersion = version,
            CipherId = cipherId
        };
}
