package io.github.ajtn123.yokowave.client;

import io.github.ajtn123.yokowave.Settings;
import java.util.ArrayList;
import java.util.List;
import java.util.function.Consumer;
import net.minecraft.client.gui.GuiGraphics;
import net.minecraft.client.gui.components.Button;
import net.minecraft.client.gui.components.EditBox;
import net.minecraft.client.gui.screens.Screen;
import net.minecraft.network.chat.Component;

/** Shared native Minecraft screen, compiled with each target's official mappings. */
public final class BridgeScreen extends Screen {
    private record Label(String key, int y) { }
    private static final int ROW = 26;
    private static final int FIELD = 200;
    private static final int LABEL = 110;
    private final Screen parent;
    private final ClientController controller;
    private final List<Label> labels = new ArrayList<>();
    private boolean outputPage;
    private String error = "";
    private final java.util.Set<String> invalidFields = new java.util.HashSet<>();
    private Button connect;
    private Button arm;

    public BridgeScreen(Screen parent, ClientController controller) {
        super(Component.translatable("minecraft.title"));
        this.parent = parent;
        this.controller = controller;
    }

    @Override protected void init() {
        labels.clear();
        invalidFields.clear();
        error = "";
        connect = null;
        arm = null;
        int left = width / 2 - (FIELD + LABEL) / 2;
        int top = 32;
        Settings s = controller.settings();
        if (!outputPage) {
            button(left + LABEL, top, FIELD, Component.translatable("minecraft.mode." + s.mode().name()), () -> {
                Settings current = controller.settings();
                Settings.Mode mode = Settings.Mode.values()[(current.mode().ordinal() + 1) % Settings.Mode.values().length];
                save(new Settings(mode, mode == Settings.Mode.DGLAB ? "ws://127.0.0.1:9998/v4" : "ws://127.0.0.1:9000/ws/", current.clientId(), "", current.channel(), current.strength(), current.frequency(), current.duration(), current.damageScale()));
                rebuildWidgets();
            });
            labels.add(new Label("minecraft.mode", top));
            field("minecraft.address", s.address(), top += ROW, value -> change("address", value));
            if (s.mode() != Settings.Mode.DGLAB) {
                field("minecraft.token", controller.token(), top += ROW, controller::token);
                if (s.mode() == Settings.Mode.EVENT) field("minecraft.clientId", s.clientId(), top += ROW, value -> change("clientId", value));
            }
            if (s.mode() != Settings.Mode.EVENT) {
                var selected = controller.bridge.devices().stream().filter(device -> device.id().equals(s.deviceId())).findFirst();
                button(left + LABEL, top += ROW, FIELD, selected.map(device -> Component.literal(device.name())).orElse(Component.translatable("minecraft.selectDevice")), () -> {
                    var devices = controller.bridge.devices();
                    if (devices.isEmpty()) return;
                    int current = -1;
                    for (int i = 0; i < devices.size(); i++) if (devices.get(i).id().equals(s.deviceId())) current = i;
                    change("deviceId", devices.get((current + 1) % devices.size()).id());
                    rebuildWidgets();
                });
                labels.add(new Label("minecraft.device", top));
            }
            connect = button(left, top += ROW, (LABEL + FIELD) / 2 - 3, Component.empty(), () -> {
                if (controller.bridge.connected()) controller.bridge.disconnect();
                else if (error.isEmpty() && controller.configValid()) controller.bridge.connect(controller.settings(), controller.token());
            });
            arm = button(left + (LABEL + FIELD) / 2 + 3, top, (LABEL + FIELD) / 2 - 3, Component.empty(), () -> controller.bridge.arm(!controller.bridge.armed()));
        } else {
            field("minecraft.strength", Double.toString(s.strength()), top, value -> change("strength", value));
            field("minecraft.frequency", Double.toString(s.frequency()), top += ROW, value -> change("frequency", value));
            field("minecraft.duration", Integer.toString(s.duration()), top += ROW, value -> change("duration", value));
            field("minecraft.damageScale", Double.toString(s.damageScale()), top += ROW, value -> change("damageScale", value));
            labels.add(new Label("minecraft.channel", top += ROW));
            button(left + LABEL, top, FIELD, Component.literal(Character.toString('A' + s.channel())), () -> {
                var device = controller.bridge.devices().stream().filter(item -> item.id().equals(s.deviceId())).findFirst();
                int mask = s.mode() == Settings.Mode.DGLAB ? 3 : device.map(item -> item.channelMask()).orElse(7);
                int next = s.channel();
                do { next = (next + 1) % 3; } while ((mask & (1 << next)) == 0 && next != s.channel());
                change("channel", Integer.toString(next));
                rebuildWidgets();
            });
        }
        button(left, height - 28, 100, Component.translatable(outputPage ? "minecraft.connection" : "minecraft.output"), () -> { outputPage = !outputPage; rebuildWidgets(); });
        button(left + 105, height - 28, 100, Component.translatable("minecraft.pair"), () -> {
            String url = controller.bridge.pairingUrl();
            if (!url.isEmpty()) minecraft.setScreen(new PairingScreen(this, url));
        });
        button(left + 210, height - 28, 100, Component.translatable("minecraft.done"), this::onClose);
    }

