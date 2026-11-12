package io.github.ajtn123.yokowave.sdk;

import com.google.gson.JsonElement;
import com.google.gson.JsonObject;
import java.net.URLEncoder;
import java.nio.charset.StandardCharsets;
import java.time.Duration;
import java.util.List;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicLong;

/** V4 controller connected to an external relay; Apps connect using pairingUrl(). */
public final class DgLabClient implements AutoCloseable {
    public record Device(String appId, String slotId, String name, String type, JsonObject properties, JsonObject state) {
        public double maximum() { return type.equals("COYOTE_020") ? 2047 : List.of("COYOTE_030", "OVC_1").contains(type) ? 200 : 0; }
        public boolean online() { return state.has("hasDevice") ? state.get("hasDevice").getAsBoolean() : properties.has("connectState") && Json.text(properties, "connectState").equals("connected"); }
        public double limit(int channel) {
            var value = state.get(channel == 0 ? "channelA" : "channelB");
            if (value == null || !value.isJsonObject()) return maximum();
            JsonObject options = value.getAsJsonObject();
            if (options.has("isMuted") && options.get("isMuted").getAsBoolean()) return 0;
            return options.has("intensityMax") ? Math.clamp(options.get("intensityMax").getAsDouble(), 0, maximum()) : maximum();
        }
    }
    private final JsonSocket socket = new JsonSocket(this::receive);
    private final DgLabCatalog catalog = new DgLabCatalog();
    private final CompletableFuture<String> hello = new CompletableFuture<>();
    private final AtomicLong nextId = new AtomicLong();
    private final String address;
    private String clientId;
    private volatile java.util.function.Consumer<JsonObject> events = ignored -> { };

    public DgLabClient(String address) throws Exception {
        YokoWaveClient.validateAddress(address);
        this.address = address;
        try {
            socket.connect(address, false);
            clientId = hello.get(5, TimeUnit.SECONDS);
        } catch (Exception error) { socket.close(); throw error; }
    }

    public String pairingUrl() {
        String paired = address + "?tid=" + URLEncoder.encode(clientId, StandardCharsets.UTF_8);
        return "https://dungeon-lab.cn/s/?v=1&action=socket&url=" + URLEncoder.encode(paired, StandardCharsets.UTF_8);
    }

    public CompletableFuture<JsonElement> request(String appId, String method, JsonElement data) {
        return request(appId, method, data, Duration.ofSeconds(5));
    }

    public CompletableFuture<JsonElement> request(String appId, String method, JsonElement data, Duration timeout) {
        String id = Long.toString(nextId.incrementAndGet());
        return socket.request(appId + ":" + id, Json.object("type", "message", "clientId", appId,
            "data", Json.object("t", "req", "reqId", id, "m", method, "data", data)), timeout);
    }

    public CompletableFuture<JsonElement> operate(Device device, int type, int channel, JsonElement value, int duration, boolean immediate) {
        return request(device.appId, "device.op", Json.object("s", device.slotId, "t", type, "c", channel, "v", value, "p", 1, "d", duration, "im", immediate, "ver", 3), Duration.ofMillis(Math.max(5000L, (long) duration + 1000)));
    }

    public void maintain() throws Exception {
        socket.check();
        for (String app : catalog.refreshes()) {
            long revision = catalog.revision(app);
            var reply = request(app, "devices.get", null).get(6, TimeUnit.SECONDS).getAsJsonObject();
            catalog.describe(app, reply, revision);
        }
    }

    public List<Device> devices() { return catalog.snapshot(); }
    /** Called on the receive thread after catalog updates; handlers must not block. */
    public void onEvent(java.util.function.Consumer<JsonObject> handler) { events = java.util.Objects.requireNonNull(handler); }

    private void receive(JsonObject frame) {
        String type = Json.text(frame, "type");
        switch (type) {
                case "hello" -> hello.complete(Json.text(frame, "clientId"));
                case "heartbeat" -> socket.send(Json.object("type", "heartbeat"));
                case "client_attached" -> {
                    String app = Json.text(frame, "clientId");
                    catalog.attach(app);
                }
                case "client_disconnected" -> catalog.detach(Json.text(frame, "clientId"));
                case "error" -> throw new IllegalStateException("DG-LAB relay error");
                case "message" -> catalog.event(Json.text(frame, "clientId"), frame.getAsJsonObject("data"));
                default -> { }
        }
        events.accept(frame.deepCopy());
    }

    @Override public void close() { socket.close(); catalog.clear(); }
}
