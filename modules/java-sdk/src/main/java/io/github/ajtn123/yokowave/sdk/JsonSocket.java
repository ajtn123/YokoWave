package io.github.ajtn123.yokowave.sdk;

import com.google.gson.JsonElement;
import com.google.gson.JsonObject;
import com.google.gson.JsonParser;
import java.net.URI;
import java.net.http.HttpClient;
import java.net.http.WebSocket;
import java.nio.ByteBuffer;
import java.time.Duration;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.TimeUnit;
import java.util.function.Consumer;

/** Bounded, fragmented JSON text transport. Callbacks never wait for an RPC response. */
public final class JsonSocket implements WebSocket.Listener, AutoCloseable {
    private static final int MAXIMUM_BYTES = 1_048_576;
    private final ConcurrentHashMap<String, CompletableFuture<JsonElement>> pending = new ConcurrentHashMap<>();
    private final Consumer<JsonObject> notification;
    private final StringBuilder text = new StringBuilder();
    private final HttpClient client = HttpClient.newBuilder().connectTimeout(Duration.ofSeconds(5)).build();
    private volatile WebSocket socket;
    private final java.util.concurrent.atomic.AtomicReference<Throwable> failure = new java.util.concurrent.atomic.AtomicReference<>();
    private CompletableFuture<WebSocket> sending = CompletableFuture.completedFuture(null);

    public JsonSocket(Consumer<JsonObject> notification) { this.notification = notification; }

    public void connect(String address, boolean yokoWave) throws Exception {
        if (socket != null || failure.get() != null) throw new IllegalStateException("Connection already used");
        var builder = client.newWebSocketBuilder().connectTimeout(Duration.ofSeconds(5));
        if (yokoWave) builder.subprotocols("yokowave.v2");
        socket = builder.buildAsync(URI.create(address), this).get(6, TimeUnit.SECONDS);
        if (failure.get() != null) { socket.abort(); check(); }
        if (yokoWave && !socket.getSubprotocol().equals("yokowave.v2")) throw new IllegalStateException("Unexpected WebSocket subprotocol");
    }

    public synchronized CompletableFuture<JsonElement> request(String key, JsonObject message) {
        return request(key, message, Duration.ofSeconds(5));
    }

    public synchronized CompletableFuture<JsonElement> request(String key, JsonObject message, Duration timeout) {
        long milliseconds = timeout.toMillis();
        if (milliseconds < 1 || milliseconds > 7_260_000) throw new IllegalArgumentException("Invalid response timeout");
        var result = new CompletableFuture<JsonElement>();
        if (failure.get() != null || socket == null || pending.size() >= 64) return CompletableFuture.failedFuture(new IllegalStateException("Connection unavailable or request queue full"));
        if (pending.putIfAbsent(key, result) != null) return CompletableFuture.failedFuture(new IllegalStateException("Duplicate request ID"));
        try {
            send(message);
        } catch (RuntimeException error) {
            pending.remove(key, result);
            return CompletableFuture.failedFuture(error);
        }
        return result.orTimeout(milliseconds, TimeUnit.MILLISECONDS).whenComplete((ignored, error) -> {
            pending.remove(key, result);
            if (error != null && !(error instanceof RpcException)) fail(error);
        });
    }

    public synchronized void send(JsonObject message) {
        if (socket == null || failure.get() != null) throw new IllegalStateException("Connection unavailable");
        String payload = message.toString();
        if (payload.getBytes(java.nio.charset.StandardCharsets.UTF_8).length > MAXIMUM_BYTES) throw new IllegalArgumentException("Message too large");
        sending = sending.thenCompose(ignored -> socket.sendText(payload, true));
        sending.whenComplete((ignored, error) -> { if (error != null) fail(error); });
    }

    public void check() { if (failure.get() != null) throw new IllegalStateException("Connection failed", failure.get()); }

    @Override public void onOpen(WebSocket socket) {
        this.socket = socket;
        socket.request(1);
    }

    @Override public java.util.concurrent.CompletionStage<?> onText(WebSocket socket, CharSequence data, boolean last) {
        try {
            if (text.length() + data.length() > MAXIMUM_BYTES) throw new IllegalStateException("Message too large");
            text.append(data);
            if (last) {
                validateDepth(text);
                String payload = text.toString();
                if (payload.getBytes(java.nio.charset.StandardCharsets.UTF_8).length > MAXIMUM_BYTES) throw new IllegalStateException("Message too large");
                JsonObject message = JsonParser.parseString(payload).getAsJsonObject();
                text.setLength(0);
                if (message.has("id") && (message.has("result") || message.has("error"))) complete(Json.text(message, "id"), message);
                else if (message.has("type") && Json.text(message, "type").equals("message") && message.has("data") && message.get("data").isJsonObject()) {
                    JsonObject inner = message.getAsJsonObject("data");
                    if (inner.has("t") && Json.text(inner, "t").equals("resp")) complete(Json.text(message, "clientId") + ":" + Json.text(inner, "reqId"), inner);
                    else notification.accept(message);
                } else notification.accept(message);
            }
        } catch (RuntimeException error) { fail(error); }
        socket.request(1);
        return null;
    }

    private void complete(String key, JsonObject response) {
        var completion = pending.remove(key);
        if (completion == null) return;
        if (response.has("error")) {
            JsonElement error = response.get("error");
            String code = error.isJsonObject() && error.getAsJsonObject().has("code") ? error.getAsJsonObject().get("code").getAsString() : "remote_error";
            completion.completeExceptionally(new RpcException(code));
        }
        else completion.complete(response.get("result"));
    }

    @Override public java.util.concurrent.CompletionStage<?> onBinary(WebSocket socket, ByteBuffer data, boolean last) {
        fail(new IllegalStateException("Expected JSON text frame"));
        return null;
    }

    @Override public java.util.concurrent.CompletionStage<?> onClose(WebSocket socket, int status, String reason) {
        fail(new IllegalStateException("Connection closed"));
        return null;
    }

    @Override public void onError(WebSocket socket, Throwable error) { fail(error); }

    private void fail(Throwable error) {
        if (!failure.compareAndSet(null, error)) return;
        if (socket != null) socket.abort();
        pending.forEach((key, completion) -> completion.completeExceptionally(error));
        pending.clear();
    }

    @Override public void close() {
        fail(new IllegalStateException("Connection closed"));
        if (socket != null) socket.abort();
        client.shutdownNow();
    }

    private static void validateDepth(CharSequence json) {
        int depth = 0;
        boolean quoted = false;
        boolean escaped = false;
        for (int i = 0; i < json.length(); i++) {
            char value = json.charAt(i);
            if (quoted) {
                if (escaped) escaped = false;
                else if (value == '\\') escaped = true;
                else if (value == '"') quoted = false;
            } else if (value == '"') quoted = true;
            else if (value == '{' || value == '[') { if (++depth > 32) throw new IllegalStateException("Message too deeply nested"); }
            else if (value == '}' || value == ']') depth--;
        }
    }
}
