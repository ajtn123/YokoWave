namespace YokoWave.Core.Bluetooth;

/// <summary>Configuration and read requests, without direct output control. Output belongs to an owning playback mode.</summary>
public abstract record DeviceCommand
{
    protected DeviceCommand(DeviceCapability capability) => Capability = capability;
    [System.Text.Json.Serialization.JsonIgnore]
    public DeviceCapability Capability
    { get; }
    [System.Text.Json.Serialization.JsonIgnore]
    public virtual bool ReadOnly => false;
    public abstract void Validate();
}

public enum DeviceQuery { All, ChannelA, ChannelB, AuxiliaryMotor, Battery, Steps, Motion, Firmware, Light, Product }
public sealed record QueryDevice(DeviceQuery Query = DeviceQuery.All) : DeviceCommand(DeviceCapability.StatusQuery)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public override bool ReadOnly => true;
    public override void Validate() => ProtocolGuard.Require(Enum.IsDefined(Query));
}

public enum StepCounterAction { Stop, Start, Reset, Pause, Resume }
public sealed record ConfigureMotionStream(bool Enabled) : DeviceCommand(DeviceCapability.Motion)
{
    public override void Validate() { }
}
public sealed record ControlStepCounter(StepCounterAction Action) : DeviceCommand(DeviceCapability.StepCounter)
{
    public override void Validate() => ProtocolGuard.Require(Enum.IsDefined(Action));
}

public sealed record ConfigureSensorStream(bool Enabled, int IndicatorColor = 1) : DeviceCommand(DeviceCapability.SensorStream)
{
    public override void Validate() => ProtocolGuard.Byte(IndicatorColor, 1, 6);
}

public sealed record ConfigureIndicator(int Color) : DeviceCommand(DeviceCapability.IndicatorColor)
{
    public override void Validate() => ProtocolGuard.Byte(Color, 1, 6);
}

public enum ShoulderBlink { Solid, Slow, Fast, Stop }
public sealed record ConfigureShoulderLight(int Color = 0, int AlternateColor = 0, ShoulderBlink Blink = ShoulderBlink.Solid)
    : DeviceCommand(DeviceCapability.ShoulderLight)
{
    public override void Validate()
    {
        ProtocolGuard.Byte(Color, 0, 7);
        ProtocolGuard.Byte(AlternateColor, 0, 7);
        ProtocolGuard.Require(Enum.IsDefined(Blink));
    }
}

public sealed record CalibratePawAngles() : DeviceCommand(DeviceCapability.PawCalibration)
{
    public override void Validate() { }
}

public sealed record ResetPawParameters() : DeviceCommand(DeviceCapability.PawTriggers)
{
    public override void Validate() { }
}

public enum SensorOrientation { Unchanged = 0, Normal = 1, Flipped = 3 }
public sealed record ConfigurePressureSensor(bool ResetPressure = false, SensorOrientation Orientation = SensorOrientation.Unchanged)
    : DeviceCommand(DeviceCapability.ScreenOrientation | DeviceCapability.PressureReset)
{
    public override void Validate() => ProtocolGuard.Require(Enum.IsDefined(Orientation));
}

public sealed record SynchronizeDisplay() : DeviceCommand(DeviceCapability.DisplaySync)
{
    public override void Validate() { }
}

public interface IDeviceCommandTransport
{
    Task ExecuteCommandAsync(string deviceId, DeviceCommand command, CancellationToken cancellationToken = default);
}

internal sealed class DeviceCommandHandlers
{
    private readonly Dictionary<Type, Func<DeviceCommand, CancellationToken, Task>> _handlers = [];
    public DeviceCommandHandlers Add<T>(Func<T, CancellationToken, Task> handler) where T : DeviceCommand
    {
        _handlers.Add(typeof(T), (command, ct) => handler((T)command, ct));
        return this;
    }
    public Task ExecuteAsync(DeviceCommand command, CancellationToken ct) => _handlers.TryGetValue(command.GetType(), out var handler)
        ? handler(command, ct) : throw new Localization.LocalizedOperationException("Error.DeviceFeatures");
}
