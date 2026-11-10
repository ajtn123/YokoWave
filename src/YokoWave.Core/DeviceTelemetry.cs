using YokoWave.Core.Bluetooth;

namespace YokoWave.Core;

public enum PumpDirection { Off, Forward, Reverse }
public sealed record PumpOutput(PumpDirection Direction = PumpDirection.Off, bool Water = false,
    int PeristalticSeconds = 0, int WaterSeconds = 0)
{
    public void Validate()
    {
        ProtocolGuard.Require(Enum.IsDefined(Direction));
        ProtocolGuard.Word(PeristalticSeconds);
        ProtocolGuard.Word(WaterSeconds);
    }
}
public sealed record DgBalanceSettings(byte FrequencyA = 128, byte FrequencyB = 128, byte WidthA = 128, byte WidthB = 128);
public sealed record DeviceTelemetry(double? PressureKpa = null, int? PressureA = null, int? PressureB = null,
    bool? Pressed = null, int? Acceleration = null, int? AngleX = null, int? AngleY = null, int? AngleZ = null,
    int? VoltageRaw = null, int? EventId = null, int? Parameter = null, int? IntensityA = null, int? IntensityB = null,
    ushort? Buttons = null, int? ModelId = null,
    MotorDeviceInfo? MotorInfo = null, SensorAxes? AccelerationAxes = null, SensorAxes? GyroscopeAxes = null,
    AuxiliaryMotorMode? AuxiliaryMotor = null, int? ModeA = null, int? ModeB = null,
    int? IndicatorColor = null, int? Sequence = null, bool? EventActive = null,
    PawCalibration? Calibration = null, IReadOnlyList<PawEventState>? PawEvents = null,
    int? MaximumWriteLength = null, PumpDirection? Peristaltic = null, bool? WaterPump = null,
    CoyotePulse? PulseA = null, CoyotePulse? PulseB = null, byte? ProtocolErrorCode = null,
    DateTimeOffset? PressureCapturedAt = null, DateTimeOffset? EventCapturedAt = null)
{
    public double? VoltageVolts => VoltageRaw is >= 0 and <= 255 ? PawProtocol.Voltage(VoltageRaw.Value) : null;
    public DeviceTelemetry Merge(DeviceTelemetry update) => this with
    {
        PressureKpa = update.PressureKpa ?? PressureKpa,
        PressureA = update.PressureA ?? PressureA,
        PressureB = update.PressureB ?? PressureB,
        Pressed = update.Pressed ?? Pressed,
        Acceleration = update.Acceleration ?? Acceleration,
        AngleX = update.AngleX ?? AngleX,
        AngleY = update.AngleY ?? AngleY,
        AngleZ = update.AngleZ ?? AngleZ,
        VoltageRaw = update.VoltageRaw ?? VoltageRaw,
        EventId = update.EventId ?? EventId,
        Parameter = update.Parameter ?? Parameter,
        IntensityA = update.IntensityA ?? IntensityA,
        IntensityB = update.IntensityB ?? IntensityB,
        Buttons = update.Buttons ?? Buttons,
        ModelId = update.ModelId ?? ModelId,
        MotorInfo = update.MotorInfo ?? MotorInfo,
        AccelerationAxes = update.AccelerationAxes ?? AccelerationAxes,
        GyroscopeAxes = update.GyroscopeAxes ?? GyroscopeAxes,
        AuxiliaryMotor = update.AuxiliaryMotor ?? AuxiliaryMotor,
        ModeA = update.ModeA ?? ModeA,
        ModeB = update.ModeB ?? ModeB,
        IndicatorColor = update.IndicatorColor ?? IndicatorColor,
        Sequence = update.Sequence ?? Sequence,
        EventActive = update.EventActive ?? EventActive,
        Calibration = update.Calibration ?? Calibration,
        PawEvents = update.PawEvents ?? PawEvents,
        MaximumWriteLength = update.MaximumWriteLength ?? MaximumWriteLength,
        Peristaltic = update.Peristaltic ?? Peristaltic,
        WaterPump = update.WaterPump ?? WaterPump,
        PulseA = update.PulseA ?? PulseA,
        PulseB = update.PulseB ?? PulseB,
        ProtocolErrorCode = update.ProtocolErrorCode ?? ProtocolErrorCode,
        PressureCapturedAt = update.PressureCapturedAt ?? PressureCapturedAt,
        EventCapturedAt = update.EventCapturedAt ?? EventCapturedAt
    };
}
