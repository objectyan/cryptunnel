package io.github.objectyan.cryptunnel.core.tunnel;

public class AuthFailedException extends RuntimeException {

    private final String reason;

    public AuthFailedException(String reason) {
        super("Auth failed: " + reason);
        this.reason = reason;
    }

    public AuthFailedException(String reason, Throwable cause) {
        super("Auth failed: " + reason, cause);
        this.reason = reason;
    }

    public String getReason() {
        return reason;
    }
}
