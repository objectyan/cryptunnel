package io.github.objectyan.cryptunnel.core.auth;

import java.util.Objects;

/**
 * 解析后的认证报文。
 *
 * <p>4 段格式：{@code AUTH:{authKey}:{timestamp}:{nonce}}，{@link #targetId} 为 null。</p>
 * <p>5 段格式：{@code AUTH:{authKey}:{timestamp}:{nonce}:{targetId}}。</p>
 */
public final class AuthMessage {

    private final String authKey;
    private final long timestamp;
    private final String nonce;
    private final String targetId;

    public AuthMessage(String authKey, long timestamp, String nonce, String targetId) {
        this.authKey = Objects.requireNonNull(authKey, "authKey");
        this.timestamp = timestamp;
        this.nonce = Objects.requireNonNull(nonce, "nonce");
        this.targetId = targetId;
    }

    public String getAuthKey() {
        return authKey;
    }

    public long getTimestamp() {
        return timestamp;
    }

    public String getNonce() {
        return nonce;
    }

    public String getTargetId() {
        return targetId;
    }

    @Override
    public String toString() {
        return "AuthMessage{"
                + "authKey='***'"
                + ", timestamp=" + timestamp
                + ", nonce='***'"
                + ", targetId='" + targetId + '\''
                + '}';
    }
}
