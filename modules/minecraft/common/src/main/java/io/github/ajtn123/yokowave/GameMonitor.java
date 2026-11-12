package io.github.ajtn123.yokowave;

import java.util.ArrayList;
import java.util.List;

/** Client observations work in single-player and on servers without a companion mod. */
public final class GameMonitor {
    public record Snapshot(double health, double maximumHealth, double absorption, int hunger, int air, int armor, int experience, double speed, boolean grounded, double verticalSpeed) { }
    public record Event(String id, Double value) { }
    private Snapshot previous;

    public List<Event> sample(Snapshot current, boolean metrics) {
        return sample(current, metrics, false);
    }

    public List<Event> sample(Snapshot current, boolean metrics, boolean playerReplaced) {
        List<Event> events = new ArrayList<>();
        if (previous != null) {
            if (previous.health <= 0 && current.health > 0) events.add(new Event("respawn", null));
            if (!playerReplaced) {
                double damage = previous.health + previous.absorption - current.health - current.absorption;
                if (damage > 0) events.add(new Event("damage", Math.min(1024, damage)));
                if (previous.health > 0 && current.health <= 0) events.add(new Event("death", null));
                if (previous.grounded && !current.grounded && current.verticalSpeed > 0) events.add(new Event("jump", null));
            }
        }
        if (metrics) {
            events.add(new Event("health", Math.clamp(current.health / Math.max(1, current.maximumHealth), 0, 1)));
            events.add(new Event("hunger", (double) Math.clamp(current.hunger, 0, 20)));
            events.add(new Event("air", (double) Math.clamp(current.air, -20, 300)));
            events.add(new Event("armor", (double) Math.clamp(current.armor, 0, 100)));
            events.add(new Event("experience", (double) Math.clamp(current.experience, 0, 1_000_000)));
            events.add(new Event("speed", Math.clamp(current.speed, 0, 1000)));
        }
        previous = current;
        return List.copyOf(events);
    }

    public void reset() { previous = null; }
}
