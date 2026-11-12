package io.github.ajtn123.yokowave;

import java.nio.file.Files;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.io.TempDir;
import java.nio.file.Path;
import static org.junit.jupiter.api.Assertions.*;

class GameTest {
    @Test void observesDamageDeathRespawnJumpAndResetsAcrossWorlds() {
        var monitor = new GameMonitor();
        assertTrue(monitor.sample(snapshot(20, 4, true), false).isEmpty());
        var damage = monitor.sample(snapshot(18, 0, false), true);
        assertEquals(6, damage.stream().filter(event -> event.id().equals("damage")).findFirst().orElseThrow().value());
        assertTrue(damage.stream().anyMatch(event -> event.id().equals("jump")));
        assertTrue(monitor.sample(snapshot(0, 0, true), false).stream().anyMatch(event -> event.id().equals("death")));
        assertTrue(monitor.sample(snapshot(20, 0, true), false).stream().anyMatch(event -> event.id().equals("respawn")));
        monitor.reset();
        assertTrue(monitor.sample(snapshot(1, 0, true), false).isEmpty());
    }

    @Test void pulseNeverExceedsCapAndLeavesOtherChannelsZero() {
        Settings settings = new Settings(Settings.Mode.CONTROL, "ws://localhost", "test", "device", 1, 25, 20, 500, 3);
        var playback = YokoWaveOutput.playback(settings, 100);
        assertEquals(0, playback.get("channelA").getAsDouble());
        assertEquals(25, playback.get("channelB").getAsDouble());
        assertEquals(0, playback.get("channelC").getAsDouble());
        assertEquals("once", playback.getAsJsonObject("program").get("order").getAsString());
        assertEquals(.5, playback.getAsJsonObject("waveform").get("periodSeconds").getAsDouble());
    }

    @Test void playerReplacementPreservesRespawnWithoutInventingDamageOrJumpEvents() {
        var monitor = new GameMonitor();
        monitor.sample(snapshot(20, 0, true), false);
        assertTrue(monitor.sample(snapshot(1, 0, false), false, true).isEmpty());
        monitor.sample(snapshot(0, 0, true), false);
        var respawn = monitor.sample(snapshot(20, 0, false), false, true);
        assertEquals(1, respawn.size());
        assertEquals("respawn", respawn.getFirst().id());
        monitor.reset();
        assertTrue(monitor.sample(snapshot(20, 0, true), false, true).isEmpty());
    }

    @Test void settingsRoundTripAndDamagedFilesArePreserved(@TempDir Path directory) throws Exception {
        var file = directory.resolve("settings.json");
        var store = new SettingsStore(file);
        var settings = Settings.defaults();
        store.save(settings);
        assertEquals(settings, store.load());
        Files.writeString(file, "broken");
        assertThrows(java.io.IOException.class, store::load);
        assertEquals("broken", Files.readString(file));
        assertThrows(IllegalArgumentException.class, () -> new Settings(Settings.Mode.CONTROL, "ws://localhost", "test", "", 0, Double.NaN, 20, 500, 1));
    }

    private static GameMonitor.Snapshot snapshot(double health, double absorption, boolean grounded) {
        return new GameMonitor.Snapshot(health, 20, absorption, 20, 300, 0, 0, 0, grounded, grounded ? 0 : .4);
    }
}
