namespace YokoWave.Core;

public enum PumpDirection { Off, Forward, Reverse }
public sealed record PumpOutput(PumpDirection Direction = PumpDirection.Off, bool Water = false);
public sealed record DgBalanceSettings(byte FrequencyA = 128, byte FrequencyB = 128, byte WidthA = 128, byte WidthB = 128);
public sealed record DeviceTelemetry(double? PressureKpa = null, int? PressureA = null, int? PressureB = null,
    bool? Pressed = null, int? Acceleration = null, int? AngleX = null, int? AngleY = null, int? AngleZ = null,
    int? VoltageRaw = null, int? EventId = null, int? Parameter = null, int? IntensityA = null, int? IntensityB = null,
    ushort? Buttons = null, int? ModelId = null)
{
    public DeviceTelemetry Merge(DeviceTelemetry update) => new(update.PressureKpa ?? PressureKpa,
        update.PressureA ?? PressureA, update.PressureB ?? PressureB, update.Pressed ?? Pressed,
        update.Acceleration ?? Acceleration, update.AngleX ?? AngleX, update.AngleY ?? AngleY, update.AngleZ ?? AngleZ,
        update.VoltageRaw ?? VoltageRaw, update.EventId ?? EventId, update.Parameter ?? Parameter,
        update.IntensityA ?? IntensityA, update.IntensityB ?? IntensityB, update.Buttons ?? Buttons, update.ModelId ?? ModelId);
}
