package io.github.objectyan.cryptunnel.core.target;

import io.github.objectyan.cryptunnel.core.crypto.AesUtil;
import java.util.Objects;
import java.util.regex.Pattern;

/**
 * 一个 target = 一套目标 MySQL。
 *
 * <p>target 名必须全局唯一，匹配 {@code [a-z0-9-]{1,32}}。</p>
 *
 * <p>所有字段不可变；密钥相关字段（aesKey/authKey）不入 toString 防日志泄露。</p>
 */
public final class TargetDefinition {

    private static final Pattern NAME_PATTERN = Pattern.compile("^[a-z0-9-]{1,32}$");

    private final String name;
    private final String displayName;
    private final String mysqlHost;
    private final int mysqlPort;
    private final String aesKey;
    private final String authKey;
    private final int maxConnections;
    private final int connectTimeoutMs;
    private final int readTimeoutMs;

    public TargetDefinition(String name,
                           String displayName,
                           String mysqlHost,
                           int mysqlPort,
                           String aesKey,
                           String authKey,
                           int maxConnections,
                           int connectTimeoutMs,
                           int readTimeoutMs) {
        if (name == null || !NAME_PATTERN.matcher(name).matches()) {
            throw new IllegalArgumentException(
                    "name must match [a-z0-9-]{1,32}; got: " + name);
        }
        if (mysqlHost == null || mysqlHost.isEmpty()) {
            throw new IllegalArgumentException("mysqlHost is required");
        }
        if (mysqlPort < 1 || mysqlPort > 65535) {
            throw new IllegalArgumentException("mysqlPort out of range: " + mysqlPort);
        }
        if (aesKey == null || aesKey.length() < 8) {
            throw new IllegalArgumentException("aesKey length must be >= 8");
        }
        if (authKey == null || authKey.length() < 8) {
            throw new IllegalArgumentException("authKey length must be >= 8");
        }
        if (maxConnections < 1 || maxConnections > 1000) {
            throw new IllegalArgumentException("maxConnections out of range: " + maxConnections);
        }
        if (connectTimeoutMs < 1000 || connectTimeoutMs > 60000) {
            throw new IllegalArgumentException("connectTimeoutMs out of range: " + connectTimeoutMs);
        }
        if (readTimeoutMs != 0 && (readTimeoutMs < 1000 || readTimeoutMs > 600000)) {
            throw new IllegalArgumentException(
                    "readTimeoutMs must be 0 (unlimited) or in [1000, 600000]: " + readTimeoutMs);
        }

        this.name = name;
        this.displayName = displayName;
        this.mysqlHost = mysqlHost;
        this.mysqlPort = mysqlPort;
        this.aesKey = aesKey;
        this.authKey = authKey;
        this.maxConnections = maxConnections;
        this.connectTimeoutMs = connectTimeoutMs;
        this.readTimeoutMs = readTimeoutMs;
    }

    public String getName() {
        return name;
    }

    public String getDisplayName() {
        return displayName;
    }

    public String getMysqlHost() {
        return mysqlHost;
    }

    public int getMysqlPort() {
        return mysqlPort;
    }

    public String getAesKey() {
        return aesKey;
    }

    public String getAuthKey() {
        return authKey;
    }

    public int getMaxConnections() {
        return maxConnections;
    }

    public int getConnectTimeoutMs() {
        return connectTimeoutMs;
    }

    public int getReadTimeoutMs() {
        return readTimeoutMs;
    }

    /**
     * 派生 AES 密钥（每次重新派生，不缓存；调用方决定是否缓存 SecretKeySpec）。
     */
    public javax.crypto.spec.SecretKeySpec deriveAesKey() {
        return AesUtil.deriveAesKey(aesKey);
    }

    public javax.crypto.spec.SecretKeySpec deriveHmacKey() {
        return AesUtil.deriveHmacKey(aesKey);
    }

    @Override
    public String toString() {
        return "TargetDefinition{"
                + "name='" + name + '\''
                + ", displayName='" + displayName + '\''
                + ", mysqlHost='" + mysqlHost + '\''
                + ", mysqlPort=" + mysqlPort
                + ", maxConnections=" + maxConnections
                + ", connectTimeoutMs=" + connectTimeoutMs
                + ", readTimeoutMs=" + readTimeoutMs
                + '}';
    }

    @Override
    public boolean equals(Object o) {
        if (this == o) return true;
        if (!(o instanceof TargetDefinition)) return false;
        TargetDefinition that = (TargetDefinition) o;
        return Objects.equals(name, that.name);
    }

    @Override
    public int hashCode() {
        return Objects.hash(name);
    }
}
