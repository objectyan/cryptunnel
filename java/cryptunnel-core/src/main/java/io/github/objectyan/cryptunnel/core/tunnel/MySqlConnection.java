package io.github.objectyan.cryptunnel.core.tunnel;

import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.Socket;

public final class MySqlConnection implements AutoCloseable {

    private final Socket socket;
    private final InputStream in;
    private final OutputStream out;

    public MySqlConnection(Socket socket) throws IOException {
        if (socket == null) {
            throw new IllegalArgumentException("socket is null");
        }
        this.socket = socket;
        this.in = socket.getInputStream();
        this.out = socket.getOutputStream();
    }

    public Socket socket() {
        return socket;
    }

    public InputStream in() {
        return in;
    }

    public OutputStream out() {
        return out;
    }

    public boolean isOpen() {
        return !socket.isClosed() && socket.isConnected();
    }

    @Override
    public void close() throws IOException {
        socket.close();
    }
}
