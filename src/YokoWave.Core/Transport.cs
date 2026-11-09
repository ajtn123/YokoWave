using YokoWave.Core.Localization;
namespace YokoWave.Core;

/// <summary>Normalized application values. A hardware adapter must supply its own verified encoding.</summary>
public interface IDeviceTransport : IAsyncDisposable
{
    string DisplayName { get; }
    bool IsSimulation => true;
    Task<IReadOnlyList<DeviceDescriptor>> ScanAsync(CancellationToken cancellationToken = default);
    Task ConnectAsync(DeviceDescriptor device, CancellationToken cancellationToken = default);
    Task SendAsync(string deviceId, OutputFrame frame, CancellationToken cancellationToken = default);
    Task DisconnectAsync(string deviceId, CancellationToken cancellationToken = default);
}

public enum ElectrodeContact { NotConnected, Delivering, Connected, Unknown }

public sealed class DeviceStatusUpdate(string deviceId, int? battery = null,
    ElectrodeContact? contactA = null, ElectrodeContact? contactB = null,
    bool? outputAEnabled = null, bool? outputBEnabled = null,
    byte? hardwareVersion = null, byte? softwareVersion = null, bool? lightEnabled = null,
    double? motionAngle = null, int? steps = null, int? channelMask = null, DeviceTelemetry? telemetry = null,
    TimeSpan? motionAge = null, TimeSpan? telemetryAge = null) : EventArgs
{
    public string DeviceId { get; } = deviceId;
    public int? Battery { get; } = battery;
    public ElectrodeContact? ContactA { get; } = contactA;
    public ElectrodeContact? ContactB { get; } = contactB;
    public bool? OutputAEnabled { get; } = outputAEnabled;
    public bool? OutputBEnabled { get; } = outputBEnabled;
    public byte? HardwareVersion { get; } = hardwareVersion;
    public byte? SoftwareVersion { get; } = softwareVersion;
    public bool? LightEnabled { get; } = lightEnabled;
    public double? MotionAngle { get; } = motionAngle;
    public int? Steps { get; } = steps;
    public int? ChannelMask { get; } = channelMask;
    public DeviceTelemetry? Telemetry { get; } = telemetry;
    public TimeSpan? MotionAge { get; } = motionAge;
    public TimeSpan? TelemetryAge { get; } = telemetryAge;
}

public sealed class DeviceTransportFault(string deviceId, bool disconnected, Exception error) : EventArgs
{
    public string DeviceId { get; } = deviceId;
    public bool Disconnected { get; } = disconnected;
    public Exception Error { get; } = error;
}

public interface IDeviceStatusSource
{
    event EventHandler<DeviceStatusUpdate>? StatusChanged;
    event EventHandler<DeviceTransportFault>? Faulted;
}

public interface IElectricalDeviceFeatures
{
    Task<ElectricalFeatureState> RefreshFeaturesAsync(string deviceId, CancellationToken cancellationToken = default);
    Task<bool> SetLightAsync(string deviceId, bool enabled, CancellationToken cancellationToken = default);
}

public interface IElectricalMotionSource
{
    Task SetMotionStreamingAsync(string deviceId, bool enabled, CancellationToken cancellationToken = default);
}

public interface IInputDeviceFeatures
{
    Task ResetInputAsync(string deviceId, CancellationToken cancellationToken = default);
}

public sealed record MotionReading(double Angle, long Sequence, DateTimeOffset CapturedAt, int? Steps = null);

public sealed record ElectricalFeatureState(byte HardwareVersion, byte SoftwareVersion, bool LightEnabled);

/// <summary>Deterministic local peripherals; never accesses the system Bluetooth adapter.</summary>
public sealed class SimulationTransport : IDeviceTransport
{
    private readonly Dictionary<string, OutputFrame> _connected = [];
    public string DisplayName => L10n.Get("Mode.Simulation");
    public IReadOnlyDictionary<string, OutputFrame> Outputs => _connected;

    public async Task<IReadOnlyList<DeviceDescriptor>> ScanAsync(CancellationToken cancellationToken = default)
    {
        await Task.Delay(650, cancellationToken).ConfigureAwait(false);
        return
        [
            new("sim-dual-01", "Pulse Duo · 01", DeviceKind.DualChannel, 86, -42),
            new("sim-dual-02", "Pulse Duo · 02", DeviceKind.DualChannel, 72, -55),
            new("sim-vibe-01", "Vibe Mini", DeviceKind.Vibration, 94, -48),
            new("sim-pump-01", "Air Flow", DeviceKind.Pump, 68, -61),
            new("sim-linear-01", "Motion One", DeviceKind.Linear, 80, -50)
        ];
    }

    public async Task ConnectAsync(DeviceDescriptor device, CancellationToken cancellationToken = default)
    {
        await Task.Delay(120, cancellationToken).ConfigureAwait(false);
        _connected.TryAdd(device.Id, OutputFrame.Zero);
    }

    public Task SendAsync(string deviceId, OutputFrame frame, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_connected.ContainsKey(deviceId))
            throw new LocalizedOperationException("Error.NotConnected");
        _connected[deviceId] = frame;
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        _connected.Remove(deviceId);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _connected.Clear();
        return ValueTask.CompletedTask;
    }
}
