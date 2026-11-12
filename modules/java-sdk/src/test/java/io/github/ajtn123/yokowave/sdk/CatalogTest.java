package io.github.ajtn123.yokowave.sdk;

import java.util.List;
import org.junit.jupiter.api.Test;
import static org.junit.jupiter.api.Assertions.*;

class CatalogTest {
    @Test void staleQueryCannotOverwriteNewerPatchesAndDisconnectRemovesOnlyItsApp() {
        var catalog = new DgLabCatalog();
        catalog.attach("one");
        catalog.attach("two");
        var snapshot = Json.object("devices", List.of(device("slot")));
        catalog.describe("one", snapshot, catalog.revision("one"));
        catalog.describe("two", snapshot, catalog.revision("two"));
        long revision = catalog.revision("one");
        catalog.event("one", Json.object("ev", "slots.patch", "slots", List.of(Json.object("slotId", "slot", "slotState", Json.object("channelA", Json.object("intensityMax", 12))))));
        catalog.describe("one", snapshot, revision);
        assertEquals(12, catalog.snapshot().stream().filter(device -> device.appId().equals("one")).findFirst().orElseThrow().limit(0));
        catalog.detach("two");
        assertEquals(1, catalog.snapshot().size());
    }

    @Test void invalidSnapshotIsAtomicAndMutePreventsOutput() {
        var catalog = new DgLabCatalog();
        catalog.attach("one");
        catalog.describe("one", Json.object("devices", List.of(device("slot"))), 0);
        assertThrows(IllegalStateException.class, () -> catalog.event("one", Json.object("ev", "devices.snapshot", "devices", List.of(device("duplicate"), device("duplicate")))));
        assertEquals("slot", catalog.snapshot().getFirst().slotId());
        catalog.event("one", Json.object("ev", "slots.patch", "slots", List.of(Json.object("slotId", "slot", "slotState", Json.object("channelA", Json.object("isMuted", true))))));
        assertEquals(0, catalog.snapshot().getFirst().limit(0));
        assertEquals(200, catalog.snapshot().getFirst().limit(1));
    }

    private static com.google.gson.JsonObject device(String id) {
        return Json.object("slotId", id, "name", "Coyote", "type", "COYOTE_030", "props", Json.object("connectState", "connected"));
    }
}
