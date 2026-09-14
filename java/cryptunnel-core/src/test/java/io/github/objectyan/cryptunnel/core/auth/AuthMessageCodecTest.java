package io.github.objectyan.cryptunnel.core.auth;

import org.junit.Test;
import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertNull;
import static org.junit.Assert.fail;

public class AuthMessageCodecTest {

    @Test
    public void parse_fourSegment_targetIdIsNull() {
        AuthMessage m = AuthMessageCodec.parse("AUTH:mykey:1700000000:abc123");
        assertEquals("mykey", m.getAuthKey());
        assertEquals(1700000000L, m.getTimestamp());
        assertEquals("abc123", m.getNonce());
        assertNull(m.getTargetId());
    }

    @Test
    public void parse_fiveSegment_targetIdIsFifth() {
        AuthMessage m = AuthMessageCodec.parse("AUTH:mykey:1700000000:abc:mes-test");
        assertEquals("mykey", m.getAuthKey());
        assertEquals(1700000000L, m.getTimestamp());
        assertEquals("abc", m.getNonce());
        assertEquals("mes-test", m.getTargetId());
    }

    @Test
    public void parse_missingAuthPrefix_throws() {
        try {
            AuthMessageCodec.parse("XXX:mykey:1700000000:abc");
            fail("expected IllegalArgumentException");
        } catch (IllegalArgumentException e) {
            // expected
        }
    }

    @Test
    public void parse_wrongSegmentCount_throws() {
        try {
            AuthMessageCodec.parse("AUTH:mykey:1700000000");
            fail("expected IllegalArgumentException for 3 parts");
        } catch (IllegalArgumentException e) {
            // expected
        }
        try {
            AuthMessageCodec.parse("AUTH:mykey:1700000000:abc:target:extra");
            fail("expected IllegalArgumentException for 6 parts");
        } catch (IllegalArgumentException e) {
            // expected
        }
    }

    @Test
    public void parse_nullPayload_throws() {
        try {
            AuthMessageCodec.parse(null);
            fail("expected IllegalArgumentException for null");
        } catch (IllegalArgumentException e) {
            // expected
        }
    }

    @Test
    public void parse_nonNumericTimestamp_throws() {
        try {
            AuthMessageCodec.parse("AUTH:mykey:notanumber:abc");
            fail("expected IllegalArgumentException");
        } catch (IllegalArgumentException e) {
            // expected
        }
    }

    @Test
    public void encode_withNullTarget_emitsFourSegments() {
        String s = AuthMessageCodec.encode("mykey", null);
        String[] parts = s.split(":");
        // AUTH:mykey:ts:nonce = 4
        assertEquals(4, parts.length);
        assertEquals("AUTH", parts[0]);
        assertEquals("mykey", parts[1]);
        // 16 字节 nonce -> 32 字符 hex
        assertEquals(32, parts[3].length());
    }

    @Test
    public void encode_withTarget_emitsFiveSegments() {
        String s = AuthMessageCodec.encode("mykey", "cloudcc-prod");
        String[] parts = s.split(":");
        // AUTH:mykey:ts:nonce:target = 5
        assertEquals(5, parts.length);
        assertEquals("cloudcc-prod", parts[4]);
    }

    @Test
    public void roundTrip_parseEncode_parse_preservesTarget() {
        String s = AuthMessageCodec.encode("mykey", "mes-test");
        AuthMessage m = AuthMessageCodec.parse(s);
        assertEquals("mykey", m.getAuthKey());
        assertEquals("mes-test", m.getTargetId());
    }
}
