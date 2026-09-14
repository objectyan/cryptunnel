package io.github.objectyan.cryptunnel.core.target;

import java.util.Collections;
import java.util.List;

public final class LegacyConfigAdapter {

    public static final String DEFAULT_TARGET_NAME = "default";
    public static final int DEFAULT_READ_TIMEOUT_MS = 300000;

    private LegacyConfigAdapter() {
    }

    public static List<TargetDefinition> wrap(
            List<TargetDefinition> explicitTargets,
            String legacyMysqlHost,
            int legacyMysqlPort,
            String legacyAesKey,
            String legacyAuthKey,
            int legacyMaxConn,
            int legacyConnectMs,
            int legacyReadMs) {

        if (explicitTargets != null && !explicitTargets.isEmpty()) {
            return explicitTargets;
        }

        int readMs = legacyReadMs == 0 ? DEFAULT_READ_TIMEOUT_MS : legacyReadMs;

        TargetDefinition def = new TargetDefinition(
                DEFAULT_TARGET_NAME,
                "default (legacy config)",
                legacyMysqlHost,
                legacyMysqlPort,
                legacyAesKey,
                legacyAuthKey,
                legacyMaxConn <= 0 ? 5 : legacyMaxConn,
                legacyConnectMs <= 0 ? 10000 : legacyConnectMs,
                readMs
        );
        return Collections.singletonList(def);
    }
}
