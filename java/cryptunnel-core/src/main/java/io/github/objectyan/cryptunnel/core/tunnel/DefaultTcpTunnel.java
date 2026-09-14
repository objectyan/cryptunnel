package io.github.objectyan.cryptunnel.core.tunnel;

import io.github.objectyan.cryptunnel.core.target.TargetDefinition;
import java.io.IOException;
import java.net.InetSocketAddress;
import java.net.Socket;

public final class DefaultTcpTunnel implements TcpTunnel {

    @Override
    public MySqlConnection open(TargetDefinition target) throws IOException {
        if (target == null) {
            throw new IllegalArgumentException("target is null");
        }
        Socket s = new Socket();
        s.connect(new InetSocketAddress(target.getMysqlHost(), target.getMysqlPort()),
                target.getConnectTimeoutMs());
        s.setTcpNoDelay(true);
        s.setKeepAlive(true);
        if (target.getReadTimeoutMs() > 0) {
            s.setSoTimeout(target.getReadTimeoutMs());
        }
        return new MySqlConnection(s);
    }
}
