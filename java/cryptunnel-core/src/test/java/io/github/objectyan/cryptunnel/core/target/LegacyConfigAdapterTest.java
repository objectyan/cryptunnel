package io.github.objectyan.cryptunnel.core.target;

import java.util.Collections;
import org.junit.Test;
import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertTrue;

public class LegacyConfigAdapterTest {

    @Test
    public void wrap_explicitTargetsReturnedAsIs() {
        TargetDefinition t = new TargetDefinition("x", null, "10.0.0.1", 3306,
                "aes-key-xxxx", "auth-key-yyyy", 5, 10000, 0);
        assertEquals(Collections.singletonList(t),
                LegacyConfigAdapter.wrap(Collections.singletonList(t), "ignored", 3306,
                        "a", "b", 5, 10000, 0));
    }

    @Test
    public void wrap_legacyFields_createDefaultTarget() {
        assertTrue(LegacyConfigAdapter.wrap(null,
                "10.6.10.22", 3306, "legacy-aes", "legacy-auth",
                5, 10000, 0).size() == 1);
        TargetDefinition def = LegacyConfigAdapter.wrap(null,
                "10.6.10.22", 3306, "legacy-aes", "legacy-auth",
                5, 10000, 0).get(0);
        assertEquals("default", def.getName());
        assertEquals("10.6.10.22", def.getMysqlHost());
        assertEquals("legacy-aes", def.getAesKey());
    }

    @Test
    public void wrap_legacyReadTimeoutZero_becomesFiveMinutes() {
        TargetDefinition def = LegacyConfigAdapter.wrap(null,
                "host", 3306, "aesaaaaaaaaaa", "authaaaaaaaaaa",
                5, 10000, 0).get(0);
        assertEquals(300_000, def.getReadTimeoutMs());
    }

    @Test
    public void wrap_legacyReadTimeoutNonZero_kept() {
        TargetDefinition def = LegacyConfigAdapter.wrap(null,
                "host", 3306, "aesaaaaaaaaaa", "authaaaaaaaaaa",
                5, 10000, 60000).get(0);
        assertEquals(60000, def.getReadTimeoutMs());
    }
}
