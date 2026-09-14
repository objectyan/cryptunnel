namespace CryptoParity;

/// <summary>
/// 读取 dotnet/build/parity/*.txt 基准向量。
/// 向量由 Java 侧实现导出，是本自检的唯一事实来源，禁止在 C# 侧硬编码期望值。
/// </summary>
internal sealed class Vectors
{
    private readonly Dictionary<string, string> _values;

    private Vectors(Dictionary<string, string> values) => _values = values;

    /// <summary>rawKey，三种算法共用同一把配置密钥。</summary>
    public string RawKey => this["RAW_KEY"];

    /// <summary>基准明文（模拟真实认证报文）。</summary>
    public byte[] Plain => Encoding.UTF8.GetBytes(this["PLAIN"]);

    public string this[string key] =>
        _values.TryGetValue(key, out var v)
            ? v
            : throw new KeyNotFoundException($"基准向量缺少键 {key}，请检查 vectors 文件是否完整");

    public static Vectors Load()
    {
        var dir = LocateParityDir();
        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var file in new[] { "vectors.txt", "vectors-sm4-gcm.txt" })
        {
            var path = Path.Combine(dir, file);
            if (!File.Exists(path))
                throw new FileNotFoundException($"找不到基准向量文件：{path}");

            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#'))
                    continue;
                var idx = line.IndexOf('=');
                if (idx <= 0)
                    continue;
                // 两个文件都定义了 RAW_KEY / AES_KEY / HMAC_KEY / PLAIN，值相同，后者覆盖前者无妨
                values[line[..idx].Trim()] = line[(idx + 1)..].Trim();
            }
        }

        return new Vectors(values);
    }

    /// <summary>
    /// 从运行目录向上找 parity 目录。这样无论从仓库根、build 目录还是 bin 下运行都能定位。
    /// </summary>
    private static string LocateParityDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (dir.Name == "parity" && File.Exists(Path.Combine(dir.FullName, "vectors.txt")))
                return dir.FullName;

            var candidate = Path.Combine(dir.FullName, "dotnet", "build", "parity", "vectors.txt");
            if (File.Exists(candidate))
                return Path.GetDirectoryName(candidate)!;

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("向上遍历未找到 dotnet/build/parity 目录");
    }

    public static string ToHex(ReadOnlySpan<byte> data) =>
        Convert.ToHexString(data).ToLowerInvariant();

    public static byte[] FromHex(string hex) => Convert.FromHexString(hex);
}
