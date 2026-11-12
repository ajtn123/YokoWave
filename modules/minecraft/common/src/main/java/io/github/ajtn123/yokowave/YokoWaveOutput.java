package io.github.ajtn123.yokowave;

import com.google.gson.JsonArray;
import com.google.gson.JsonObject;
import io.github.ajtn123.yokowave.sdk.Json;
import io.github.ajtn123.yokowave.sdk.YokoWaveClient;
import java.util.ArrayList;
import java.util.List;
import java.util.UUID;

public final class YokoWaveOutput implements OutputConnection {
    private Settings settings;
    private final String token;
    private YokoWaveClient client;
    private List<Device> devices = List.of();
    private boolean leased;

    public YokoWaveOutput(Settings settings, String token) { this.settings = settings; this.token = token; }
    @Override public void configure(Settings settings) throws Exception { stop(); this.settings = settings; }

    @Override public void connect() throws Exception {
        client = new YokoWaveClient(settings.address(), token);
        if (settings.mode() == Settings.Mode.EVENT) {
            client.acquire(YokoWaveClient.LeaseType.EVENT, List.of());
            client.register(settings.clientId(), definitions());
            leased = true;
        } else {
            List<Device> discovered = new ArrayList<>();
            for (var item : client.devices()) {
                JsonObject device = item.getAsJsonObject();
                String kind = Json.text(device, "kind");
                if (List.of("dualChannel", "vibration").contains(kind)) discovered.add(new Device(Json.text(device, "id"), Json.text(device, "name"), device.get("channelMask").getAsInt()));
            }
            devices = List.copyOf(discovered);
        }
    }

    @Override public void update(List<GameMonitor.Event> events, boolean armed) throws Exception {
        if (!armed) return;
        if (settings.mode() == Settings.Mode.EVENT) {
            for (var event : events) client.emit(event.id(), event.value());
        } else {
            if (!leased) {
                Device device = devices.stream().filter(item -> item.id().equals(settings.deviceId())).findFirst().orElseThrow(() -> new IllegalStateException("minecraft.selectDevice"));
                if ((device.channelMask() & (1 << settings.channel())) == 0) throw new IllegalStateException("minecraft.invalid");
                client.acquire(YokoWaveClient.LeaseType.CONTROL, List.of(device.id()));
                leased = true;
            }
            double damage = events.stream().filter(event -> event.id().equals("damage")).mapToDouble(GameMonitor.Event::value).sum();
            if (damage > 0 && settings.strength() > 0) client.call("control.play", Json.object("playback", playback(settings, damage)));
        }
    }

    public static JsonObject playback(Settings settings, double damage) {
        double strength = Math.min(settings.strength(), Math.max(0, damage * settings.damageScale()));
        double[] channels = new double[3];
        channels[settings.channel()] = strength;
        return Json.object("program", Json.object("kind", "waveform", "order", "once", "outputLimit", settings.strength(), "offsetChannelB", false),
            "waveform", Json.object("formatVersion", 1, "id", UUID.randomUUID(), "name", "Minecraft", "shape", "sampled", "periodSeconds", settings.duration() / 1000d,
                "frequency", settings.frequency(), "samples", List.of(Json.object("level", 1, "frequencyHz", settings.frequency(), "pulseWidth", 1), Json.object("level", 1, "frequencyHz", settings.frequency(), "pulseWidth", 1))),
            "channelA", channels[0], "channelB", channels[1], "channelC", channels[2], "durationSeconds", Math.max(1, settings.duration() / 1000d));
    }

    private static List<YokoWaveClient.EventDefinition> definitions() {
        List<YokoWaveClient.EventDefinition> events = new ArrayList<>();
        for (String id : List.of("death", "respawn", "jump")) events.add(new YokoWaveClient.EventDefinition(id, "trigger", id, null, null, 1000));
        String[] ids = { "damage", "health", "hunger", "air", "armor", "experience", "speed" };
        double[] minimums = { 0, 0, 0, -20, 0, 0, 0 };
        double[] maximums = { 1024, 1, 20, 300, 100, 1_000_000, 1000 };
        for (int i = 0; i < ids.length; i++) events.add(new YokoWaveClient.EventDefinition(ids[i], "number", ids[i], minimums[i], maximums[i], 1000));
        return List.copyOf(events);
    }

    @Override public void maintain() throws Exception { client.check(); }
    @Override public List<Device> devices() { return devices; }
    @Override public void stop() throws Exception {
        if (client != null && leased && settings.mode() == Settings.Mode.CONTROL) {
            client.release();
            leased = false;
        }
    }
    @Override public void close() throws Exception { if (client != null) client.close(); }
}
