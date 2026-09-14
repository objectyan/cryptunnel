package io.github.objectyan.cryptunnel.core.auth;

import java.security.SecureRandom;

public final class AuthMessageCodec {

    private static final SecureRandom SECURE_RANDOM = new SecureRandom();

    private AuthMessageCodec() {
    }

    public static AuthMessage parse(String payload) {
        if (payload == null) {
            throw new IllegalArgumentException("payload is null");
        }
        if (!payload.startsWith("AUTH:")) {
            throw new IllegalArgumentException("payload does not start with AUTH:");
        }
        // 去掉 "AUTH:" 前缀后，剩余按 ":" split
        String body = payload.substring("AUTH:".length());
        String[] parts = body.split(":", -1);

        if (parts.length == 3) {
            // 老格式：AUTH:key:ts:nonce -> 去前缀后 3 段
            String authKey = parts[0];
            long ts = parseLong(parts[1], "timestamp");
            String nonce = parts[2];
            return new AuthMessage(authKey, ts, nonce, null);
        }
        if (parts.length == 4) {
            // 新格式：AUTH:key:ts:nonce:target -> 去前缀后 4 段
            String authKey = parts[0];
            long ts = parseLong(parts[1], "timestamp");
            String nonce = parts[2];
            String targetId = parts[3];
            return new AuthMessage(authKey, ts, nonce, targetId);
        }
        throw new IllegalArgumentException(
                "AUTH payload has " + (parts.length + 1) + " parts; expected 4 or 5");
    }

    public static String encode(String authKey, String targetId) {
        long ts = System.currentTimeMillis() / 1000L;
        byte[] nonceBytes = new byte[16];
        SECURE_RANDOM.nextBytes(nonceBytes);
        StringBuilder sb = new StringBuilder(80);
        sb.append("AUTH:").append(authKey).append(":").append(ts).append(":");
        appendHexLower(sb, nonceBytes);
        if (targetId != null && !targetId.isEmpty()) {
            sb.append(":").append(targetId);
        }
        return sb.toString();
    }

    public static String encode(String authKey) {
        return encode(authKey, null);
    }

    private static long parseLong(String s, String field) {
        try {
            return Long.parseLong(s);
        } catch (NumberFormatException e) {
            throw new IllegalArgumentException(field + " is not a number: " + s);
        }
    }

    private static void appendHexLower(StringBuilder sb, byte[] bytes) {
        for (byte b : bytes) {
            sb.append(Character.forDigit((b >> 4) & 0xF, 16));
            sb.append(Character.forDigit(b & 0xF, 16));
        }
    }
}
