namespace CryptoParity;

/// <summary>
/// 跨语言加密对齐自检。全部断言通过时退出码 0，任一失败退出码 1。
///
/// 校验内容：
///   1. 密钥派生逐字节对齐 Java（AES / HMAC / SM4）
///   2. SM4 分组函数对齐 GB/T 32907-2016 附录 A.1 官方向量
///   3. 解开 Java 侧产出的真实载荷（SM4 / GCM / 默认 AES-CBC）
///   4. 三种算法各自 round-trip、篡改检测、跨算法互不兼容
/// </summary>
internal static class Program
{
    private static int _passed;
    private static int _failed;

    private static int Main(string[] args)
    {
        // 始终按 UTF-8 输出：Windows 控制台默认 ANSI 代码页会把中文写成 GBK 字节，
        // 在 CI / Git Bash / 重定向到文件时都会乱码。
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch (IOException)
        {
            // 无控制台宿主（如某些 CI runner）时忽略
        }

        var vectors = Vectors.Load();

        // --export 模式：只输出 C# 侧加密的载荷，交由 Java 侧解密，
        // 用于补齐「C# 加密 -> Java 解密」这个反方向。
        if (args.Length > 0 && args[0] == "--export")
        {
            Exporter.Run(vectors);
            return 0;
        }

        // --verify-rust <file> 模式：解开 Rust 侧现场加密的载荷，
        // 验证「Rust 加密 -> C# 解密」（Rust 客户端可行性校验的反方向闭环）。
        if (args.Length > 1 && args[0] == "--verify-rust")
        {
            return RustPayloadChecks.Run(args[1]);
        }

        Console.WriteLine("=== Cryptunnel 加密跨语言对齐自检 ===");
        Console.WriteLine();

        KeyDerivationChecks.Run(vectors, Check);
        Sm4EngineChecks.Run(vectors, Check);
        PayloadChecks.Run(vectors, Check);
        RoundTripChecks.Run(vectors, Check);
        IsolationChecks.Run(vectors, Check);

        Console.WriteLine();
        Console.WriteLine($"=== 通过 {_passed} 项，失败 {_failed} 项 ===");
        return _failed == 0 ? 0 : 1;
    }

    /// <summary>执行一项断言并打印结果。异常也算失败，不中断后续检查。</summary>
    internal static void Check(string name, Func<bool> assertion)
    {
        bool ok;
        string extra = string.Empty;
        try
        {
            ok = assertion();
        }
        catch (Exception e)
        {
            ok = false;
            extra = $" -> {e.GetType().Name}: {e.Message}";
        }

        if (ok)
        {
            _passed++;
            Console.WriteLine($"  [PASS] {name}");
        }
        else
        {
            _failed++;
            Console.WriteLine($"  [FAIL] {name}{extra}");
        }
    }

    /// <summary>断言指定操作抛出异常（用于篡改检测、跨算法隔离）。</summary>
    internal static bool Throws(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (Exception)
        {
            return true;
        }
    }
}
