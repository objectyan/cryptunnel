package io.github.objectyan.cryptunnel.core.target;

import java.util.Arrays;
import java.util.Collections;
import org.junit.Test;
import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertTrue;
import static org.junit.Assert.fail;

public class DefaultTargetRegistryTest {

    private TargetDefinition make(String name) {
        return new TargetDefinition(name, null, "10.0.0.1", 3306, "aes-key-xxxx", "auth-key-yyyy",
                5, 10000, 0);
    }

    @Test
    public void register_targets_lookupByName() {
        DefaultTargetRegistry r = new DefaultTargetRegistry(
                Arrays.asList(make("a"), make("b")), "a");
        assertEquals("a", r.get("a").getName());
        assertEquals("b", r.get("b").getName());
    }

    @Test
    public void get_nullOrEmpty_routesToDefault() {
        DefaultTargetRegistry r = new DefaultTargetRegistry(
                Arrays.asList(make("a"), make("b")), "b");
        assertEquals("b", r.get(null).getName());
        assertEquals("b", r.get("").getName());
    }

    @Test
    public void get_unknownTarget_throws() {
        DefaultTargetRegistry r = new DefaultTargetRegistry(
                Collections.singletonList(make("a")), "a");
        try {
            r.get("missing");
            fail("expected TargetNotFoundException");
        } catch (TargetNotFoundException e) {
            assertEquals("missing", e.getTargetId());
        }
    }

    @Test
    public void defaultTargetName_fallsBackToFirstWhenUnset() {
        DefaultTargetRegistry r = new DefaultTargetRegistry(
                Arrays.asList(make("first"), make("second")), null);
        assertEquals("first", r.defaultTargetName());
    }

    @Test
    public void defaultTargetName_unregistered_throws() {
        try {
            new DefaultTargetRegistry(Collections.singletonList(make("a")), "not-a");
            fail("expected IllegalStateException");
        } catch (IllegalStateException e) {
            assertTrue(e.getMessage().contains("not-a"));
        }
    }

    @Test
    public void duplicateTargetName_throws() {
        try {
            new DefaultTargetRegistry(Arrays.asList(make("dup"), make("dup")), "dup");
            fail("expected IllegalStateException");
        } catch (IllegalStateException e) {
            assertTrue(e.getMessage().contains("duplicate"));
        }
    }

    @Test
    public void emptyList_throws() {
        try {
            new DefaultTargetRegistry(Collections.<TargetDefinition>emptyList(), null);
            fail("expected IllegalStateException");
        } catch (IllegalStateException e) {
            // expected
        }
    }

    @Test
    public void all_returnsAllRegistered() {
        DefaultTargetRegistry r = new DefaultTargetRegistry(
                Arrays.asList(make("a"), make("b"), make("c")), "a");
        assertEquals(3, r.all().size());
    }

    @Test
    public void all_isUnmodifiable() {
        DefaultTargetRegistry r = new DefaultTargetRegistry(
                Collections.singletonList(make("a")), "a");
        try {
            r.all().clear();
            fail("expected UnsupportedOperationException");
        } catch (UnsupportedOperationException e) {
            // expected
        }
    }
}