    private void field(String key, String value, int y, Consumer<String> changed) {
        int x = width / 2 - (FIELD + LABEL) / 2 + LABEL;
        var box = new EditBox(font, x, y, FIELD, 20, Component.translatable(key));
        box.setMaxLength(key.equals("minecraft.address") ? 2048 : 256);
        box.setValue(value);
        if (key.equals("minecraft.token")) box.setFormatter((content, start) -> net.minecraft.util.FormattedCharSequence.forward("*".repeat(content.length()), net.minecraft.network.chat.Style.EMPTY));
        box.setResponder(changed);
        addRenderableWidget(box);
        labels.add(new Label(key, y));
    }

    private Button button(int x, int y, int width, Component label, Runnable action) {
        return addRenderableWidget(Button.builder(label, ignored -> action.run()).bounds(x, y, width, 20).build());
    }

    private void change(String key, String value) {
        Settings s = controller.settings();
        invalidFields.remove(key);
        try {
            save(new Settings(s.mode(), key.equals("address") ? value : s.address(), key.equals("clientId") ? value : s.clientId(), key.equals("deviceId") ? value : s.deviceId(),
                key.equals("channel") ? Integer.parseInt(value) : s.channel(), key.equals("strength") ? Double.parseDouble(value) : s.strength(),
                key.equals("frequency") ? Double.parseDouble(value) : s.frequency(), key.equals("duration") ? Integer.parseInt(value) : s.duration(),
                key.equals("damageScale") ? Double.parseDouble(value) : s.damageScale()));
        } catch (RuntimeException failure) {
            invalidFields.add(key);
            error = "minecraft.invalid";
            controller.bridge.arm(false);
        }
        if (!invalidFields.isEmpty()) error = "minecraft.invalid";
    }

    private void save(Settings settings) {
        try { controller.save(settings); error = ""; }
        catch (Exception failure) {
            error = "minecraft.configError";
            controller.bridge.arm(false);
        }
    }

    @Override public void tick() {
        if (connect != null) {
            connect.setMessage(Component.translatable(controller.bridge.connected() ? "minecraft.disconnect" : "minecraft.connect"));
            connect.active = !controller.bridge.busy() && (controller.bridge.connected() || controller.configValid() && error.isEmpty());
            arm.setMessage(Component.translatable(controller.bridge.armed() ? "minecraft.stop" : "minecraft.arm"));
            boolean selected = controller.settings().mode() == Settings.Mode.EVENT || controller.bridge.devices().stream().anyMatch(device -> device.id().equals(controller.settings().deviceId()));
            arm.active = controller.bridge.connected() && (controller.bridge.armed() || selected && error.isEmpty() && !controller.bridge.busy());
        }
    }

    @Override public void render(GuiGraphics graphics, int mouseX, int mouseY, float delta) {
        graphics.fill(0, 0, width, height, ScreenPalette.BACKGROUND);
        graphics.drawCenteredString(font, title, width / 2, 12, ScreenPalette.FOREGROUND);
        int left = width / 2 - (FIELD + LABEL) / 2;
        for (var label : labels) graphics.drawString(font, Component.translatable(label.key), left, label.y + 6, ScreenPalette.FOREGROUND);
        graphics.drawCenteredString(font, Component.translatable(!controller.configValid() ? "minecraft.configError" : error.isEmpty() ? controller.bridge.status() : error), width / 2, height - 43, ScreenPalette.FOREGROUND);
        super.render(graphics, mouseX, mouseY, delta);
    }

    @Override public boolean isPauseScreen() { return false; }
    @Override public void onClose() { minecraft.setScreen(parent); }
}
