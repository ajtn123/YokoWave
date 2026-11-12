package io.github.ajtn123.yokowave;

import java.util.List;
import java.util.concurrent.ArrayBlockingQueue;
import java.util.concurrent.Executors;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.TimeUnit;

/** One worker owns all I/O; game ticks cannot queue unbounded or replay stale input. */
public final class GameBridge implements AutoCloseable {
    private record Batch(List<GameMonitor.Event> events, long capturedAt, long generation) { }
    private final ScheduledExecutorService worker = Executors.newSingleThreadScheduledExecutor(Thread.ofVirtual().factory());
    private final ArrayBlockingQueue<Batch> queue = new ArrayBlockingQueue<>(16);
    private volatile boolean armed;
    private volatile boolean connected;
    private final java.util.concurrent.atomic.AtomicInteger operations = new java.util.concurrent.atomic.AtomicInteger();
    private final java.util.concurrent.atomic.AtomicBoolean disconnectQueued = new java.util.concurrent.atomic.AtomicBoolean();
    private volatile String status = "minecraft.disconnected";
    private volatile String pairingUrl = "";
    private volatile List<OutputConnection.Device> devices = List.of();
    private final java.util.concurrent.atomic.AtomicLong generation = new java.util.concurrent.atomic.AtomicLong();
    private final java.util.concurrent.atomic.AtomicLong connectionGeneration = new java.util.concurrent.atomic.AtomicLong();
    private final java.util.concurrent.atomic.AtomicReference<Settings> pendingSettings = new java.util.concurrent.atomic.AtomicReference<>();
    private OutputConnection connection;
    private boolean wasArmed;
    private final java.util.concurrent.atomic.AtomicBoolean closed = new java.util.concurrent.atomic.AtomicBoolean();
    private final java.util.function.BiFunction<Settings, String, OutputConnection> factory;

    public GameBridge() { this((settings, token) -> settings.mode() == Settings.Mode.DGLAB ? new DgLabOutput(settings) : new YokoWaveOutput(settings, token)); }
    GameBridge(java.util.function.BiFunction<Settings, String, OutputConnection> factory) {
        this.factory = factory;
        worker.scheduleWithFixedDelay(this::pump, 0, 50, TimeUnit.MILLISECONDS);
    }
    public boolean connected() { return connected; }
    public boolean armed() { return armed; }
    public boolean busy() { return operations.get() > 0; }
    public String status() { return status; }
    public String pairingUrl() { return pairingUrl; }
    public List<OutputConnection.Device> devices() { return devices; }

    public void connect(Settings settings, String token) {
        if (closed.get() || connected || !operations.compareAndSet(0, 1)) return;
        long request = connectionGeneration.incrementAndGet();
        worker.execute(() -> {
            try {
                connection = factory.apply(settings, token);
                connection.connect();
                if (request != connectionGeneration.get() || closed.get()) { cleanup(); return; }
                pairingUrl = connection.pairingUrl();
                connected = true;
                status = "minecraft.connected";
            } catch (Exception error) { fail(error); }
            finally { operations.decrementAndGet(); }
        });
    }

    public void arm(boolean enabled) {
        armed = enabled && connected;
        generation.incrementAndGet();
        queue.clear();
    }

    public void configure(Settings settings) {
        arm(false);
        pendingSettings.set(settings);
    }

    public void offer(List<GameMonitor.Event> events) {
        if (armed && connected && !queue.offer(new Batch(events, System.nanoTime(), generation.get()))) {
            armed = false;
            status = "minecraft.overloaded";
        }
    }

    public void disconnect() {
        arm(false);
        connectionGeneration.incrementAndGet();
        if (closed.get() || !disconnectQueued.compareAndSet(false, true)) return;
        operations.incrementAndGet();
        worker.execute(() -> {
            try { cleanup(); status = "minecraft.disconnected"; }
            catch (Exception error) { status = "minecraft.connectionError"; log(error); }
            finally { disconnectQueued.set(false); operations.decrementAndGet(); }
        });
    }

    private void pump() {
        if (!connected || connection == null) return;
        try {
            Settings updated = pendingSettings.getAndSet(null);
            if (updated != null) connection.configure(updated);
            connection.maintain();
            devices = connection.devices();
            if (!armed) {
                queue.clear();
                if (wasArmed) connection.stop();
                wasArmed = false;
                return;
            }
            wasArmed = true;
            Batch batch;
            while (armed && (batch = queue.poll()) != null) {
                if (batch.generation == generation.get() && System.nanoTime() - batch.capturedAt < TimeUnit.MILLISECONDS.toNanos(500)) connection.update(batch.events, true);
            }
        } catch (Exception error) { fail(error); }
    }

    private void fail(Exception error) {
        status = "minecraft.connectionError";
        try { cleanup(); } catch (Exception cleanupError) { error.addSuppressed(cleanupError); }
        log(error);
    }

    private void cleanup() throws Exception {
        connected = false;
        arm(false);
        wasArmed = false;
        devices = List.of();
        pairingUrl = "";
        pendingSettings.set(null);
        OutputConnection old = connection;
        connection = null;
        if (old != null) old.close();
    }

    private static void log(Exception error) {
        System.getLogger(GameBridge.class.getName()).log(System.Logger.Level.WARNING, "Minecraft connection failed", error);
    }

    @Override public void close() {
        if (!closed.compareAndSet(false, true)) return;
        arm(false);
        try { worker.submit(() -> { try { cleanup(); } catch (Exception error) { log(error); } }).get(20, TimeUnit.SECONDS); }
        catch (Exception error) { log(error); }
        finally { worker.shutdownNow(); }
    }
}
