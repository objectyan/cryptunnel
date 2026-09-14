using System.Net.Sockets;
using Cryptunnel.Core.Crypto;
using Cryptunnel.Core.Logging;
using Cryptunnel.Core.Models;
using Cryptunnel.Core.Stats;
using Cryptunnel.Core.Tunnel;

namespace TunnelVerify;

/// <summary>
/// 隧道运行时的端到端验收。用真实 WebSocket 服务端 + 真实 TcpClient 跑通整条链路。
///
/// <para><b>为什么不用 mock</b>：这次改的是认证判定、帧类型处理、close reason 传递
/// 这些协议细节，把 WebSocket 换成可注入的抽象等于把要验的东西一起 mock 掉了。</para>
/// </summary>
internal static partial class Program
{
    private static int _passed;
    private static int _failed;

    private const string AesKey = "verify-aes-key-0123456789";
    private const string AuthKey = "verify-auth-key";

    private static async Task<int> Main()
    {
        try { Console.OutputEncoding = Encoding.UTF8; }
        catch (IOException) { }

        Console.WriteLine("=== 隧道运行时端到端验收（真实 WebSocket + 真实 TCP）===");

        await AuthSuccessChecks();
        await AuthFailureChecks();
        await FrameTypeChecks();
        await DataPathChecks();
        SecurityWarningChecks();

        Console.WriteLine();
        Console.WriteLine($"=== 通过 {_passed} 项，失败 {_failed} 项 ===");
        return _failed == 0 ? 0 : 1;
    }

    /// <summary>构造一份指向模拟服务端的配置。</summary>
    private static TunnelConfig Cfg(FakeServer server, string cipher, int localPort) => new()
    {
        Name = "verify",
        DisplayName = "verify",
        Enabled = true,
        ServerUrl = server.ServerUrl,
        AesKey = AesKey,
        AuthKey = AuthKey,
        ListenAddress = "127.0.0.1",
        LocalPort = localPort,
        WsPath = server.WsPath,
        Mode = TransportMode.WebSocket,
        AllowFallback = false,
        WsConnectMs = 5000,
        AuthResponseMs = 2000,
        ChunkSize = 4096,
        Cipher = cipher,
    };

    private static int FreePort()
    {
        var l = new TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    /// <summary>
    /// 起一条隧道、用 TcpClient 连上去，把控制权交给 <paramref name="body"/>。
    /// </summary>
    private static async Task WithTunnel(
        FakeServer server, string cipher,
        Func<TcpClient, CapturingLogger, Task> body)
    {
        var logger = new CapturingLogger();
        var port = FreePort();
        var cfg = Cfg(server, cipher, port);
        using var tunnel = new Tunnel(cfg, logger, new NullTap(), new StatsCollector());
        tunnel.Start();

        // 等监听就绪
        await WaitUntil(() => logger.Has("监听 localhost"), TimeSpan.FromSeconds(3));

        using var client = new TcpClient();
        await client.ConnectAsync(System.Net.IPAddress.Loopback, port);

        await body(client, logger);
    }

    private static async Task<bool> WaitUntil(Func<bool> cond, TimeSpan timeout)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (cond()) return true;
            await Task.Delay(25);
        }
        return cond();
    }

    private static void Check(string name, bool ok, string? detail = null)
    {
        if (ok)
        {
            _passed++;
            Console.WriteLine($"  [PASS] {name}");
        }
        else
        {
            _failed++;
            Console.WriteLine($"  [FAIL] {name}");
            if (!string.IsNullOrEmpty(detail)) Console.WriteLine(detail);
        }
    }
}
