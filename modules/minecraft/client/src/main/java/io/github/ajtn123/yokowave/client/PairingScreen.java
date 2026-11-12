package io.github.ajtn123.yokowave.client;

import com.google.zxing.BarcodeFormat;
import com.google.zxing.MultiFormatWriter;
import com.google.zxing.common.BitMatrix;
import net.minecraft.client.gui.GuiGraphics;
import net.minecraft.client.gui.components.Button;
import net.minecraft.client.gui.screens.Screen;
import net.minecraft.network.chat.Component;

public final class PairingScreen extends Screen {
    private final Screen parent;
    private final String url;
    private final BitMatrix qr;

    public PairingScreen(Screen parent, String url) {
        super(Component.translatable("minecraft.pair"));
        this.parent = parent;
        this.url = url;
        BitMatrix image = null;
        try { image = new MultiFormatWriter().encode(url, BarcodeFormat.QR_CODE, 0, 0); }
        catch (Exception ignored) { }
        qr = image;
    }

    @Override protected void init() {
        addRenderableWidget(Button.builder(Component.translatable("minecraft.copy"), ignored -> minecraft.keyboardHandler.setClipboard(url)).bounds(width / 2 - 105, height - 28, 130, 20).build());
        addRenderableWidget(Button.builder(Component.translatable("minecraft.done"), ignored -> onClose()).bounds(width / 2 + 30, height - 28, 75, 20).build());
    }

    @Override public void render(GuiGraphics graphics, int mouseX, int mouseY, float delta) {
        graphics.fill(0, 0, width, height, ScreenPalette.BACKGROUND);
        graphics.drawCenteredString(font, qr == null ? Component.translatable("minecraft.qrError") : title, width / 2, 12, ScreenPalette.FOREGROUND);
        if (qr != null) {
            int scale = Math.max(1, Math.min(width - 20, height - 65) / qr.getWidth());
            int left = (width - qr.getWidth() * scale) / 2;
            int top = 28;
            graphics.fill(left, top, left + qr.getWidth() * scale, top + qr.getHeight() * scale, ScreenPalette.QR_BACKGROUND);
            for (int row = 0; row < qr.getHeight(); row++) for (int col = 0; col < qr.getWidth(); col++) {
                if (qr.get(col, row)) graphics.fill(left + col * scale, top + row * scale, left + (col + 1) * scale, top + (row + 1) * scale, ScreenPalette.QR_FOREGROUND);
            }
        }
        super.render(graphics, mouseX, mouseY, delta);
    }

    @Override public boolean isPauseScreen() { return false; }
    @Override public void onClose() { minecraft.setScreen(parent); }
}
