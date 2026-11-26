package app.screenshow;

import java.io.BufferedInputStream;
import java.io.DataInputStream;
import java.io.EOFException;
import java.io.IOException;
import java.io.OutputStream;
import java.net.InetAddress;
import java.net.InetSocketAddress;
import java.net.Socket;
import java.net.SocketTimeoutException;
import java.nio.charset.StandardCharsets;
import java.security.GeneralSecurityException;
import java.security.MessageDigest;
import java.security.cert.CertificateException;
import java.security.cert.X509Certificate;
import java.util.ArrayDeque;
import java.util.Arrays;
import java.util.Base64;
import javax.net.ssl.SSLContext;
import javax.net.ssl.SSLException;
import javax.net.ssl.SSLSocket;
import javax.net.ssl.TrustManager;
import javax.net.ssl.X509TrustManager;

final class RemoteSession {
    private static final int TIMEOUT = 8000;
    private static final int MAX_FRAME = 4 * 1024 * 1024;
    private static final int MAX_PENDING = 512;
    static final int MOVE = 1;
    static final int BUTTON = 2;
    static final int WHEEL = 3;
    static final int KEY = 4;
    static final int RELEASE = 5;
    static final int TEXT = 6;

    static final class Invitation {
        final InetSocketAddress address;
        final byte[] token;
        final byte[] fingerprint;
        final String code;

        private Invitation(InetSocketAddress address, byte[] token, byte[] fingerprint, String code) {
            this.address = address;
            this.token = token;
            this.fingerprint = fingerprint;
            this.code = code;
        }

        static Invitation parse(String value) {
            String code = value.trim();
            if ((!code.startsWith("SS1-") && !code.startsWith("SS2-")) || code.length() != 87)
                throw new IllegalArgumentException("Вставьте приглашение ScreenShow.");
            byte[] data;
            try {
                data = Base64.getUrlDecoder().decode(code.substring(4));
            } catch (IllegalArgumentException error) {
                throw new IllegalArgumentException("В приглашении есть недопустимые символы.");
            }
            if (data.length != 62) throw new IllegalArgumentException("В приглашении неверный формат.");
            int first = data[0] & 255;
            int port = (data[4] & 255) * 256 + (data[5] & 255);
            if (first == 0 || first >= 224 || port == 0)
                throw new IllegalArgumentException("В приглашении указан недопустимый адрес.");
            try {
                InetAddress ip = InetAddress.getByAddress(Arrays.copyOfRange(data, 0, 4));
                return new Invitation(new InetSocketAddress(ip, port), Arrays.copyOfRange(data, 6, 30),
                    Arrays.copyOfRange(data, 30, 62), code);
            } catch (IOException error) {
                throw new IllegalArgumentException("В приглашении указан недопустимый адрес.", error);
            }
        }

        boolean canSave() { return code.startsWith("SS2-"); }

        String deviceKey() { return Base64.getUrlEncoder().withoutPadding().encodeToString(fingerprint); }
    }

    static final class Command {
        final int kind, x, y, value;

        Command(int kind, int x, int y, int value) {
            this.kind = kind;
            this.x = x;
            this.y = y;
            this.value = value;
        }

        void write(OutputStream stream, byte[] packet) throws IOException {
            packet[0] = (byte) kind;
            packet[1] = (byte) x;
            packet[2] = (byte) (x >>> 8);
            packet[3] = (byte) y;
            packet[4] = (byte) (y >>> 8);
            for (int index = 0; index < 4; index++) {
                packet[5 + index] = (byte) (value >>> (index * 8));
            }
            stream.write(packet);
        }
    }

    interface Listener {
        void waiting();
        void connected(boolean control, boolean text);
        void frame(byte[] jpeg) throws IOException;
        void ended(String message, boolean error);
    }

    private final Invitation invitation;
    private final Listener listener;
    private final ArrayDeque<Command> commands = new ArrayDeque<>();
    private Socket socket;
    private boolean stopped;
    private boolean canControl;

    RemoteSession(Invitation invitation, Listener listener) {
        this.invitation = invitation;
        this.listener = listener;
    }

    void start() {
        new Thread(this::run, "ScreenShow receiver").start();
    }

    private void run() {
        String message = "Сеанс завершён.";
        boolean error = false;
        try {
            SSLContext tls = SSLContext.getInstance("TLS");
            tls.init(null, new TrustManager[] { new PinnedServer(invitation.fingerprint) }, null);
            if (!connect(tls, true) && !connect(tls, false))
                throw new IOException("Приглашение недействительно. Запросите новое.");
        } catch (EOFException ignored) {
            // The host closed the session.
        } catch (SSLException failure) {
            message = "Не удалось проверить защищённое подключение. Запросите новое приглашение.";
            error = true;
        } catch (SocketTimeoutException failure) {
            message = "Компьютер не отвечает. Проверьте Wi-Fi или VPN и создайте новое приглашение.";
            error = true;
        } catch (GeneralSecurityException failure) {
            message = "Не удалось открыть защищённое подключение.";
            error = true;
        } catch (IOException failure) {
            message = failure.getMessage();
            error = true;
        } finally {
            boolean cancelled;
            synchronized (this) {
                cancelled = stopped;
            }
            stop();
            listener.ended(cancelled ? "Сеанс завершён." : message, !cancelled && error);
        }
    }

