using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using YokoWave.Core;
using YokoWave.Core.Bluetooth;

namespace YokoWave.Sdk.DgLab.V4;

public sealed record DgLabV4Slot(string Id, string AppId, DgLabV4Device Device, DateTimeOffset CapturedAt)
{
    public bool Online => Boolean(Device.SlotState?["hasDevice"]) ?? Text(Device.Props?["connectState"]) == "connected";
    public double Maximum => DgLabV4Models.Find(Device.Type)?.Maximum ?? 0;
    public DeviceDescriptor Descriptor
    {
        get
        {
            var model = DgLabV4Models.Find(Device.Type);
            return new(Id, Device.Name, model?.Kind ?? DeviceKind.Linear, Integer(Device.Props?["power"], 0, 100), null,
                model?.Protocol.ToString(), model?.Channels ?? 0, DeviceOrigin.Remote, false,
                model is null ? DeviceCapability.None : DeviceCapability.StatusQuery);
        }
    }

    public double Limit(int channel)
    {
        if (channel is < 0 or > 1)
            throw new DgLabV4Exception("invalid_params");
        var state = Device.SlotState?[channel == 0 ? "channelA" : "channelB"] as JsonObject;
        return Boolean(state?["isMuted"]) == true ? 0 : Math.Clamp(Number(state?["intensityMax"], Maximum), 0, Maximum);
    }

    public DeviceStatusUpdate Status(DateTimeOffset now)
    {
        var props = Device.Props;
        var descriptor = Descriptor;
        var telemetry = new DeviceTelemetry(
            PressureKpa: descriptor.IsInput ? Numeric(props?["pressure"]) : null,
            IntensityA: Integer(props?["intensityA"], 0, (int)Maximum), IntensityB: Integer(props?["intensityB"], 0, (int)Maximum),
            ModeA: Integer(props?["mode"], 0, 3), PressureCapturedAt: descriptor.IsInput ? CapturedAt : null);
        return new(Id, descriptor.Battery, Contact(props?["channelAStatus"]), Contact(props?["channelBStatus"]),
            hardwareVersion: (byte?)Integer(props?["version"], 0, 255), telemetry: telemetry,
            telemetryAge: now >= CapturedAt ? now - CapturedAt : TimeSpan.FromDays(1));
    }

    public static double? Numeric(JsonNode? value)
    {
        if (value is not JsonValue item)
            return null;
        if (!item.TryGetValue<double>(out double number) && (item.GetValueKind() != JsonValueKind.Number ||
            !double.TryParse(item.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number)))
            return null;
        return double.IsFinite(number) ? number : null;
    }
    public static double Number(JsonNode? value, double fallback = 0) => Numeric(value) ?? fallback;
    public static bool? Boolean(JsonNode? value) => value is JsonValue item && item.TryGetValue<bool>(out bool flag) ? flag : null;
    public static string? Text(JsonNode? value) => value is JsonValue item && item.TryGetValue<string>(out string? text) ? text : null;
    public static int? Integer(JsonNode? value, int minimum, int maximum) => Numeric(value) is { } number && number >= minimum && number <= maximum && number == Math.Truncate(number) ? (int)number : null;

    private ElectrodeContact? Contact(JsonNode? value) => Device.Type == "OVC_1" ? Boolean(value) switch
    {
        true => ElectrodeContact.Connected,
        false => ElectrodeContact.NotConnected,
        _ => null
    } : Integer(value, 0, 4) switch
    {
        1 => ElectrodeContact.NotConnected,
        2 => ElectrodeContact.Delivering,
        0 or 3 or 4 => ElectrodeContact.Unknown,
        _ => null
    };
}
