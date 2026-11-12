package io.github.ajtn123.yokowave.neoforge;

import io.github.ajtn123.yokowave.client.ClientController;
import net.neoforged.api.distmarker.Dist;
import net.neoforged.bus.api.IEventBus;
import net.neoforged.fml.common.Mod;
import net.neoforged.fml.loading.FMLPaths;
import net.neoforged.neoforge.client.event.ClientTickEvent;
import net.neoforged.neoforge.client.event.RegisterKeyMappingsEvent;
import net.neoforged.neoforge.common.NeoForge;

@Mod(value = "yokowave", dist = Dist.CLIENT)
public final class NeoForgeClient {
    public NeoForgeClient(IEventBus bus) {
        var controller = new ClientController(FMLPaths.CONFIGDIR.get());
        bus.addListener((RegisterKeyMappingsEvent event) -> event.register(controller.key));
        NeoForge.EVENT_BUS.addListener((ClientTickEvent.Post event) -> controller.tick());
        Runtime.getRuntime().addShutdownHook(new Thread(controller.bridge::close));
    }
}
