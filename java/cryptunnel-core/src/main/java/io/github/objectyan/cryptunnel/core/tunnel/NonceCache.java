package io.github.objectyan.cryptunnel.core.tunnel;

import java.util.Map;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.ConcurrentMap;

public final class NonceCache {

    private final ConcurrentMap<String, Long> used = new ConcurrentHashMap<String, Long>();
    private final long windowMs;

    public NonceCache(long windowMs) {
        if (windowMs < 10) {
            throw new IllegalArgumentException("windowMs must be >= 10");
        }
        this.windowMs = windowMs;
    }

    public boolean tryConsume(String nonce) {
        if (nonce == null || nonce.isEmpty()) {
            return false;
        }
        long now = System.currentTimeMillis();
        cleanup(now);
        return used.putIfAbsent(nonce, now) == null;
    }

    public int size() {
        return used.size();
    }

    private void cleanup(long now) {
        long threshold = now - windowMs;
        for (Map.Entry<String, Long> e : used.entrySet()) {
            if (e.getValue() < threshold) {
                used.remove(e.getKey());
            }
        }
    }
}
