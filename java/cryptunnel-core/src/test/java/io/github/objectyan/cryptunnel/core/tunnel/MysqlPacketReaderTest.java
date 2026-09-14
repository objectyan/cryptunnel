package io.github.objectyan.cryptunnel.core.tunnel;

import java.io.ByteArrayInputStream;
import java.io.IOException;
import org.junit.Test;
import static org.junit.Assert.assertEquals;

public class MysqlPacketReaderTest {

    /**
     * 构造一个标准 MySQL 包：header[3+1] + payload。
     */
    private byte[] buildPacket(int payloadLen, byte payloadSeed) {
        byte[] buf = new byte[4 + payloadLen];
        buf[0] = (byte) (payloadLen & 0xFF);
        buf[1] = (byte) ((payloadLen >> 8) & 0xFF);
        buf[2] = (byte) ((payloadLen >> 16) & 0xFF);
        buf[3] = 0;  // sequence
        for (int i = 0; i < payloadLen; i++) {
            buf[4 + i] = (byte) ((payloadSeed + i) & 0xFF);
        }
        return buf;
    }

    @Test
    public void readPacket_completeOnePacket_returnsAllBytes() throws IOException {
        byte[] packet = buildPacket(64, (byte) 0x10);
        ByteArrayInputStream in = new ByteArrayInputStream(packet);
        byte[] buf = new byte[8192];
        int n = MysqlPacketReader.readPacket(in, buf, 5000);
        assertEquals(packet.length, n);
        for (int i = 0; i < packet.length; i++) {
            assertEquals(packet[i], buf[i]);
        }
    }

    @Test
    public void readPacket_zeroLengthPayload() throws IOException {
        byte[] packet = buildPacket(0, (byte) 0);
        ByteArrayInputStream in = new ByteArrayInputStream(packet);
        byte[] buf = new byte[8192];
        int n = MysqlPacketReader.readPacket(in, buf, 5000);
        assertEquals(4, n);
    }

    @Test
    public void readPacket_streamEnd_returnsMinusOne() throws IOException {
        ByteArrayInputStream in = new ByteArrayInputStream(new byte[0]);
        byte[] buf = new byte[8192];
        int n = MysqlPacketReader.readPacket(in, buf, 5000);
        assertEquals(-1, n);
    }
}
