package io.github.ajtn123.yokowave.client;

import io.github.ajtn123.yokowave.GameBridge;
import io.github.ajtn123.yokowave.GameMonitor;
import io.github.ajtn123.yokowave.Settings;
import io.github.ajtn123.yokowave.SettingsStore;
import java.nio.file.Path;
import net.minecraft.client.KeyMapping;
import net.minecraft.client.Minecraft;
import org.lwjgl.glfw.GLFW;

public final class ClientController {
    public final KeyMapping key = new KeyMapping("minecraft.open", GLFW.GLFW_KEY_F8, "minecraft.title");
    public final GameBridge bridge = new GameBridge();
    private final SettingsStore store;
    private final GameMonitor monitor = new GameMonitor();
    private Settings settings = Settings.defaults();
    private String token = "";
    private int ticks;
    private boolean configValid = true;
    private Object observedLevel;
    private Object observedPlayer;
    private Object observedConnection;

    public ClientController(Path configDirectory) {
        store = new SettingsStore(configDirectory.resolve("yokowave.json"));
        try { settings = store.load(); }
        catch (Exception error) { configValid = false; }
    }

    public Settings settings() { return settings; }
    public String token() { return token; }
    public boolean configValid() { return configValid; }
    public void token(String value) { token = value; if (bridge.connected() || bridge.busy()) bridge.disconnect(); }

    public void save(Settings value) throws Exception {
        // Refuse to overwrite a damaged file. The user can move it aside before restarting.
        if (!configValid) throw new IllegalStateException("minecraft.configError");
        store.save(value);
        if (!value.equals(settings) && (bridge.connected() || bridge.busy())) {
            if (value.mode() != settings.mode() || !value.address().equals(settings.address()) || !value.clientId().equals(settings.clientId())) bridge.disconnect();
            else bridge.configure(value);
        }
        settings = value;
    }

    public void tick() {
        Minecraft minecraft = Minecraft.getInstance();
        while (key.consumeClick()) {
            if (minecraft.screen instanceof BridgeScreen screen) screen.onClose();
            else minecraft.setScreen(new BridgeScreen(minecraft.screen, this));
        }
        var player = minecraft.player;
        boolean playerReplaced = player != observedPlayer || minecraft.level != observedLevel;
        if (minecraft.getConnection() != observedConnection) {
            monitor.reset();
            bridge.arm(false);
        } else if (playerReplaced && settings.mode() != Settings.Mode.EVENT) {
            bridge.arm(false);
        }
        observedConnection = minecraft.getConnection();
        observedPlayer = player;
        observedLevel = minecraft.level;
        boolean paused = minecraft.isPaused() || minecraft.screen instanceof net.minecraft.client.gui.screens.PauseScreen;
        boolean deathScreen = minecraft.screen instanceof net.minecraft.client.gui.screens.DeathScreen;
        if (player == null || minecraft.level == null || paused && !deathScreen) {
            monitor.reset();
            bridge.arm(false);
            return;
        }
        var movement = player.getDeltaMovement();
        var snapshot = new GameMonitor.Snapshot(player.getHealth(), player.getMaxHealth(), player.getAbsorptionAmount(), player.getFoodData().getFoodLevel(),
            player.getAirSupply(), player.getArmorValue(), player.experienceLevel, movement.horizontalDistance() * 20, player.onGround(), movement.y);
        var events = monitor.sample(snapshot, ++ticks % 5 == 0, playerReplaced);
        if (!events.isEmpty()) bridge.offer(events);
    }
}
