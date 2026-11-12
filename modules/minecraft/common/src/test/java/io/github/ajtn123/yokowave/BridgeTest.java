package io.github.ajtn123.yokowave;

import java.util.List;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import java.util.function.BooleanSupplier;
import org.junit.jupiter.api.Test;
import static org.junit.jupiter.api.Assertions.*;

class BridgeTest {
    @Test void disconnectCancelsAConnectionInProgressWithoutEnablingOutput() throws Exception {
        var output = new Output();
        try (var bridge = new GameBridge((settings, token) -> output)) {
            bridge.connect(Settings.defaults(), "");
            assertTrue(output.connecting.await(2, TimeUnit.SECONDS));
            bridge.disconnect();
            output.allowConnection.countDown();
            await(() -> !bridge.busy());
            assertFalse(bridge.connected());
            assertFalse(bridge.armed());
            assertTrue(output.closed);
            assertEquals(1, output.updated.getCount());
        }
    }

    @Test void configurationStopsOutputAndReusesTheConnectionAndCloseWaitsForCleanup() throws Exception {
        var output = new Output();
        output.allowConnection.countDown();
        try (var bridge = new GameBridge((settings, token) -> output)) {
            bridge.connect(Settings.defaults(), "");
            await(bridge::connected);
            bridge.arm(true);
            bridge.offer(List.of(new GameMonitor.Event("damage", 4d)));
            assertTrue(output.updated.await(2, TimeUnit.SECONDS));
            bridge.configure(Settings.defaults());
            assertTrue(output.configured.await(2, TimeUnit.SECONDS));
            assertFalse(bridge.armed());
            assertTrue(bridge.connected());
            assertFalse(output.closed);
        }
        assertTrue(output.closed);
    }

    private static void await(BooleanSupplier condition) throws Exception {
        long deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(2);
        while (!condition.getAsBoolean() && System.nanoTime() < deadline) Thread.sleep(10);
        assertTrue(condition.getAsBoolean());
    }

    private static final class Output implements OutputConnection {
        final CountDownLatch connecting = new CountDownLatch(1);
        final CountDownLatch allowConnection = new CountDownLatch(1);
        final CountDownLatch updated = new CountDownLatch(1);
        final CountDownLatch configured = new CountDownLatch(1);
        volatile boolean closed;
        @Override public void connect() throws Exception { connecting.countDown(); allowConnection.await(2, TimeUnit.SECONDS); }
        @Override public void configure(Settings settings) { configured.countDown(); }
        @Override public void update(List<GameMonitor.Event> events, boolean armed) { updated.countDown(); }
        @Override public void maintain() { }
        @Override public void stop() { }
        @Override public List<Device> devices() { return List.of(); }
        @Override public void close() { closed = true; }
    }
}
