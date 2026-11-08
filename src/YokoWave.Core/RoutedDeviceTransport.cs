using System.Collections.Concurrent;
using YokoWave.Core.Bluetooth;
using YokoWave.Core.Localization;

namespace YokoWave.Core;

/// <summary>Providers retain their connections; discovered device IDs select the provider for every operation.</summary>
public sealed class RoutedDeviceTransport : IDeviceTransport, IDeviceStatusSource, IDeviceCommandTransport,
    IElectricalDeviceFeatures, IElectricalMotionSource, IInputDeviceFeatures, IRawGattTransport
{
    private readonly ConcurrentDictionary<string, IDeviceTransport> _providers = new();
    private readonly ConcurrentDictionary<string, IDeviceTransport> _routes = new();
    public bool IsSimulation => _providers.Values.All(provider => provider.IsSimulation);
    public bool IsLocalSimulation => _providers.TryGetValue("local", out var local) && local.IsSimulation;
    public string DisplayName => _providers.TryGetValue("local", out var local) ? local.DisplayName : L10n.Get("Remote.Title");
    public event EventHandler<DeviceStatusUpdate>? StatusChanged;
    public event EventHandler<DeviceTransportFault>? Faulted;
    public event Action<GattDeviceNotification>? GattNotificationReceived;
    public RoutedDeviceTransport(IDeviceTransport local) => Add("local", local);
    public void Add(string key, IDeviceTransport transport)
    {
        if (!_providers.TryAdd(key, transport)) throw new LocalizedOperationException("Error.RemoteExists");
        if (transport is IDeviceStatusSource source)
        {
            source.StatusChanged += ForwardStatus;
            source.Faulted += ForwardFault;
        }
        if (transport is IRawGattTransport raw) raw.GattNotificationReceived += ForwardGatt;
    }
    private void ForwardStatus(object? sender, DeviceStatusUpdate value) => StatusChanged?.Invoke(this, value);
    private void ForwardFault(object? sender, DeviceTransportFault value) => Faulted?.Invoke(this, value);
    private void ForwardGatt(GattDeviceNotification value) => GattNotificationReceived?.Invoke(value);
    public async Task RemoveAsync(string key)
    {
        if (!_providers.TryRemove(key, out var provider)) return;
        if (provider is IDeviceStatusSource source)
        {
            source.StatusChanged -= ForwardStatus;
            source.Faulted -= ForwardFault;
        }
        if (provider is IRawGattTransport raw) raw.GattNotificationReceived -= ForwardGatt;
        foreach (var pair in _routes.Where(pair => pair.Value == provider).ToArray()) _routes.TryRemove(pair.Key, out _);
        await provider.DisposeAsync();
    }
    public async Task ReplaceLocalAsync(IDeviceTransport next)
    {
        await RemoveAsync("local");
        Add("local", next);
    }
    private IDeviceTransport Provider(string id) => _routes.TryGetValue(id, out var provider) ? provider : throw new LocalizedOperationException("Error.BluetoothRescan");
    public bool IsSimulated(string id) => Provider(id).IsSimulation;
    public IReadOnlyList<string> DeviceIds(string key) => _providers.TryGetValue(key, out var provider) ? _routes.Where(pair => pair.Value == provider).Select(pair => pair.Key).ToArray() : [];
    private T Feature<T>(string id) => Provider(id) is T feature ? feature : throw new LocalizedOperationException("Error.DeviceFeatures");
    public async Task<IReadOnlyList<DeviceDescriptor>> ScanAsync(CancellationToken cancellationToken = default)
    {
        var scans = _providers.Select(async pair =>
        {
            try
            {
                var devices = await pair.Value.ScanAsync(cancellationToken);
                foreach (var device in devices)
                    if (!_routes.TryAdd(device.Id, pair.Value) && _routes[device.Id] != pair.Value) throw new LocalizedOperationException("Error.RemoteIdCollision");
                return devices.Select(device => device with { Simulated = device.Simulated ?? pair.Value.IsSimulation }).ToArray();
            }
            catch (Exception error) when (pair.Key != "local" && error is not OperationCanceledException)
            {
                System.Diagnostics.Trace.TraceWarning("Remote discovery failed: {0}", error);
                return [];
            }
        }).ToArray();
        return (await Task.WhenAll(scans)).SelectMany(devices => devices).ToArray();
    }
    public Task ConnectAsync(DeviceDescriptor device, CancellationToken cancellationToken = default)
    {
        if (!_routes.ContainsKey(device.Id) && device.Origin == DeviceOrigin.Local) _routes.TryAdd(device.Id, _providers["local"]);
        return Provider(device.Id).ConnectAsync(device, cancellationToken);
    }
    public Task SendAsync(string deviceId, OutputFrame frame, CancellationToken cancellationToken = default) => Provider(deviceId).SendAsync(deviceId, frame, cancellationToken);
    public Task DisconnectAsync(string deviceId, CancellationToken cancellationToken = default) => Provider(deviceId).DisconnectAsync(deviceId, cancellationToken);
    public Task ExecuteCommandAsync(string deviceId, DeviceCommand command, CancellationToken cancellationToken = default) => Feature<IDeviceCommandTransport>(deviceId).ExecuteCommandAsync(deviceId, command, cancellationToken);
    public Task<ElectricalFeatureState> RefreshFeaturesAsync(string deviceId, CancellationToken cancellationToken = default) => Feature<IElectricalDeviceFeatures>(deviceId).RefreshFeaturesAsync(deviceId, cancellationToken);
    public Task<bool> SetLightAsync(string deviceId, bool enabled, CancellationToken cancellationToken = default) => Feature<IElectricalDeviceFeatures>(deviceId).SetLightAsync(deviceId, enabled, cancellationToken);
    public Task SetMotionStreamingAsync(string deviceId, bool enabled, CancellationToken cancellationToken = default) => Provider(deviceId) is IElectricalMotionSource motion
        ? motion.SetMotionStreamingAsync(deviceId, enabled, cancellationToken) : Provider(deviceId).IsSimulation ? Task.CompletedTask : throw new LocalizedOperationException("Error.MotionUnavailable");
    public Task ResetInputAsync(string deviceId, CancellationToken cancellationToken = default) => Feature<IInputDeviceFeatures>(deviceId).ResetInputAsync(deviceId, cancellationToken);
    public Task<GattDescription> OpenGattAsync(string id, Guid lease, CancellationToken ct = default) => Feature<IRawGattTransport>(id).OpenGattAsync(id, lease, ct);
    public Task<byte[]?> ReadGattAsync(string id, Guid lease, GattAddress address, CancellationToken ct = default) => Feature<IRawGattTransport>(id).ReadGattAsync(id, lease, address, ct);
    public Task WriteGattAsync(string id, Guid lease, IReadOnlyList<GattWrite> writes, CancellationToken ct = default) => Feature<IRawGattTransport>(id).WriteGattAsync(id, lease, writes, ct);
    public Task CloseGattAsync(string id, Guid lease, CancellationToken ct = default) => Feature<IRawGattTransport>(id).CloseGattAsync(id, lease, ct);
    public async ValueTask DisposeAsync()
    {
        List<Exception> errors = [];
        foreach (var key in _providers.Keys)
        {
            try { await RemoveAsync(key); }
            catch (Exception error) { errors.Add(error); }
        }
        if (errors.Count > 0) throw new LocalizedAggregateException("Error.Cleanup", errors);
    }
}
