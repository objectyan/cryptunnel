package io.github.objectyan.cryptunnel.core.crypto;

/**
 * 隧道加解密失败。
 *
 * <p>涵盖两类情况：</p>
 * <ul>
 *   <li>载荷格式非法（长度不足、编码错误等）</li>
 *   <li>完整性校验未通过（HMAC 不匹配，可能是密钥错误、算法不匹配或数据被篡改）</li>
 * </ul>
 *
 * <p>设计为<b>非受检</b>异常：{@code TargetDiscovery} 采用
 * {@code catch (Exception)} 兜底并继续尝试下一个 target，受检异常会破坏该流程。</p>
 */
public class TunnelCryptoException extends RuntimeException {

    private static final long serialVersionUID = 1L;

    public TunnelCryptoException(String message) {
        super(message);
    }

    public TunnelCryptoException(String message, Throwable cause) {
        super(message, cause);
    }
}
