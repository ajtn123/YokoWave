namespace YokoWave.Core;

/// <summary>Normalized application values. A hardware adapter must supply its own verified encoding.</summary>
public interface IDeviceTransport : IAsyncDisposable
{
    string DisplayName { get; }
    Task<IReadOnlyList<DeviceDescriptor>> ScanAsync(CancellationToken cancellationToken = default);
    Task ConnectAsync(DeviceDescriptor device, CancellationToken cancellationToken = default);
    Task SendAsync(string deviceId, OutputFrame frame, CancellationToken cancellationToken = default);
    Task DisconnectAsync(string deviceId, CancellationToken cancellationToken = default);
}

/// <summary>Deterministic local peripherals; never accesses the system Bluetooth adapter.</summary>
public sealed class SimulationTransport : IDeviceTransport
{
    private readonly Dictionary<string, OutputFrame> _connected = [];
    public string DisplayName => "本地模拟";
    public IReadOnlyDictionary<string, OutputFrame> Outputs => _connected;

    public async Task<IReadOnlyList<DeviceDescriptor>> ScanAsync(CancellationToken cancellationToken = default)
    {
        await Task.Delay(650, cancellationToken);
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
        await Task.Delay(120, cancellationToken);
        _connected.TryAdd(device.Id, OutputFrame.Zero);
    }

    public Task SendAsync(string deviceId, OutputFrame frame, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_connected.ContainsKey(deviceId)) throw new InvalidOperationException("设备尚未连接。");
        _connected[deviceId] = frame;
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        _connected.Remove(deviceId);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() { _connected.Clear(); return ValueTask.CompletedTask; }
}
