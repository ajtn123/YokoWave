package io.github.ajtn123.yokowave.sdk;

import com.google.gson.JsonObject;
import com.google.gson.JsonParser;
import java.net.InetSocketAddress;
import java.util.List;
import java.util.concurrent.CopyOnWriteArrayList;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import org.java_websocket.WebSocket;
import org.java_websocket.handshake.ClientHandshake;
import org.java_websocket.server.WebSocketServer;
import org.junit.jupiter.api.Test;
import static org.junit.jupiter.api.Assertions.*;

class ProtocolTest {
    private static final String TOKEN = "test-token-at-least-24-characters";

    @Test void eventsAuthenticateRegisterRenewAndReleaseWithoutClaimingDevices() throws Exception {
        try (Fixture server = new Fixture(false); var client = new YokoWaveClient(server.url(), TOKEN)) {
            client.acquire(YokoWaveClient.LeaseType.EVENT, List.of());
            client.register("minecraft.test", List.of(new YokoWaveClient.EventDefinition("damage", "number", "Damage", 0d, 1024d, 1000)));
            client.emit("damage", 4d);
            assertThrows(IllegalArgumentException.class, () -> client.call("control.stop", new JsonObject()));
            assertTrue(server.renewed.await(3, TimeUnit.SECONDS));
            client.emit("damage", 2d);
            var calls = server.messages;
            assertEquals("system.authenticate", Json.text(calls.get(0), "method"));
            assertEquals(0, calls.get(2).getAsJsonObject("params").getAsJsonArray("deviceIds").size());
            long sequence = 0;
            for (var call : calls) {
                var parameters = call.getAsJsonObject("params");
                if (!parameters.has("sequence")) continue;
                assertEquals(++sequence, parameters.get("sequence").getAsLong());
                assertTrue(parameters.get("deadline").getAsLong() > 10000);
            }
        }
    }

    @Test void controlStopsBeforeReleasingAndPropagatesRemoteFailures() throws Exception {
        try (Fixture server = new Fixture(false)) {
            try (var client = new YokoWaveClient(server.url(), TOKEN)) {
                assertEquals(1, client.devices().size());
                client.acquire(YokoWaveClient.LeaseType.CONTROL, List.of("device"));
                client.call("control.output", Json.object("frame", Json.object("channelA", 0, "channelB", 0, "frequency", 20, "outputLimit", 0)));
                var rejected = assertThrows(java.util.concurrent.ExecutionException.class, () -> client.call("control.reject", new JsonObject()));
                assertInstanceOf(RpcException.class, rejected.getCause());
                assertThrows(java.util.concurrent.ExecutionException.class, () -> client.call("control.output", Json.object("invalid", "界".repeat(400_000))));
                client.release();
                client.acquire(YokoWaveClient.LeaseType.CONTROL, List.of("device"));
                client.call("control.stop", new JsonObject());
            }
            var methods = server.messages.stream().map(message -> Json.text(message, "method")).toList();
            assertTrue(methods.indexOf("control.stop") < methods.indexOf("lease.release"));
        }
    }

    @Test void dgV4DiscoversPatchesAndAddressesRequestsToTheCorrectApp() throws Exception {
        try (Fixture server = new Fixture(true); var client = new DgLabClient(server.url())) {
            long deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(2);
            while (client.devices().isEmpty() && System.nanoTime() < deadline) { client.maintain(); Thread.sleep(10); }
            assertTrue(client.pairingUrl().contains("tid%3Dcontroller"));
            assertTrue(server.heartbeat.await(2, TimeUnit.SECONDS));
            assertEquals("slot", client.devices().getFirst().slotId());
            var device = client.devices().getFirst();
            assertEquals(200, device.maximum());
            assertTrue(device.online());
            assertEquals(30, device.limit(0));
            var result = client.operate(device, 4, 0, Json.value(0), 100, true).get(2, TimeUnit.SECONDS);
            assertTrue(result.getAsJsonObject().get("ok").getAsBoolean());
            var operation = server.messages.stream().filter(item -> item.has("data") && Json.text(item.getAsJsonObject("data"), "m").equals("device.op")).findFirst().orElseThrow();
            assertEquals("app", Json.text(operation, "clientId"));
            assertEquals(4, operation.getAsJsonObject("data").getAsJsonObject("data").get("t").getAsInt());
        }
    }

