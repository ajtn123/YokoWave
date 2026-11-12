package io.github.ajtn123.yokowave;

import java.net.URI;
import java.util.UUID;

public record Settings(Mode mode, String address, String clientId, String deviceId, int channel, double strength, double frequency, int duration, double damageScale) {
    public enum Mode { EVENT, CONTROL, DGLAB }

    public Settings {
        if (mode == null || address == null || clientId == null || deviceId == null) throw new IllegalArgumentException("minecraft.invalid");
        URI uri = URI.create(address);
        if (!("ws".equals(uri.getScheme()) || "wss".equals(uri.getScheme())) || uri.getHost() == null ||
            uri.getUserInfo() != null || uri.getQuery() != null || uri.getFragment() != null ||
            !clientId.matches("[A-Za-z0-9_.-]{1,80}") || channel < 0 || channel > 2 ||
            !Double.isFinite(strength) || strength < 0 || strength > 100 || !Double.isFinite(frequency) || frequency < 10 || frequency > 100 ||
            duration < 100 || duration > 2000 || !Double.isFinite(damageScale) || damageScale < 0 || damageScale > 10) {
            throw new IllegalArgumentException("minecraft.invalid");
        }
    }

    public static Settings defaults() {
        return new Settings(Mode.EVENT, "ws://127.0.0.1:9000/ws/", "minecraft." + UUID.randomUUID(), "", 0, 0, 20, 500, 1);
    }

}
