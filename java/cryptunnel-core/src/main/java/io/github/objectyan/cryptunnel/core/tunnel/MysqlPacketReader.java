package io.github.objectyan.cryptunnel.core.tunnel;

import java.io.IOException;
import java.io.InputStream;

public final class MysqlPacketReader {

    private static final int MAX_WAIT_MS = 30000;

    private MysqlPacketReader() {
    }

    public static int readPacket(InputStream in, byte[] buffer) throws IOException {
        return readPacket(in, buffer, MAX_WAIT_MS);
    }

    public static int readPacket(InputStream in, byte[] buffer, long maxWaitMs) throws IOException {
        if (in == null) {
            throw new IllegalArgumentException("in is null");
        }
        if (buffer == null || buffer.length < 4) {
            throw new IllegalArgumentException("buffer must be >= 4 bytes");
        }
        long start = System.currentTimeMillis();
        int totalRead = 0;
        while (totalRead < 4 && (System.currentTimeMillis() - start) < maxWaitMs) {
            int b = in.read();
            if (b == -1) {
                return totalRead > 0 ? totalRead : -1;
            }
            buffer[totalRead++] = (byte) b;
        }
        if (totalRead < 4) {
            return totalRead;
        }
        int payloadLen = (buffer[0] & 0xFF)
                | ((buffer[1] & 0xFF) << 8)
                | ((buffer[2] & 0xFF) << 16);
        int totalPacket = payloadLen + 4;
        if (totalPacket > buffer.length) {
            throw new IOException("MySQL packet length " + totalPacket
                    + " exceeds buffer " + buffer.length);
        }
        int remaining = totalPacket - totalRead;
        while (remaining > 0 && (System.currentTimeMillis() - start) < maxWaitMs) {
            int read = in.read(buffer, totalRead, Math.min(remaining, buffer.length - totalRead));
            if (read == -1) {
                break;
            }
            totalRead += read;
            remaining -= read;
        }
        try {
            if (in.available() > 0) {
                int avail = in.available();
                int canRead = Math.min(avail, buffer.length - totalRead);
                if (canRead > 0) {
                    int extra = in.read(buffer, totalRead, canRead);
                    if (extra > 0) {
                        totalRead += extra;
                    }
                }
            }
        } catch (IOException ignored) {
        }
        return totalRead;
    }
}
