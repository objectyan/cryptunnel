package io.github.objectyan.cryptunnel.core.tunnel;

import java.net.SocketException;

/**
 * reader 线程「会话正常收尾」噪音的判定（javax / jakarta 两份 handler 共用，故放 core）。
 *
 * <p><b>为什么需要它</b>：客户端先断开时，服务端的 {@code cleanup()} 会关闭 MySQL socket，
 * 而 reader 线程正阻塞在 {@code in.read()} 上 → read 抛 {@link SocketException}
 * （"Socket closed"）。这是关闭顺序的必然产物，<b>不是故障</b>；记成 ERROR + 全栈
 * 会让每一次正常断开都像事故。同一 handler 的 {@code handleTransportError}
 * 早已为同类形态做过降级治理，这里补上另一处遗漏。</p>
 *
 * <p><b>与「MySQL 侧连接被重置」的区分</b>：后者 socket 仍处于打开状态、会话也还活着，
 * 属于真实故障，必须保留 ERROR 日志。二者都表现为 {@code SocketException}，
 * 只能靠「socket 是否已关 / 会话是否已关」区分。</p>
 *
 * <p>core 是零依赖模块（不引入 servlet API），故以 boolean 传入两个状态，
 * 由调用方（starter 层）自行读取 {@code socket.isClosed()} 与 {@code wsSession.isOpen()}。</p>
 */
public final class ReaderShutdown {

    private ReaderShutdown() {
        // utility
    }

    /**
     * @param e            reader 线程捕获的异常
     * @param socketClosed MySQL socket 是否已被关闭
     * @param sessionOpen  WS 会话是否仍处于打开状态
     * @return true = 正常收尾噪音（记 INFO）；false = 真故障（记 ERROR）
     */
    public static boolean isNoise(Exception e, boolean socketClosed, boolean sessionOpen) {
        if (!(e instanceof SocketException)) {
            return false;
        }
        return socketClosed || !sessionOpen;
    }
}
