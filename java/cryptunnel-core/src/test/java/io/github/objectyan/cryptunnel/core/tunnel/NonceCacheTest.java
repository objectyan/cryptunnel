package io.github.objectyan.cryptunnel.core.tunnel;

import org.junit.Test;
import static org.junit.Assert.assertFalse;
import static org.junit.Assert.assertTrue;

public class NonceCacheTest {

    @Test
    public void firstUse_returnsTrue() {
        NonceCache c = new NonceCache(60_000);
        assertTrue(c.tryConsume("nonce-1"));
    }

    @Test
    public void secondUseOfSameNonce_returnsFalse() {
        NonceCache c = new NonceCache(60_000);
        assertTrue(c.tryConsume("nonce-1"));
        assertFalse(c.tryConsume("nonce-1"));
    }

    @Test
    public void differentNonces_bothPass() {
        NonceCache c = new NonceCache(60_000);
        assertTrue(c.tryConsume("nonce-1"));
        assertTrue(c.tryConsume("nonce-2"));
    }

    @Test
    public void nullOrEmptyNonce_returnsFalse() {
        NonceCache c = new NonceCache(60_000);
        assertFalse(c.tryConsume(null));
        assertFalse(c.tryConsume(""));
    }

    @Test
    public void expiredNoncesAreReclaimed() throws Exception {
        NonceCache c = new NonceCache(100);
        c.tryConsume("old");
        Thread.sleep(150);
        assertTrue(c.tryConsume("old"));
    }
}