    private boolean connect(SSLContext context, boolean mobile) throws IOException {
        try (Socket transport = new Socket()) {
            synchronized (this) {
                if (stopped) return true;
                socket = transport;
            }
            try {
                transport.connect(invitation.address, TIMEOUT);
            } catch (SocketTimeoutException failure) {
                throw failure;
            } catch (IOException failure) {
                throw new IOException("Не удалось связаться с ПК. Проверьте сеть и брандмауэр Windows.", failure);
            }
            transport.setTcpNoDelay(true);
            transport.setSoTimeout(TIMEOUT);
            try (SSLSocket tls = (SSLSocket) context.getSocketFactory().createSocket(
                    transport, invitation.address.getAddress().getHostAddress(), invitation.address.getPort(), true)) {
                tls.setEnabledProtocols(new String[] { "TLSv1.2" });
                tls.startHandshake();
                OutputStream output = tls.getOutputStream();
                byte[] hello = new byte[28];
                byte[] version = (mobile ? "SSM1" : "SSH1").getBytes(StandardCharsets.US_ASCII);
                System.arraycopy(version, 0, hello, 0, version.length);
                System.arraycopy(invitation.token, 0, hello, version.length, invitation.token.length);
                output.write(hello);
                listener.waiting();
                tls.setSoTimeout(0);
                DataInputStream input = new DataInputStream(new BufferedInputStream(tls.getInputStream()));
                int decision = input.readUnsignedByte();
                if (decision == 3) return false;
                if (decision == 0) throw new IOException("Владелец отклонил подключение.");
                if (decision == 4) throw new IOException("Время подтверждения истекло.");
                if (decision != 1 && decision != 2) throw new IOException("Получен неизвестный ответ от компьютера.");

                tls.setSoTimeout(TIMEOUT);
                synchronized (this) {
                    if (stopped) return true;
                    canControl = decision == 2;
                    if (canControl) new Thread(() -> writeCommands(output), "ScreenShow sender").start();
                }
                listener.connected(decision == 2, mobile && decision == 2);
                while (true) listener.frame(readFrame(input));
            }
        }
    }

    static byte[] readFrame(DataInputStream input) throws IOException {
        int size = Integer.reverseBytes(input.readInt());
        if (size <= 0 || size > MAX_FRAME) throw new IOException("Получен кадр недопустимого размера.");
        byte[] frame = new byte[size];
        input.readFully(frame);
        return frame;
    }

    synchronized void queue(Command command) {
        if (stopped || !canControl) return;
        if (command.kind == RELEASE) commands.clear();
        Command last = commands.peekLast();
        if (command.kind == MOVE && last != null && last.kind == MOVE) commands.removeLast();
        if (commands.size() >= MAX_PENDING) {
            commands.clear();
            commands.add(new Command(RELEASE, 0, 0, 0));
        } else {
            commands.add(command);
        }
        notifyAll();
    }

    private void writeCommands(OutputStream stream) {
        byte[] packet = new byte[9];
        try {
            while (true) {
                Command command;
                synchronized (this) {
                    while (!stopped && commands.isEmpty()) wait();
                    if (stopped) return;
                    command = commands.removeFirst();
                }
                command.write(stream, packet);
            }
        } catch (InterruptedException failure) {
            Thread.currentThread().interrupt();
            stop();
        } catch (IOException failure) {
            stop();
        }
    }

    void stop() {
        Socket close;
        synchronized (this) {
            if (stopped) return;
            stopped = true;
            close = socket;
            commands.clear();
            notifyAll();
        }
        if (close != null) {
            try {
                close.close();
            } catch (IOException ignored) {
                // The socket has already been shut down.
            }
        }
    }

    private static final class PinnedServer implements X509TrustManager {
        private final byte[] fingerprint;

        PinnedServer(byte[] fingerprint) {
            this.fingerprint = fingerprint;
        }

        @Override public void checkServerTrusted(X509Certificate[] chain, String authType) throws CertificateException {
            if (chain == null || chain.length == 0) throw new CertificateException("Missing server certificate.");
            try {
                byte[] actual = MessageDigest.getInstance("SHA-256").digest(chain[0].getEncoded());
                if (!MessageDigest.isEqual(fingerprint, actual)) throw new CertificateException("Certificate mismatch.");
                chain[0].checkValidity();
            } catch (GeneralSecurityException failure) {
                throw new CertificateException("Invalid server certificate.", failure);
            }
        }

        @Override public void checkClientTrusted(X509Certificate[] chain, String authType) throws CertificateException {
            throw new CertificateException("Client authentication is unsupported.");
        }

        @Override public X509Certificate[] getAcceptedIssuers() {
            return new X509Certificate[0];
        }
    }
}
