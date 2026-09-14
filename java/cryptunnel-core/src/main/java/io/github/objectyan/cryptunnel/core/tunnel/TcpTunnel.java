package io.github.objectyan.cryptunnel.core.tunnel;

import io.github.objectyan.cryptunnel.core.target.TargetDefinition;
import java.io.IOException;

public interface TcpTunnel {
    MySqlConnection open(TargetDefinition target) throws IOException;
}
