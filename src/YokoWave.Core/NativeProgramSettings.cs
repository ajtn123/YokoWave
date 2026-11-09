using YokoWave.Core.Bluetooth;

namespace YokoWave.Core;

public enum NativeProgramKind { ElectricalPreset, ElectricalBuffered, MotorPreset, CoyoteV2Pulse }
public sealed record FrequencyPulse(int FrequencyHz = 20, int PulseWidth = 50)
{
    public void Validate()
    {
        ProtocolGuard.Byte(FrequencyHz, 1, 100);
        ProtocolGuard.Byte(PulseWidth, 0, 100);
    }
}
public sealed record CoyotePulse(int X = 1, int Y = 9, int Z = 20)
{
    public void Validate()
    {
        ProtocolGuard.Byte(X, 0, 31);
        ProtocolGuard.Word(Y, 0, 1023);
        ProtocolGuard.Byte(Z, 0, 31);
    }
}
public sealed record NativeProgramSettings(NativeProgramKind Kind = NativeProgramKind.ElectricalPreset,
    int PresetA = 1, int PresetB = 1, int PresetC = 1, IReadOnlyList<FrequencyPulse>? BufferA = null, IReadOnlyList<FrequencyPulse>? BufferB = null,
    bool Repeat = true, CoyotePulse? PulseA = null, CoyotePulse? PulseB = null)
{
    public DeviceCapability RequiredCapability => Kind switch
    {
        NativeProgramKind.ElectricalPreset => DeviceCapability.ElectricalPresets,
        NativeProgramKind.ElectricalBuffered => DeviceCapability.BufferedElectrical,
        NativeProgramKind.MotorPreset => DeviceCapability.MotorPresets,
        NativeProgramKind.CoyoteV2Pulse => DeviceCapability.RawPulse,
        _ => DeviceCapability.None
    };
    public void Validate()
    {
        ProtocolGuard.Require(Enum.IsDefined(Kind));
        if (Kind is NativeProgramKind.MotorPreset or NativeProgramKind.ElectricalPreset)
        {
            var maximum = Kind == NativeProgramKind.MotorPreset ? 255 : 16;
            ProtocolGuard.Byte(PresetA, 0, maximum);
            ProtocolGuard.Byte(PresetB, 0, maximum);
            ProtocolGuard.Byte(PresetC, 0, maximum);
        }
        if (Kind == NativeProgramKind.ElectricalBuffered)
        {
            ProtocolGuard.Require(BufferA is { Count: >= 1 and <= 100 } && BufferB is { Count: >= 1 and <= 100 });
            foreach (var sample in BufferA!)
            {
                ProtocolGuard.Require(sample is not null);
                sample!.Validate();
            }
            foreach (var sample in BufferB!)
            {
                ProtocolGuard.Require(sample is not null);
                sample!.Validate();
            }
        }
        if (Kind == NativeProgramKind.CoyoteV2Pulse)
        {
            (PulseA ?? new()).Validate();
            (PulseB ?? new()).Validate();
        }
    }
    public NativeProgramSettings Snapshot() => this with { BufferA = BufferA?.ToArray(), BufferB = BufferB?.ToArray() };
    public bool Accepts(DeviceDescriptor device) => device.Profile is { } profile ? profile.Supports(RequiredCapability)
        : Kind == NativeProgramKind.MotorPreset ? device.Kind == DeviceKind.Vibration : device.Kind == DeviceKind.DualChannel;
}
public sealed record NativeProgramOutput(NativeProgramSettings Settings, double ElapsedSeconds, double? RemainingSeconds)
{
    public void ValidateOutput(OutputFrame frame)
    {
        Settings.Validate();
        ProtocolGuard.Require(double.IsFinite(ElapsedSeconds) && ElapsedSeconds >= 0 &&
            (RemainingSeconds is null || double.IsFinite(RemainingSeconds.Value) && RemainingSeconds >= 0));
        if (Settings.Kind == NativeProgramKind.MotorPreset && (frame.OutputLimit != 100 ||
            frame.Hardware is { ChannelALimit: not 100 } or { ChannelBLimit: not 100 } ||
            new[] { frame.ChannelA, frame.ChannelB, frame.ChannelC }.Any(value => value is > 0 and < 100)))
            throw new Localization.LocalizedArgumentException("Error.NativeMotorLimit");
    }
}

/// <summary>Hardware preferences are stored per device; drivers retain protocol synchronization state.</summary>
public sealed record HardwareOutputSettings(double ChannelALimit = 100, double ChannelBLimit = 100,
    AuxiliaryMotorMode AuxiliaryMotor = AuxiliaryMotorMode.Off, bool RelativeStrength = false)
{
    public void Validate()
    {
        ProtocolGuard.Require(new[] { ChannelALimit, ChannelBLimit }.All(value => double.IsFinite(value) && value is >= 0 and <= 100));
        ProtocolGuard.Require(Enum.IsDefined(AuxiliaryMotor));
    }
    public bool Accepts(DeviceDescriptor device, bool needsMotion)
    {
        if (AuxiliaryMotor != AuxiliaryMotorMode.Off && (needsMotion || device.Profile is { } p && !p.Supports(DeviceCapability.AuxiliaryMotor)))
            return false;
        return !RelativeStrength || device.Profile?.Supports(DeviceCapability.RelativeStrength) != false;
    }
}
