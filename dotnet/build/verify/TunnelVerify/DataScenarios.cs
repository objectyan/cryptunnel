using System.Net.Sockets;
using Cryptunnel.Core.Crypto;
using Cryptunnel.Core.Logging;
using Cryptunnel.Core.Models;
using Cryptunnel.Core.Stats;
using Cryptunnel.Core.Tunnel;

namespace TunnelVerify;

internal static partial class Program
{
    /// <summary>
    /// [3] 非预期帧类型必须硬失败。此前 Binary 帧会被当文本解析成乱码、
    /// 解密失败后 continue，静默丢数据。
    /// </summary>
    private static async Task FrameTypeChecks()
    {
        Console.WriteLine();
        Console.WriteLine("[3] 非预期帧类型与解密失败必须断开（不得静默继续）");

        {
            var cipher = CipherRegistry.Default;
            await using var server = new FakeServer(cipher, AesKey, ServerBehavior.PushBinaryFrame);
            server.Start();

            CapturingLogger? log = null;
            await WithTunnel(server, cipher.Id, async (client, logger) =>
            {
                log = logger;
                await WaitUntil(() => logger.Has("二进制帧"), TimeSpan.FromSeconds(5));
            });

            Check("认证阶段收到二进制帧 → 报错并拒绝",
                log?.Has("二进制帧") ?? false,
                $"    日志：\n{log?.Dump()}");
        }

        {
            var cipher = CipherRegistry.Default;
            await using var server = new FakeServer(cipher, AesKey, ServerBehavior.PushUndecryptable);
            server.Start();

            CapturingLogger? log = null;
            await WithTunnel(server, cipher.Id, async (client, logger) =>
            {
                log = logger;
                await WaitUntil(() => logger.Has("解密失败"), TimeSpan.FromSeconds(5));
            });

            Check("首帧解密失败 → 报错并提示核对 cipher/aesKey",
                (log?.Has("解密失败") ?? false) && (log?.Has("cipher") ?? false),
                $"    日志：\n{log?.Dump()}");
        }

        // 客户端与服务端算法不一致：最难诊断的一种配置错误。
        {
            var serverCipher = CipherRegistry.Get("aes-256-gcm");
            await using var server = new FakeServer(serverCipher, AesKey);
            server.Start();

            CapturingLogger? log = null;
            await WithTunnel(server, "aes-256-cbc-hmac-sha256", async (client, logger) =>
            {
                log = logger;
                await WaitUntil(() => logger.Has("认证失败") || logger.Has("解密失败"),
                    TimeSpan.FromSeconds(5));
            });

            Check("客户端 CBC vs 服务端 GCM → 明确失败（不静默传坏数据）",
                (log?.Has("认证失败") ?? false) || (log?.Has("解密失败") ?? false),
                $"    日志：\n{log?.Dump()}");
        }
    }

    /// <summary>
    /// [4] 认证成功后的双向数据转发必须正常。这是「功能完整」的底线 ——
    /// 前面所有安全加固都不能把正常的数据通路弄坏。
    /// </summary>
    private static async Task DataPathChecks()
    {
        Console.WriteLine();
        Console.WriteLine("[4] 认证后双向数据转发（三算法均须正常）");

        foreach (var cipherId in new[]
                 { "aes-256-cbc-hmac-sha256", "aes-256-gcm", "sm4-cbc-hmac-sha256" })
        {
            var cipher = CipherRegistry.Get(cipherId);
            await using var server = new FakeServer(cipher, AesKey);
            server.Start();

            var payload = "SELECT * FROM orders WHERE id = 42;"u8.ToArray();
            byte[] echoed = [];
            CapturingLogger? log = null;

            await WithTunnel(server, cipherId, async (client, logger) =>
            {
                log = logger;
                var stream = client.GetStream();
                var buf = new byte[512];
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

                // 先收握手包（内容已在 [1] 组验过，这里只需消费掉）
                try { _ = await stream.ReadAsync(buf, cts.Token); }
                catch (OperationCanceledException) { return; }

                // 发一条数据，等回显
                await stream.WriteAsync(payload, cts.Token);
                try
                {
                    var n = await stream.ReadAsync(buf, cts.Token);
                    echoed = buf[..n];
                }
                catch (OperationCanceledException) { }
            });

            Check($"{cipherId}: 出站数据正确送达服务端",
                server.ReceivedDataFrames.Count > 0
                && server.ReceivedDataFrames[0].SequenceEqual(payload),
                $"    服务端收到 {server.ReceivedDataFrames.Count} 帧；日志：\n{log?.Dump()}");

            // 服务端回显时加了 0xFF 前缀
            var expectedEcho = new byte[payload.Length + 1];
            expectedEcho[0] = 0xFF;
            payload.CopyTo(expectedEcho, 1);

            Check($"{cipherId}: 入站数据正确回到 DBeaver",
                echoed.SequenceEqual(expectedEcho),
                $"    期望 {expectedEcho.Length} 字节，实际 {echoed.Length} 字节");
        }
    }

    /// <summary>
    /// [5] 非回环监听的安全告警。用户选择保留「只告警不阻止」，
    /// 因此告警必须做到无法被忽略，且两条启动路径都要覆盖。
    /// </summary>
    private static void SecurityWarningChecks()
    {
        Console.WriteLine();
        Console.WriteLine("[5] 非回环监听安全告警（StartAll 与 StartOne 都须覆盖）");

        TunnelConfig MakeCfg(string addr) => new()
        {
            Name = "sec-" + Guid.NewGuid().ToString("N")[..6],
            ServerUrl = "http://127.0.0.1:1",
            AesKey = AesKey,
            AuthKey = AuthKey,
            ListenAddress = addr,
            LocalPort = FreePort(),
            Enabled = true,
        };

        // 通配与非回环地址都必须告警
        foreach (var addr in new[] { "0.0.0.0", "::", "*", "+" })
        {
            var logger = new CapturingLogger();
            using var mgr = new TunnelManager(logger, new NullTap(), new StatsCollector());
            mgr.StartAll([MakeCfg(addr)]);

            Check($"StartAll: 监听 {addr} 触发安全警告",
                logger.HasAtLevel(LogLevel.Warn, "安全警告"),
                $"    日志：\n{logger.Dump()}");

            Check($"StartAll: {addr} 的告警说明无需密钥即可被利用",
                logger.Has("无需知道") && logger.Has("127.0.0.1"),
                $"    日志：\n{logger.Dump()}");

            mgr.StopAll();
        }

        // StartOne 此前完全没有这个检查
        {
            var logger = new CapturingLogger();
            using var mgr = new TunnelManager(logger, new NullTap(), new StatsCollector());
            mgr.StartOne(MakeCfg("0.0.0.0"));

            Check("StartOne: 监听 0.0.0.0 同样触发安全警告（此前该路径无检查）",
                logger.HasAtLevel(LogLevel.Warn, "安全警告"),
                $"    日志：\n{logger.Dump()}");

            mgr.StopAll();
        }

        // 回环地址不应产生噪音告警
        foreach (var addr in new[] { "127.0.0.1", "127.0.0.5", "::1" })
        {
            var logger = new CapturingLogger();
            using var mgr = new TunnelManager(logger, new NullTap(), new StatsCollector());
            mgr.StartAll([MakeCfg(addr)]);

            Check($"回环地址 {addr} 不产生告警（避免告警疲劳）",
                !logger.Has("安全警告"),
                $"    日志：\n{logger.Dump()}");

            mgr.StopAll();
        }
    }
}