    @Test void rejectsInvalidAddressesAndCredentialsBeforeConnecting() {
        for (String address : List.of("http://localhost", "ws://user:password@localhost", "ws://localhost/?tid=x", "ws://localhost/#x")) {
            assertThrows(IllegalArgumentException.class, () -> YokoWaveClient.validateAddress(address));
        }
        assertThrows(IllegalArgumentException.class, () -> new YokoWaveClient("ws://localhost", "short"));
    }

    private static final class Fixture extends WebSocketServer implements AutoCloseable {
        final List<JsonObject> messages = new CopyOnWriteArrayList<>();
        final CountDownLatch renewed = new CountDownLatch(1);
        final CountDownLatch heartbeat = new CountDownLatch(1);
        private final CountDownLatch started = new CountDownLatch(1);
        private final boolean dg;
        private final long start = System.nanoTime();

        Fixture(boolean dg) throws Exception {
            super(new InetSocketAddress("127.0.0.1", 0), 1, List.of(new org.java_websocket.drafts.Draft_6455(List.of(), List.of(new org.java_websocket.protocols.Protocol("yokowave.v2"), new org.java_websocket.protocols.Protocol("")))));
            this.dg = dg;
            setConnectionLostTimeout(0);
            start();
            assertTrue(started.await(5, TimeUnit.SECONDS));
        }

        String url() { return "ws://127.0.0.1:" + getPort(); }
        @Override public void onStart() { started.countDown(); }
        @Override public void onOpen(WebSocket socket, ClientHandshake handshake) {
            if (dg) {
                socket.send(Json.object("type", "hello", "clientId", "controller").toString());
                socket.send(Json.object("type", "heartbeat").toString());
                socket.send(Json.object("type", "client_attached", "clientId", "app").toString());
            }
        }

        @Override public void onMessage(WebSocket socket, String text) {
            var request = JsonParser.parseString(text).getAsJsonObject();
            if (request.has("type") && Json.text(request, "type").equals("heartbeat")) {
                heartbeat.countDown();
                return;
            }
            messages.add(request);
            if (dg) {
                var data = request.getAsJsonObject("data");
                Object result = Json.text(data, "m").equals("devices.get") ? Json.object("devices", List.of(Json.object("slotId", "slot", "name", "Coyote", "type", "COYOTE_030",
                    "props", Json.object("connectState", "connected"), "slotState", Json.object("hasDevice", true, "channelA", Json.object("intensityMax", 30))))) : Json.object("ok", true);
                socket.send(Json.object("type", "message", "clientId", "app", "data", Json.object("t", "resp", "reqId", Json.text(data, "reqId"), "result", result)).toString());
            } else {
                String method = Json.text(request, "method");
                if (method.equals("control.reject")) {
                    socket.send(Json.object("jsonrpc", "2.0", "id", Json.text(request, "id"), "error", Json.object("code", -32602, "message", "Error.RemoteRequest")).toString());
                    return;
                }
                Object result = switch (method) {
                    case "system.hello", "lease.renew" -> Json.object("version", 2, "clockMilliseconds", 10000 + TimeUnit.NANOSECONDS.toMillis(System.nanoTime() - start));
                    case "devices.list" -> List.of(Json.object("id", "device", "name", "Test", "kind", "dualChannel", "channelMask", 3));
                    case "lease.acquire" -> Json.object("id", "lease", "type", Json.text(request.getAsJsonObject("params"), "type"), "deviceIds", request.getAsJsonObject("params").get("deviceIds"), "leaseMilliseconds", 1500);
                    default -> true;
                };
                socket.send(Json.object("jsonrpc", "2.0", "id", Json.text(request, "id"), "result", result).toString());
                if (method.equals("lease.renew")) renewed.countDown();
            }
        }

        @Override public void onClose(WebSocket socket, int code, String reason, boolean remote) { }
        @Override public void onError(WebSocket socket, Exception error) { }
        @Override public void close() throws Exception { stop(1000); }
    }
}
