package io.github.objectyan.cryptunnel.core.crypto;

import java.util.ArrayList;
import java.util.Collection;
import java.util.Collections;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.ConcurrentMap;

/**
 * {@link TunnelCipher} 注册表：按算法标识查找实现。
 *
 * <p>启动时注册：</p>
 * <ul>
 *   <li>{@link AesCbcHmacSha256Cipher} —— 默认，JDK 8 原生</li>
 *   <li>{@link AesGcmCipher} —— JDK 8 原生，AEAD，载荷更短</li>
 *   <li>{@link Sm4Cipher} —— 国密，<b>仅在 classpath 存在 BouncyCastle 时注册</b></li>
 * </ul>
 *
 * <p><b>为什么 SM4 要条件注册</b>：BouncyCastle 在 core 中是 optional 依赖，
 * 未引入时 {@code Sm4Cipher} 类连加载都会抛 NoClassDefFoundError。
 * 条件注册可让「未引入 BC」表现为「该算法不存在」，配置期报错信息干净可读。</p>
 *
 * <p><b>注意</b>：本注册表只做「标识 -&gt; 实现」的本地映射，
 * 与线上协商无关——按 ADR-0003，报文内不含任何算法标识。</p>
 */
public final class TunnelCiphers {

    /** 国密 SM4 的简便别名。常量放此处，以免仅为取别名就加载 Sm4Cipher 类。 */
    static final String SM4_ALIAS = "sm4";

    /** Sm4Cipher.ID 的副本，避免为拼报错文案而加载 Sm4Cipher。 */
    static final String SM4_ID = "sm4-cbc-hmac-sha256";

    private static final ConcurrentMap<String, TunnelCipher> REGISTRY =
            new ConcurrentHashMap<String, TunnelCipher>();

    /** 全局默认算法。使用 volatile 保证跨线程可见，仅在启动期写入。 */
    private static volatile TunnelCipher defaultCipher = new AesCbcHmacSha256Cipher();

    static {
        register(new AesCbcHmacSha256Cipher());
        register(new AesGcmCipher());
        registerSm4IfAvailable();
    }

    private TunnelCiphers() {
    }

    /**
     * BouncyCastle 存在时反射注册 SM4。反射是刻意的：直接 {@code new Sm4Cipher()}
     * 会在此处产生对 {@code org.bouncycastle.*} 的静态强引用，
     * 导致未引入 BC 的环境在类初始化阶段就炸。
     */
    private static void registerSm4IfAvailable() {
        try {
            Class.forName("org.bouncycastle.jce.provider.BouncyCastleProvider");
            TunnelCipher sm4 = (TunnelCipher) Class
                    .forName("io.github.objectyan.cryptunnel.core.crypto.Sm4Cipher")
                    .newInstance();
            REGISTRY.put(sm4.id(), sm4);
            REGISTRY.put(SM4_ALIAS, sm4);
        } catch (Throwable absent) {
            // BouncyCastle 未引入：SM4 不可用。配置期会给出明确报错，此处静默。
        }
    }

    /**
     * 注册一个算法实现。相同 {@code id} 的后注册者覆盖先注册者；
     * 同时注册 {@link TunnelCipher#aliases()} 返回的别名。
     *
     * @param cipher 实现，不可为 null 且 {@code id()} 不可为空
     * @throws IllegalArgumentException 参数为 null 或 id 为空
     */
    public static void register(TunnelCipher cipher) {
        if (cipher == null) {
            throw new IllegalArgumentException("cipher is null");
        }
        String id = cipher.id();
        if (id == null || id.isEmpty()) {
            throw new IllegalArgumentException("cipher id is empty");
        }
        REGISTRY.put(id, cipher);
        Collection<String> aliases = cipher.aliases();
        if (aliases != null) {
            for (String alias : aliases) {
                if (alias != null && !alias.isEmpty()) {
                    REGISTRY.put(alias, cipher);
                }
            }
        }
    }

    /**
     * 按标识获取实现。
     *
     * @param id 算法标识或其别名
     * @return 对应实现
     * @throws IllegalArgumentException 未注册该标识，或 id 为 null
     */
    public static TunnelCipher get(String id) {
        if (id == null || id.isEmpty()) {
            throw new IllegalArgumentException("cipher id is empty");
        }
        TunnelCipher cipher = REGISTRY.get(id);
        if (cipher == null) {
            throw new IllegalArgumentException(
                    "unknown cipher '" + id + "'" + missingDependencyHint(id)
                            + ", available: " + ids());
        }
        return cipher;
    }

    /** 是否注册了指定标识（或其别名）。 */
    public static boolean contains(String id) {
        return id != null && REGISTRY.containsKey(id);
    }

    /** 已注册的全部标识与别名（顺序不保证）。 */
    public static Collection<String> ids() {
        return Collections.unmodifiableCollection(new ArrayList<String>(REGISTRY.keySet()));
    }

    /**
     * 设置全局默认算法，供宿主应用启动期调用一次。
     *
     * <p>设置后 {@link io.github.objectyan.cryptunnel.core.auth.TargetDiscovery}
     * 等组件即改用新算法，调用点无需改动。按 ADR-0003，服务端同一时刻只启用一个算法。</p>
     *
     * @param cipher 实现，不可为 null
     * @throws IllegalArgumentException 参数为 null
     */
    public static void setDefault(TunnelCipher cipher) {
        if (cipher == null) {
            throw new IllegalArgumentException("cipher is null");
        }
        defaultCipher = cipher;
    }

    /** 当前全局默认算法，缺省为 {@link AesCbcHmacSha256Cipher}。 */
    public static TunnelCipher defaultCipher() {
        return defaultCipher;
    }

    /**
     * 缺失可选依赖时给出可操作的提示——直接抛 NoClassDefFoundError 无法指导用户修复。
     */
    private static String missingDependencyHint(String id) {
        if (SM4_ALIAS.equals(id) || SM4_ID.equals(id)) {
            return " (需要额外依赖 org.bouncycastle:bcprov-jdk18on)";
        }
        return "";
    }
}
