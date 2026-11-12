package io.github.ajtn123.yokowave.sdk;

import com.google.gson.JsonObject;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.LinkedHashMap;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Map;
import java.util.Set;

/** Atomic App catalogs and revisions share a single synchronization boundary. */
final class DgLabCatalog {
    private final Map<String, Map<String, JsonObject>> apps = new LinkedHashMap<>();
    private final Map<String, Long> revisions = new HashMap<>();
    private final Set<String> refresh = new LinkedHashSet<>();

    synchronized void attach(String app) {
        if (apps.size() >= 128 && !apps.containsKey(app)) throw new IllegalStateException("Too many Apps");
        apps.putIfAbsent(app, new LinkedHashMap<>());
        refresh.add(app);
    }

    synchronized void detach(String app) {
        apps.remove(app);
        revisions.remove(app);
        refresh.remove(app);
    }

    synchronized List<String> refreshes() { var result = List.copyOf(refresh); refresh.clear(); return result; }
    synchronized long revision(String app) { return revisions.getOrDefault(app, 0L); }

    synchronized void describe(String app, JsonObject data, long revision) {
        if (apps.containsKey(app) && revision == revision(app)) apps.put(app, replacements(data));
        else if (apps.containsKey(app) && apps.get(app).isEmpty()) refresh.add(app);
    }

    synchronized void event(String app, JsonObject data) {
        if (!apps.containsKey(app) || !data.has("ev")) return;
        Map<String, JsonObject> next = new LinkedHashMap<>();
        for (var entry : apps.get(app).entrySet()) next.put(entry.getKey(), entry.getValue().deepCopy());
        switch (Json.text(data, "ev")) {
            case "devices.snapshot" -> next = replacements(data);
            case "devices.patch" -> {
                if (data.has("removed")) for (var id : data.getAsJsonArray("removed")) next.remove(id.getAsString());
                if (data.has("added")) for (var item : data.getAsJsonArray("added")) {
                    JsonObject device = item.getAsJsonObject().deepCopy();
                    validate(device);
                    next.put(Json.text(device, "slotId"), device);
                }
            }
            case "slots.patch" -> {
                if (data.has("slots")) for (var item : data.getAsJsonArray("slots")) {
                    JsonObject patch = item.getAsJsonObject();
                    JsonObject device = next.get(Json.text(patch, "slotId"));
                    if (device != null) merge(device, patch);
                }
            }
            default -> { return; }
        }
        if (next.size() > 128) throw new IllegalStateException("Too many devices");
        next.values().forEach(DgLabCatalog::validate);
        apps.put(app, next);
        revisions.put(app, revision(app) + 1);
    }

    synchronized List<DgLabClient.Device> snapshot() {
        List<DgLabClient.Device> result = new ArrayList<>();
        apps.forEach((app, devices) -> devices.forEach((id, item) -> result.add(new DgLabClient.Device(app, id, Json.text(item, "name"), Json.text(item, "type"),
            item.has("props") ? item.getAsJsonObject("props").deepCopy() : new JsonObject(), item.has("slotState") ? item.getAsJsonObject("slotState").deepCopy() : new JsonObject()))));
        return List.copyOf(result);
    }

    synchronized void clear() { apps.clear(); revisions.clear(); refresh.clear(); }

    private static Map<String, JsonObject> replacements(JsonObject data) {
        Map<String, JsonObject> devices = new LinkedHashMap<>();
        if (data.getAsJsonArray("devices").size() > 128) throw new IllegalStateException("Too many devices");
        for (var item : data.getAsJsonArray("devices")) {
            JsonObject device = item.getAsJsonObject().deepCopy();
            validate(device);
            if (devices.put(Json.text(device, "slotId"), device) != null) throw new IllegalStateException("Duplicate slot");
        }
        return devices;
    }

    private static void validate(JsonObject device) {
        if (Json.text(device, "slotId").isBlank() || Json.text(device, "slotId").length() > 256 || Json.text(device, "name").length() > 256 || Json.text(device, "type").length() > 128) throw new IllegalStateException("Invalid device");
        if (device.has("props") && !device.get("props").isJsonObject() || device.has("slotState") && !device.get("slotState").isJsonObject()) throw new IllegalStateException("Invalid state");
    }

    private static void merge(JsonObject target, JsonObject patch) {
        patch.entrySet().forEach(entry -> {
            if (entry.getValue().isJsonObject() && target.has(entry.getKey()) && target.get(entry.getKey()).isJsonObject()) merge(target.getAsJsonObject(entry.getKey()), entry.getValue().getAsJsonObject());
            else target.add(entry.getKey(), entry.getValue().deepCopy());
        });
    }
}
