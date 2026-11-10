using YokoWave.Core;
using YokoWave.Core.Bluetooth;

namespace YokoWave.Remote.DgLab.V4;

internal static class DgLabV4Models
{
    public static bool Supports(DeviceDescriptor device) => device.Profile?.Protocol is BluetoothProtocol.CoyoteV2 or BluetoothProtocol.CoyoteV3 or BluetoothProtocol.Opossum or BluetoothProtocol.Civet;
    public static string Type(DeviceDescriptor device) => device.Profile?.Protocol switch
    {
        BluetoothProtocol.CoyoteV2 => "COYOTE_020",
        BluetoothProtocol.CoyoteV3 => "COYOTE_030",
        BluetoothProtocol.Opossum => "OVC_1",
        BluetoothProtocol.Civet => "BMTR_1",
        _ => throw new DgLabV4Exception("invalid_operate")
    };
    public static double Maximum(DeviceDescriptor device) => device.Profile?.Protocol == BluetoothProtocol.CoyoteV2 ? 2047 : 200;

    public static DgLabV4Device Snapshot(string slotId, DeviceConnection connection)
    {
        var device = connection.Device;
        double maximum = Maximum(device);
        var props = new System.Text.Json.Nodes.JsonObject
        {
            ["power"] = device.Battery,
            ["connectState"] = "connected",
            ["version"] = connection.HardwareVersion,
            ["intensityA"] = connection.Telemetry?.IntensityA ?? (int)(connection.Output.ChannelA * maximum / 100),
            ["intensityB"] = connection.Telemetry?.IntensityB ?? (int)(connection.Output.ChannelB * maximum / 100)
        };
        if (connection.Telemetry?.PressureKpa is { } pressure)
        {
            props["pressure"] = pressure;
            props["updateTime"] = (connection.Telemetry.PressureCapturedAt ?? connection.TelemetryCapturedAt)?.ToString("O");
        }
        var state = new System.Text.Json.Nodes.JsonObject
        {
            ["hasDevice"] = true,
            ["markLight"] = "yellow",
            ["channelA"] = new System.Text.Json.Nodes.JsonObject { ["isMuted"] = false, ["intensityMax"] = maximum },
            ["channelB"] = new System.Text.Json.Nodes.JsonObject { ["isMuted"] = false, ["intensityMax"] = maximum }
        };
        return new(slotId, device.Name, Type(device), props, state);
    }
}
