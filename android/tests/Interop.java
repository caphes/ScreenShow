package app.screenshow;

import java.awt.image.BufferedImage;
import java.io.BufferedReader;
import java.io.ByteArrayInputStream;
import java.io.IOException;
import java.io.InputStreamReader;
import java.nio.charset.StandardCharsets;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import javax.imageio.ImageIO;

public final class Interop implements RemoteSession.Listener {
    private final CountDownLatch done = new CountDownLatch(1);
    private final String scenario;
    private final RemoteSession remote;
    private boolean authorized, received, error;

    private Interop(String code, String scenario) {
        this.scenario = scenario;
        remote = new RemoteSession(RemoteSession.Invitation.parse(code), this);
    }

    public static void main(String[] arguments) throws Exception {
        BufferedReader input = new BufferedReader(new InputStreamReader(System.in, StandardCharsets.UTF_8));
        Interop test = new Interop(input.readLine(), arguments[0]);
        test.remote.start();
        try {
            if (!test.done.await(15, TimeUnit.SECONDS)) throw new AssertionError("Viewer timed out.");
            boolean shouldFail = test.scenario.equals("bad-pin");
            if (test.error != shouldFail || test.authorized == shouldFail || test.received == shouldFail)
                throw new AssertionError("Unexpected result for " + test.scenario);
            System.out.println("PASS: " + test.scenario);
        } finally {
            test.remote.stop();
        }
    }

    @Override public void waiting() { }

    @Override public void connected(boolean control, boolean text) {
        boolean expectedControl = !scenario.equals("view");
        boolean expectedText = scenario.equals("text");
        authorized = control == expectedControl && text == expectedText;
        if (expectedText) remote.queue(new RemoteSession.Command(RemoteSession.TEXT, 0, 0, 0x416));
    }

    @Override public void frame(byte[] jpeg) throws IOException {
        BufferedImage image = ImageIO.read(new ByteArrayInputStream(jpeg));
        if (image == null || image.getWidth() > 1600 || image.getHeight() > 1000)
            throw new IOException("Invalid JPEG.");
        received = true;
        remote.stop();
    }

    @Override public void ended(String message, boolean error) {
        this.error = error;
        done.countDown();
    }
}
