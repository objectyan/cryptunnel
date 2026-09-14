package io.github.objectyan.cryptunnel.core.tunnel;

public class TargetCapacityExceededException extends RuntimeException {

    private final String targetId;

    public TargetCapacityExceededException(String targetId) {
        super("Target capacity exceeded: " + targetId);
        this.targetId = targetId;
    }

    public String getTargetId() {
        return targetId;
    }
}
