package io.github.objectyan.cryptunnel.core.target;

/**
 * target 不存在时抛出。
 */
public class TargetNotFoundException extends RuntimeException {

    private final String targetId;

    public TargetNotFoundException(String targetId) {
        super("Target not found: " + targetId);
        this.targetId = targetId;
    }

    public String getTargetId() {
        return targetId;
    }
}
