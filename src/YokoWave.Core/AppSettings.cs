namespace YokoWave.Core;

public sealed record AppSettings(double DefaultLimit = 60, string? Language = null, bool UseBluetooth = false, int TimerLimitMinutes = 120,
    List<RemoteEndpointSettings>? RemoteEndpoints = null, bool ShowContacts = true, bool AutoConnect = false, List<string>? AutoConnectDevices = null,
    List<DeviceOutputSettings>? DeviceOutputs = null);
