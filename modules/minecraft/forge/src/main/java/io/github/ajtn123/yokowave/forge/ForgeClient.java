package io.github.ajtn123.yokowave.forge;

import io.github.ajtn123.yokowave.client.ClientController;
import net.minecraftforge.api.distmarker.Dist;
import net.minecraftforge.client.event.RegisterKeyMappingsEvent;
import net.minecraftforge.common.MinecraftForge;
import net.minecraftforge.event.TickEvent;
import net.minecraftforge.eventbus.api.IEventBus;
import net.minecraftforge.fml.common.Mod;
import net.minecraftforge.fml.javafmlmod.FMLJavaModLoadingContext;
import net.minecraftforge.fml.loading.FMLEnvironment;
import net.minecraftforge.fml.loading.FMLPaths;

@Mod("yokowave")
public final class ForgeClient {
    public ForgeClient(FMLJavaModLoadingContext context) {
        if (FMLEnvironment.dist == Dist.CLIENT) initialize(context.getModEventBus());
    }

    private static void initialize(IEventBus bus) {
        var controller = new ClientController(FMLPaths.CONFIGDIR.get());
        bus.addListener((RegisterKeyMappingsEvent event) -> event.register(controller.key));
        MinecraftForge.EVENT_BUS.addListener((TickEvent.ClientTickEvent event) -> {
            if (event.phase == TickEvent.Phase.END) controller.tick();
        });
        Runtime.getRuntime().addShutdownHook(new Thread(controller.bridge::close));
    }
}
