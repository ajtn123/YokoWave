package io.github.ajtn123.yokowave;

import com.google.gson.Gson;
import java.io.IOException;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.StandardCopyOption;

public final class SettingsStore {
    private final Path path;
    private final Gson json = new Gson();

    public SettingsStore(Path path) { this.path = path; }

    public Settings load() throws IOException {
        if (!Files.exists(path)) return Settings.defaults();
        try {
            Settings settings = json.fromJson(Files.readString(path), Settings.class);
            if (settings == null) throw new IllegalArgumentException("minecraft.invalid");
            return settings;
        } catch (RuntimeException error) {
            throw new IOException("minecraft.configError", error);
        }
    }

    public void save(Settings settings) throws IOException {
        Files.createDirectories(path.getParent());
        Path temporary = Files.createTempFile(path.getParent(), "yokowave-", ".tmp");
        try {
            Files.writeString(temporary, json.toJson(settings));
            Files.move(temporary, path, StandardCopyOption.REPLACE_EXISTING, StandardCopyOption.ATOMIC_MOVE);
        } finally {
            Files.deleteIfExists(temporary);
        }
    }
}
