package io.github.objectyan.cryptunnel.core.crypto;

import java.util.Collection;
import java.util.Collections;

/**
 * 隧道加密算法的可插拔接口。
 *
 * <p>设计要点（详见 ADR-0003）：</p>
 * <ul>
 *   <li><b>报文内不含算法标识</b>：{@link #id()} 仅用于本地配置匹配与日志，
 *       绝不写入线上载荷——携带明文标识等于向 DPI 宣告「这是可换算法的隧道」。</li>
 *   <li><b>传入原始密钥而非 {@code SecretKeySpec}</b>：密钥派生方式由各算法自行决定，
 *       使接口不绑定 JCE 体系。</li>
 *   <li><b>载荷为 {@code String}</b>：现有链路为 WebSocket {@code TextMessage} + Base64，
 *       由算法自行决定编码格式，换算法时才能真正自由。</li>
 * </ul>
 *
 * <p><b>兼容性红线</b>：默认实现 {@link AesCbcHmacSha256Cipher} 必须字节级等价于
 * 改动前的固定实现，否则破坏「老客户端零改动」承诺。</p>
 */
public interface TunnelCipher {

    /**
     * 算法标识，用于本地配置匹配与日志输出。
     *
     * <p>该值<b>绝不</b>出现在传输报文中。</p>
     *
     * @return 稳定的算法标识字符串
     */
    String id();

    /**
     * 可选别名，便于配置时使用短名（例如 SM4 的 {@code "sm4"} 等价于
     * {@code "sm4-cbc-hmac-sha256"}）。
     *
     * <p>默认返回空集合；实现可覆盖。别名同样只用于本地配置匹配，绝不进报文。</p>
     *
     * @return 别名集合，可为空
     */
    default Collection<String> aliases() {
        return Collections.emptyList();
    }

    /**
     * 加密：明文 -&gt; 线上载荷。
     *
     * @param plaintext 待加密数据
     * @param rawKey    原始密钥字符串，由实现自行派生
     * @return 可直接上线的文本载荷
     */
    String seal(byte[] plaintext, String rawKey);

    /**
     * 解密：线上载荷 -&gt; 明文。
     *
     * @param payload 线上载荷
     * @param rawKey  原始密钥字符串，由实现自行派生
     * @return 明文
     * @throws TunnelCryptoException 载荷格式非法，或完整性校验未通过
     */
    byte[] open(String payload, String rawKey);
}
