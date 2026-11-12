package io.github.ajtn123.yokowave.sdk;

import com.google.gson.Gson;
import com.google.gson.JsonElement;
import com.google.gson.JsonObject;

public final class Json {
    private static final Gson GSON = new Gson();
    private Json() { }
    public static JsonElement value(Object value) { return GSON.toJsonTree(value); }
    public static JsonObject object(Object... pairs) {
        JsonObject result = new JsonObject();
        for (int i = 0; i < pairs.length; i += 2) result.add((String) pairs[i], value(pairs[i + 1]));
        return result;
    }
    public static String text(JsonObject value, String key) { return value.get(key).getAsString(); }
}
