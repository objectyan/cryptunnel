package io.github.objectyan.cryptunnel.core.tunnel;

import io.github.objectyan.cryptunnel.core.target.TargetDefinition;

public final class AuthenticatedSession {

    private final TargetDefinition target;
    private final MySqlConnection mysql;

    public AuthenticatedSession(TargetDefinition target, MySqlConnection mysql) {
        if (target == null) {
            throw new IllegalArgumentException("target is null");
        }
        if (mysql == null) {
            throw new IllegalArgumentException("mysql is null");
        }
        this.target = target;
        this.mysql = mysql;
    }

    public TargetDefinition target() {
        return target;
    }

    public MySqlConnection mysql() {
        return mysql;
    }
}
