package io.github.ajtn123.yokowave.fabric;

import io.github.ajtn123.yokowave.client.ClientController;
import net.fabricmc.api.ClientModInitializer;
import net.fabricmc.fabric.api.client.event.lifecycle.v1.ClientLifecycleEvents;
import net.fabricmc.fabric.api.client.event.lifecycle.v1.ClientTickEvents;
import net.fabricmc.fabric.api.client.keybinding.v1.KeyBindingHelper;
import net.fabricmc.loader.api.FabricLoader;

public final class FabricClient implements ClientModInitializer {
    @Override public void onInitializeClient() {
        var controller = new ClientController(FabricLoader.getInstance().getConfigDir());
        KeyBindingHelper.registerKeyBinding(controller.key);
        ClientTickEvents.END_CLIENT_TICK.register(client -> controller.tick());
        ClientLifecycleEvents.CLIENT_STOPPING.register(client -> controller.bridge.close());
    }
}
