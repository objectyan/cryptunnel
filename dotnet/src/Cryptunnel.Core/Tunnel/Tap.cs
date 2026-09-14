namespace Cryptunnel.Core.Tunnel;

public enum Direction { Out, In } // Out: 本地->服务端；In: 服务端->本地

/// <summary>
/// 只读旁路。转发路径在每个字节处调用 OnBytes，但绝不反向影响隧道。
/// 默认挂 NullTap（零开销）。二期可实现 SqlParsingTap 做 SQL 分析，
/// 且 tap 内任何异常都会被隧道捕获并降级回 NullTap，绝不会拖垮代理。
/// </summary>
public interface ITunnelTap
{
    void OnBytes(string project, Direction dir, ReadOnlySpan<byte> data);
    void OnState(string project, string state);
    void OnError(string project, string message);
}

public sealed class NullTap : ITunnelTap
{
    public void OnBytes(string project, Direction dir, ReadOnlySpan<byte> data) { }
    public void OnState(string project, string state) { }
    public void OnError(string project, string message) { }
}
