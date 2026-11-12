package io.github.ajtn123.yokowave;

import java.util.List;

/** The runtime owns one connection. Implementations perform I/O only on its worker. */
public interface OutputConnection extends AutoCloseable {
    record Device(String id, String name, int channelMask) { }
    void connect() throws Exception;
    void configure(Settings settings) throws Exception;
    void update(List<GameMonitor.Event> events, boolean armed) throws Exception;
    void maintain() throws Exception;
    void stop() throws Exception;
    List<Device> devices();
    default String pairingUrl() { return ""; }
    @Override void close() throws Exception;
}
