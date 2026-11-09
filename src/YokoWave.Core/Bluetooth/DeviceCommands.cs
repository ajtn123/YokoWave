namespace YokoWave.Core.Bluetooth;

/// <summary>Configuration and read requests, without direct output control. Output belongs to an owning playback mode.</summary>
[System.Text.Json.Serialization.JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(QueryDevice), "query")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(ConfigureMotionStream), "motion")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(ControlStepCounter), "steps")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(ConfigureSensorStream), "stream")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(ConfigureIndicator), "indicator")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(ConfigureShoulderLight), "shoulder")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(CalibratePawAngles), "calibrate")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(ResetPawParameters), "resetPaw")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(ConfigurePressureSensor), "pressure")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(SynchronizeDisplay), "display")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(ConfigurePaw), "paw")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(ConfigureLight), "light")]
public abstract record DeviceCommand
{
    [System.Text.Json.Serialization.JsonIgnore]
    public DeviceCapability Capability
    { get; }
    [System.Text.Json.Serialization.JsonIgnore]
    public virtual bool ReadOnly => false;
    protected DeviceCommand(DeviceCapability capability) => Capability = capability;
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
        _handlers.Add(typeof(T), (command, cancellationToken) => handler((T)command, cancellationToken));
        return this;
    }
    public Task ExecuteAsync(DeviceCommand command, CancellationToken cancellationToken) => _handlers.TryGetValue(command.GetType(), out var handler)
        ? handler(command, cancellationToken) : throw new Localization.LocalizedOperationException("Error.DeviceFeatures");
}

public sealed record ConfigureLight(bool Enabled) : DeviceCommand(DeviceCapability.Light)
{
    public override void Validate() { }
}
