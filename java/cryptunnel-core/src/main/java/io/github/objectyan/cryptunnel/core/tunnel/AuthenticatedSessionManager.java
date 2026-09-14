package io.github.objectyan.cryptunnel.core.tunnel;

import io.github.objectyan.cryptunnel.core.auth.AuthMessage;
import java.io.IOException;

public interface AuthenticatedSessionManager {

    AuthenticatedSession authenticate(AuthMessage msg) throws IOException;
}
