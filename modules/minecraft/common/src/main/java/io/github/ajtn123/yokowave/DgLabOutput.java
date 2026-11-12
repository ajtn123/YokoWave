package io.github.ajtn123.yokowave;

import io.github.ajtn123.yokowave.sdk.DgLabClient;
import io.github.ajtn123.yokowave.sdk.Json;
import java.util.ArrayList;
import java.util.Collections;
import java.util.List;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.TimeUnit;

public final class DgLabOutput implements OutputConnection {
    private Settings settings;
    private DgLabClient client;
    private DgLabClient.Device active;
    private final List<CompletableFuture<?>> tasks = new ArrayList<>();

    public DgLabOutput(Settings settings) { this.settings = settings; }
    @Override public void configure(Settings settings) throws Exception {
        stop();
        active = null;
        this.settings = settings;
    }
    @Override public void connect() throws Exception { client = new DgLabClient(settings.address()); }
    @Override public String pairingUrl() { return client.pairingUrl(); }
    @Override public void maintain() throws Exception {
        client.maintain();
        var iterator = tasks.iterator();
        while (iterator.hasNext()) {
            var task = iterator.next();
            if (task.isDone()) {
                task.get();
                iterator.remove();
            }
        }
    }
    @Override public List<Device> devices() {
        return client.devices().stream().filter(device -> device.maximum() > 0 && device.online())
            .map(device -> new Device(device.appId() + "/" + device.slotId(), device.name(), 3)).toList();
    }

    @Override public void update(List<GameMonitor.Event> events, boolean armed) throws Exception {
        if (!armed) return;
        DgLabClient.Device selected = client.devices().stream().filter(device -> (device.appId() + "/" + device.slotId()).equals(settings.deviceId()) && device.online() && device.maximum() > 0)
            .findFirst().orElseThrow(() -> new IllegalStateException("minecraft.selectDevice"));
        if (settings.channel() > 1) throw new IllegalStateException("minecraft.invalid");
        if (active == null) {
            active = selected;
            stop();
        }
        active = selected;
        double damage = events.stream().filter(event -> event.id().equals("damage")).mapToDouble(GameMonitor.Event::value).sum();
        if (damage <= 0 || settings.strength() == 0) return;
        double percent = Math.min(settings.strength(), damage * settings.damageScale());
        double intensity = Math.min(Math.floor(percent * selected.maximum() / 100), selected.limit(settings.channel()));
        String period = "%02X".formatted(Math.clamp(Math.round(1000 / settings.frequency()), 10, 100));
        var pulse = Collections.nCopies((settings.duration() + 99) / 100, period.repeat(4) + "64646464");
        // Both tasks finish on the App even if the game freezes or loses its connection.
        var strength = client.operate(selected, 4, settings.channel(), Json.value(intensity), settings.duration(), true);
        var waveform = client.operate(selected, 0, settings.channel(), Json.value(pulse), settings.duration(), true);
        tasks.add(CompletableFuture.allOf(strength, waveform));
        if (tasks.size() > 32) throw new IllegalStateException("minecraft.overloaded");
    }

    @Override public void stop() throws Exception {
        if (active == null) return;
        Exception failure = null;
        try {
            client.request(active.appId(), "device.op.clear", Json.object("s", active.slotId())).get(5, TimeUnit.SECONDS);
        } catch (Exception error) {
            failure = error;
        }
        for (int channel = 0; channel < 2; channel++) {
            try {
                client.operate(active, 7, channel, Json.value(0), 0, true).get(5, TimeUnit.SECONDS);
            } catch (Exception error) {
                if (failure == null) failure = error;
                else failure.addSuppressed(error);
            }
        }
        if (failure != null) throw failure;
    }

    @Override public void close() throws Exception {
        try {
            stop();
        } finally {
            if (client != null) client.close();
        }
    }
}
