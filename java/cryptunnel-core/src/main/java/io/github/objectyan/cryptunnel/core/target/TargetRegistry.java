package io.github.objectyan.cryptunnel.core.target;

import java.util.Collection;
import java.util.Set;

/**
 * target 注册表。
 */
public interface TargetRegistry {

    TargetDefinition get(String targetId);

    Set<String> names();

    /**
     * 全部 target 定义（不可变快照）。
     *
     * <p>用于服务端在收到加密 AUTH 报文时，<b>逐个试用每个 target 的密钥尝试解密</b>
     * （参见 {@link io.github.objectyan.cryptunnel.core.auth.TargetDiscovery}）。</p>
     */
    Collection<TargetDefinition> all();

    void register(TargetDefinition target);

    String defaultTargetName();
}
