package io.github.objectyan.cryptunnel.core.tunnel;

import io.github.objectyan.cryptunnel.core.auth.AuthMessage;
import io.github.objectyan.cryptunnel.core.target.TargetDefinition;
import io.github.objectyan.cryptunnel.core.target.TargetRegistry;
import java.io.IOException;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.ConcurrentMap;
import java.util.concurrent.atomic.AtomicInteger;

public final class DefaultAuthenticatedSessionManager implements AuthenticatedSessionManager {

    private final TargetRegistry registry;
    private final TcpTunnel tunnel;
    private final NonceCache nonceCache;
    private final long authTimeWindowSeconds;
    private final ConcurrentMap<String, AtomicInteger> connectionCounts = new ConcurrentHashMap<String, AtomicInteger>();

    public DefaultAuthenticatedSessionManager(TargetRegistry registry,
                                              TcpTunnel tunnel,
                                              NonceCache nonceCache,
                                              long authTimeWindowSeconds) {
        this.registry = registry;
        this.tunnel = tunnel;
        this.nonceCache = nonceCache;
        this.authTimeWindowSeconds = authTimeWindowSeconds;
    }

    @Override
    public AuthenticatedSession authenticate(AuthMessage msg) throws IOException {
        long now = System.currentTimeMillis() / 1000L;
        long timeDiff = Math.abs(now - msg.getTimestamp());
        if (timeDiff > authTimeWindowSeconds) {
            throw new AuthFailedException(
                    "timestamp out of window (diff=" + timeDiff + "s, window=" + authTimeWindowSeconds + "s)");
        }

        TargetDefinition target;
        try {
            target = registry.get(msg.getTargetId());
        } catch (RuntimeException e) {
            throw new AuthFailedException("target not found: " + msg.getTargetId(), e);
        }

        if (!target.getAuthKey().equals(msg.getAuthKey())) {
            throw new AuthFailedException("auth key invalid for target " + target.getName());
        }

        if (!nonceCache.tryConsume(msg.getNonce())) {
            throw new AuthFailedException("nonce replay detected");
        }

        AtomicInteger counter = connectionCounts.get(target.getName());
        if (counter == null) {
            AtomicInteger newCounter = new AtomicInteger(0);
            counter = connectionCounts.putIfAbsent(target.getName(), newCounter);
            if (counter == null) {
                counter = newCounter;
            }
        }
        int current = counter.incrementAndGet();
        if (current > target.getMaxConnections()) {
            counter.decrementAndGet();
            throw new TargetCapacityExceededException(target.getName());
        }

        try {
            MySqlConnection mysql = tunnel.open(target);
            return new AuthenticatedSession(target, mysql);
        } catch (IOException e) {
            counter.decrementAndGet();
            throw e;
        }
    }

    public void release(String targetName) {
        AtomicInteger counter = connectionCounts.get(targetName);
        if (counter != null) {
            counter.decrementAndGet();
        }
    }

    public int currentConnections(String targetName) {
        AtomicInteger counter = connectionCounts.get(targetName);
        return counter == null ? 0 : counter.get();
    }
}
