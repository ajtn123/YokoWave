using YokoWave.Core;
using YokoWave.Core.Bluetooth;

namespace YokoWave.Remote.DgLab.V4;

internal static class DgLabV4Models
{
    private static readonly DgLabV4DeviceModel[] _models =
    [
        new("COYOTE_020", BluetoothProtocol.CoyoteV2, DeviceKind.DualChannel, 3, 2047),
        new("COYOTE_030", BluetoothProtocol.CoyoteV3, DeviceKind.DualChannel, 3, 200),
        new("OVC_1", BluetoothProtocol.Opossum, DeviceKind.Vibration, 3, 200),
        new("BMTR_1", BluetoothProtocol.Civet, DeviceKind.Sensor, 0, 0)
    ];
    public static DgLabV4DeviceModel? Find(string type) => _models.FirstOrDefault(model => model.Type == type);
    public static bool Supports(DeviceDescriptor device) => _models.Any(model => model.Protocol == device.Profile?.Protocol);
    public static string Type(DeviceDescriptor device) => Require(device).Type;
    public static double Maximum(DeviceDescriptor device) => Require(device).Maximum;

    public static DgLabV4Device Snapshot(string slotId, DeviceConnection connection)
    {
        var device = connection.Device;
        double maximum = Maximum(device);
        var props = new System.Text.Json.Nodes.JsonObject
        {
            ["power"] = device.Battery,
            ["connectState"] = "connected",
            ["version"] = connection.HardwareVersion
        };
        if (!device.IsInput)
        {
            props["intensityA"] = connection.Telemetry?.IntensityA;
            props["intensityB"] = connection.Telemetry?.IntensityB;
            if (device.Profile?.Protocol == BluetoothProtocol.Opossum && connection.Telemetry?.ModeA is { } mode)
                props["mode"] = mode;
        }
        if (connection.Telemetry?.PressureKpa is { } pressure)
        {
            props["pressure"] = pressure;
            props["updateTime"] = (connection.Telemetry.PressureCapturedAt ?? connection.TelemetryCapturedAt)?.ToString("O");
        }
        var state = new System.Text.Json.Nodes.JsonObject
        {
            ["hasDevice"] = true,
            ["markLight"] = "yellow",
        };
        if (!device.IsInput)
        {
            state["channelA"] = new System.Text.Json.Nodes.JsonObject { ["isMuted"] = false, ["intensityMax"] = maximum };
            state["channelB"] = new System.Text.Json.Nodes.JsonObject { ["isMuted"] = false, ["intensityMax"] = maximum };
        }
        return new(slotId, device.Name, Type(device), props, state);
    }

    private static DgLabV4DeviceModel Require(DeviceDescriptor device) => _models.FirstOrDefault(model => model.Protocol == device.Profile?.Protocol)
        ?? throw new DgLabV4Exception("invalid_operate");
}

internal sealed record DgLabV4DeviceModel(string Type, BluetoothProtocol Protocol, DeviceKind Kind, int Channels, double Maximum);
