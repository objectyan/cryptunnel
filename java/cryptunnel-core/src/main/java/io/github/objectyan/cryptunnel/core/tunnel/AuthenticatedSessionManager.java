package io.github.objectyan.cryptunnel.core.tunnel;

import io.github.objectyan.cryptunnel.core.auth.AuthMessage;
import java.io.IOException;

public interface AuthenticatedSessionManager {

    AuthenticatedSession authenticate(AuthMessage msg) throws IOException;

    /**
     * 与 {@link #authenticate(AuthMessage)} 等价，但在「认证参数已校验通过、目标库 TCP
     * 尚未连接」的时点回调 {@code beforeTargetConnect}。
     *
     * <p><b>为什么要这个时点</b>：服务端原本把「认证成功」编码为「目标库主动发来首个
     * 数据帧」，因此不显式回 ACK。PostgreSQL 是客户端先发 StartupMessage，双方会
     * 互相等待而死锁（ADR-0005）。补发 ACK 后若仍放在 {@code tunnel.open()} 之后，
     * 目标库建连慢（跨网 / DNS / 防火墙丢包）时 ACK 会被一起拖住，客户端仍在认证
     * 阶段超时——ACK 必须比任何可能阻塞的目标库 I/O 更早。</p>
     *
     * <p>回调只在 timestamp / target / authKey / nonce / 容量全部校验通过之后触发；
     * 认证失败与容量超限都不会触发。回调抛异常按认证失败处理，已预留的容量会回滚。</p>
     *
     * <p>默认实现忽略回调并退化为单参方法，保证第三方实现源码与二进制兼容。</p>
     */
    default AuthenticatedSession authenticate(AuthMessage msg, Runnable beforeTargetConnect)
            throws IOException {
        return authenticate(msg);
    }
}
