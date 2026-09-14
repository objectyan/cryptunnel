using System.Net.Sockets;
using Cryptunnel.Core.Crypto;
using Cryptunnel.Core.Logging;

namespace TunnelVerify;

internal static partial class Program
{
    /// <summary>
    /// [1] 认证成功路径。核心是验证「握手包不能被吞掉」——
    /// 服务端认证成功不发 ACK，直接推 MySQL 握手包，
    /// 客户端把它当作认证成功信号的同时，必须把它转发给 DBeaver。
    /// </summary>
    private static async Task AuthSuccessChecks()
    {
        Console.WriteLine();
        Console.WriteLine("[1] 认证成功：服务端不发 ACK，直接推 MySQL 握手包");

        foreach (var cipherId in new[]
                 { "aes-256-cbc-hmac-sha256", "aes-256-gcm", "sm4-cbc-hmac-sha256" })
        {
            var cipher = CipherRegistry.Get(cipherId);
            await using var server = new FakeServer(cipher, AesKey);
            server.Start();

            byte[] received = [];
            CapturingLogger? log = null;

            await WithTunnel(server, cipherId, async (client, logger) =>
            {
                log = logger;
                var stream = client.GetStream();
                var buf = new byte[256];
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    var n = await stream.ReadAsync(buf, cts.Token);
                    received = buf[..n];
                }
                catch (OperationCanceledException) { }
            });

            Check($"{cipherId}: 认证成功并收到握手包",
                received.Length > 0 && received[0] == FakeServer.HandshakePacket[0],
                $"    收到 {received.Length} 字节；日志：\n{log?.Dump()}");

            Check($"{cipherId}: 握手包内容完整转发给 DBeaver",
                received.SequenceEqual(FakeServer.HandshakePacket),
                $"    期望 {BitConverter.ToString(FakeServer.HandshakePacket)}\n"
                + $"    实际 {BitConverter.ToString(received)}");

            Check($"{cipherId}: 服务端收到的认证报文是 4 段格式",
                server.AuthDecrypted
                && (server.ReceivedAuthPlaintext?.StartsWith("AUTH:" + AuthKey + ":",
                        StringComparison.Ordinal) ?? false)
                && server.ReceivedAuthPlaintext!.Split(':').Length == 4,
                $"    实际：{server.ReceivedAuthPlaintext}");
        }

        // 认证不应再固定阻塞 AuthResponseMs。
        {
            var cipher = CipherRegistry.Default;
            await using var server = new FakeServer(cipher, AesKey);
            server.Start();

            var sw = System.Diagnostics.Stopwatch.StartNew();
            await WithTunnel(server, cipher.Id, async (client, unusedLogger) =>
            {
                var stream = client.GetStream();
                var buf = new byte[256];
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { _ = await stream.ReadAsync(buf, cts.Token); }
                catch (OperationCanceledException) { }
            });
            sw.Stop();

            // AuthResponseMs 配的是 2000ms。旧实现无论如何都要睡满，
            // 新实现收到握手包就继续，本地回环应远快于此。
            Check($"认证不再固定阻塞 AuthResponseMs（实测 {sw.ElapsedMilliseconds}ms < 2000ms）",
                sw.ElapsedMilliseconds < 2000);
        }
    }

    /// <summary>
    /// [2] 认证失败路径。每一种 close reason 都必须变成可操作的中文提示，
    /// 而不是笼统的「隧道建立失败」。
    /// </summary>
    private static async Task AuthFailureChecks()
    {
        Console.WriteLine();
        Console.WriteLine("[2] 认证失败：close reason 必须翻译成具体中文原因");

        var cases = new (string Reason, string ExpectFragment, bool ExpectNotRetryable)[]
        {
            ("Auth key invalid",           "认证密钥",   true),
            ("Auth decrypt failed",        "cipher",     true),
            ("Auth timestamp expired",     "时钟",       false),
            ("Max connections exceeded",   "连接数",     false),
            ("MySQL connection failed",    "无法连接",   false),
        };

        foreach (var (reason, fragment, notRetryable) in cases)
        {
            var cipher = CipherRegistry.Default;
            await using var server = new FakeServer(
                cipher, AesKey, ServerBehavior.RejectWithReason, reason);
            server.Start();

            CapturingLogger? log = null;
            await WithTunnel(server, cipher.Id, async (client, logger) =>
            {
                log = logger;
                await WaitUntil(() => logger.Has("认证失败"), TimeSpan.FromSeconds(5));
            });

            Check($"{reason} → 中文提示含「{fragment}」",
                log?.Has(fragment) ?? false,
                $"    日志：\n{log?.Dump()}");

            if (notRetryable)
            {
                Check($"{reason} → 明确告知重连无用",
                    log?.Has("重连也不会成功") ?? false,
                    $"    日志：\n{log?.Dump()}");
            }
        }

        // 服务端沉默 → 必须超时而不是永久挂起。
        {
            var cipher = CipherRegistry.Default;
            await using var server = new FakeServer(cipher, AesKey, ServerBehavior.Silent);
            server.Start();

            CapturingLogger? log = null;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await WithTunnel(server, cipher.Id, async (client, logger) =>
            {
                log = logger;
                await WaitUntil(() => logger.Has("认证超时"), TimeSpan.FromSeconds(6));
            });
            sw.Stop();

            Check("服务端不响应时按 AuthResponseMs 超时（不永久挂起）",
                log?.Has("认证超时") ?? false,
                $"    耗时 {sw.ElapsedMilliseconds}ms；日志：\n{log?.Dump()}");
        }
    }
}
