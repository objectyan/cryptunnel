package io.github.objectyan.cryptunnel.core.target;

import java.util.ArrayList;
import java.util.Collection;
import java.util.Collections;
import java.util.HashSet;
import java.util.List;
import java.util.Set;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.ConcurrentMap;

public final class DefaultTargetRegistry implements TargetRegistry {

    private final ConcurrentMap<String, TargetDefinition> map = new ConcurrentHashMap<String, TargetDefinition>();
    private final String defaultTargetName;

    public DefaultTargetRegistry(List<TargetDefinition> targets, String defaultTargetName) {
        if (targets == null || targets.isEmpty()) {
            throw new IllegalStateException(
                    "at least one target must be configured");
        }
        for (TargetDefinition t : targets) {
            TargetDefinition prev = map.putIfAbsent(t.getName(), t);
            if (prev != null) {
                throw new IllegalStateException("duplicate target name: " + t.getName());
            }
        }
        String resolvedDefault = defaultTargetName;
        if (resolvedDefault == null || resolvedDefault.isEmpty()) {
            resolvedDefault = targets.get(0).getName();
        }
        if (!map.containsKey(resolvedDefault)) {
            throw new IllegalStateException(
                    "default-target '" + resolvedDefault + "' is not in the registry");
        }
        this.defaultTargetName = resolvedDefault;
    }

    @Override
    public TargetDefinition get(String targetId) {
        String key = (targetId == null || targetId.isEmpty()) ? defaultTargetName : targetId;
        TargetDefinition t = map.get(key);
        if (t == null) {
            throw new TargetNotFoundException(key);
        }
        return t;
    }

    @Override
    public Set<String> names() {
        return Collections.unmodifiableSet(new HashSet<String>(map.keySet()));
    }

    @Override
    public Collection<TargetDefinition> all() {
        return Collections.unmodifiableCollection(new ArrayList<TargetDefinition>(map.values()));
    }

    @Override
    public void register(TargetDefinition target) {
        if (target == null) {
            throw new IllegalArgumentException("target is null");
        }
        map.put(target.getName(), target);
    }

    @Override
    public String defaultTargetName() {
        return defaultTargetName;
    }
}
