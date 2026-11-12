package io.github.ajtn123.yokowave.sdk;

import com.google.gson.JsonArray;
import com.google.gson.JsonElement;
import com.google.gson.JsonObject;
import java.util.List;
import java.util.Locale;
import java.util.concurrent.Executors;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.TimeUnit;

/** One authenticated connection and lease. Calls are serialized, with automatic renewal. */
public final class YokoWaveClient implements AutoCloseable {
    public enum LeaseType { EVENT, CONTROL, GATT }
    public record EventDefinition(String id, String kind, String name, Double minimum, Double maximum, int validityMilliseconds) { }
    private final JsonSocket socket;
    private final ScheduledExecutorService renewal = Executors.newSingleThreadScheduledExecutor(Thread.ofVirtual().factory());
    private long requestId;
    private long sequence;
    private long clock;
    private long clockAt;
    private volatile String lease;
    private LeaseType type;
    private volatile Throwable failure;
    private boolean closing;
    private volatile boolean releasing;
    private java.util.concurrent.ScheduledFuture<?> renewalTask;
    private volatile java.util.function.Consumer<JsonObject> notifications = ignored -> { };

    public YokoWaveClient(String address, String token) throws Exception {
        validateAddress(address);
        if (token == null || token.length() < 24 || token.length() > 256) throw new IllegalArgumentException("Invalid token length");
        socket = new JsonSocket(message -> {
            if (!releasing && message.has("method") && Json.text(message, "method").equals("lease.revoked") && message.has("params") &&
                message.getAsJsonObject("params").has("lease") && Json.text(message.getAsJsonObject("params"), "lease").equals(lease)) {
                failure = new IllegalStateException("Lease revoked");
            }
            notifications.accept(message.deepCopy());
        });
        try {
            socket.connect(address, true);
            rpc("system.authenticate", Json.object("token", token));
            JsonObject hello = rpc("system.hello", new JsonObject()).getAsJsonObject();
            if (hello.get("version").getAsInt() != 2) throw new IllegalStateException("Unsupported protocol version");
            syncClock(hello);
        } catch (Exception error) {
            socket.close();
            renewal.shutdownNow();
            throw error;
        }
    }

    public synchronized JsonArray devices() throws Exception { return rpc("devices.list", new JsonObject()).getAsJsonArray(); }
    /** Called on the receive thread; handlers must not block or issue synchronous RPCs. */
    public void onNotification(java.util.function.Consumer<JsonObject> handler) { notifications = java.util.Objects.requireNonNull(handler); }

    public synchronized void acquire(LeaseType type, List<String> deviceIds) throws Exception {
        check();
        if (lease != null) throw new IllegalStateException("Lease already acquired");
        String namespace = type.name().toLowerCase(Locale.ROOT);
        JsonObject result = rpc("lease.acquire", Json.object("type", namespace, "deviceIds", deviceIds)).getAsJsonObject();
        if (!Json.text(result, "type").equals(namespace)) throw new IllegalStateException("Unexpected lease type");
        lease = Json.text(result, "id");
        this.type = type;
        sequence = 0;
        long duration = result.get("leaseMilliseconds").getAsLong();
        if (duration < 1000 || duration > 10000) throw new IllegalStateException("Invalid lease duration");
        renewalTask = renewal.scheduleWithFixedDelay(this::renew, duration / 3, duration / 3, TimeUnit.MILLISECONDS);
    }

    public void register(String clientId, List<EventDefinition> events) throws Exception {
        call("event.register", Json.object("registration", Json.object("clientId", clientId, "events", events, "simulated", false)));
    }

    public void emit(String eventId, Double value) throws Exception {
        JsonObject parameters = Json.object("eventId", eventId);
        if (value != null) parameters.addProperty("value", value);
        call("event.emit", parameters);
    }

    /** Supports the full leased namespace, including control.play, control.command and gatt.*. */
    public synchronized JsonElement call(String method, JsonObject parameters) throws Exception {
        check();
        if (lease == null || !method.startsWith(type.name().toLowerCase(Locale.ROOT) + ".")) throw new IllegalArgumentException("Wrong lease namespace");
        JsonObject request = parameters.deepCopy();
        request.addProperty("lease", lease);
        request.addProperty("sequence", ++sequence);
        request.addProperty("deadline", clock + TimeUnit.NANOSECONDS.toMillis(System.nanoTime() - clockAt) + 500);
        return rpc(method, request);
    }

    public synchronized void check() throws Exception {
        if (failure != null) throw new IllegalStateException("Connection failed", failure);
        socket.check();
    }

    private JsonElement rpc(String method, JsonObject parameters) throws Exception {
        String id = Long.toString(++requestId);
        return socket.request(id, Json.object("jsonrpc", "2.0", "id", id, "method", method, "params", parameters)).get(6, TimeUnit.SECONDS);
    }

    private synchronized void renew() {
        if (closing || lease == null) return;
        try { syncClock(rpc("lease.renew", Json.object("lease", lease)).getAsJsonObject()); }
        catch (Exception error) { failure = error; socket.close(); }
    }

    private void syncClock(JsonObject value) {
        clock = value.get("clockMilliseconds").getAsLong();
        clockAt = System.nanoTime();
    }

    public static void validateAddress(String address) {
        var uri = java.net.URI.create(address);
        if (!("ws".equals(uri.getScheme()) || "wss".equals(uri.getScheme())) || uri.getHost() == null ||
            uri.getUserInfo() != null || uri.getQuery() != null || uri.getFragment() != null) throw new IllegalArgumentException("Invalid WebSocket address");
    }

    public synchronized void release() throws Exception {
        if (lease == null) return;
        releasing = true;
        if (renewalTask != null) renewalTask.cancel(false);
        Exception failure = null;
        try {
            try {
                if (type == LeaseType.CONTROL) call("control.stop", new JsonObject());
            } catch (Exception error) {
                failure = error;
            }
            try {
                rpc("lease.release", Json.object("lease", lease));
            } catch (Exception error) {
                if (failure == null) failure = error;
                else failure.addSuppressed(error);
            }
        } finally {
            lease = null;
            releasing = false;
        }
        if (failure != null) throw failure;
    }

    @Override public synchronized void close() throws Exception {
        if (closing) return;
        closing = true;
        renewal.shutdownNow();
        try { release(); }
        finally { socket.close(); }
    }
}
