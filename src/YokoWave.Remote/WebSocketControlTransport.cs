using System.Collections.Concurrent;
using System.Text.Json;
using YokoWave.Core;
using YokoWave.Core.Bluetooth;
using YokoWave.Core.Localization;

namespace YokoWave.Remote;

/// <summary>Device transport for high-level control endpoints. Output still passes through the host graph.</summary>
public sealed class WebSocketControlTransport : IDeviceTransport, IDeviceStatusSource, IDeviceCommandTransport, IElectricalMotionSource, IElectricalDeviceFeatures, IInputDeviceFeatures
{
    private readonly RemoteClientConnection _connection;
    private readonly ConcurrentDictionary<string, RemoteDevice> _devices = new();
    private readonly ConcurrentDictionary<string, RemoteDeviceLink> _links = new();
    public WebSocketControlTransport(RemoteEndpointSettings settings, string token)
    {
        _connection = new(settings, token);
        _connection.Notification += Notification;
        _connection.Disconnected += () => { foreach (var link in _links.Values) link.Lost(); };
    }
    public string DisplayName => L10n.Get("Remote.Control");
    public bool IsSimulation => false;
    public event EventHandler<DeviceStatusUpdate>? StatusChanged;
    public event EventHandler<DeviceTransportFault>? Faulted;
    public Task ConnectAsync(CancellationToken ct = default) => _connection.ConnectAsync(ct);
    public async Task<IReadOnlyList<DeviceDescriptor>> ScanAsync(CancellationToken cancellationToken = default)
    {
        await ConnectAsync(cancellationToken);
        var devices = await _connection.Peer.CallAsync<RemoteDevice[]>("devices.list", ct: cancellationToken);
        _devices.Clear();
        foreach (var device in devices)
            _devices[_connection.Key(device.Id)] = device;
        return _devices.Select(p => new DeviceDescriptor(p.Key, p.Value.Name, p.Value.Kind, p.Value.Battery, p.Value.Signal,
            p.Value.ProtocolId, p.Value.ChannelMask, DeviceOrigin.Remote, p.Value.Simulated)).ToArray();
    }
    public async Task ConnectAsync(DeviceDescriptor device, CancellationToken cancellationToken = default)
    {
        if (!_devices.TryGetValue(device.Id, out var exported))
            throw new LocalizedOperationException("Error.BluetoothRescan");
        var lease = await _connection.Peer.CallAsync<RemoteLease>("lease.acquire", new
        {
            deviceIds = new[] { exported.Id },
            type = RemoteLeaseType.Control
        }, cancellationToken);
        var link = new RemoteDeviceLink(_connection.Peer, exported, lease, _connection.Hello.ClockMilliseconds, _connection.HelloAt);
        link.Disconnected += () => Faulted?.Invoke(this, new(device.Id, true, new LocalizedIOException("Error.RemoteDisconnected")));
        if (!_links.TryAdd(device.Id, link))
        {
            await link.DisposeAsync();
            throw new LocalizedOperationException("Error.DeviceBusy");
        }
        try
        {
            link.Start();
            await link.Call<bool>("control.subscribe", new
            {
            }, cancellationToken);
            Update(device.Id, await link.Call<RemoteStatus>("control.status", new
            {
            }, cancellationToken));
        }
        catch (Exception error)
        {
            try
            {
                await DisconnectAsync(device.Id);
            }
            catch (Exception cleanup) { throw new LocalizedAggregateException("Error.Cleanup", [error, cleanup]); }
            throw;
        }
    }
    private RemoteDeviceLink Link(string id) => _links.TryGetValue(id, out var link) ? link : throw new LocalizedOperationException("Error.NotConnected");
    public async Task SendAsync(string deviceId, OutputFrame frame, CancellationToken cancellationToken = default) =>
        await Link(deviceId).Call<bool>("control.output", new
        {
            frame
        }, cancellationToken);
    public async Task ExecuteCommandAsync(string deviceId, DeviceCommand command, CancellationToken ct = default) =>
        await Link(deviceId).Call<bool>("control.command", new
        {
            command
        }, ct);
    public Task SetMotionStreamingAsync(string deviceId, bool enabled, CancellationToken cancellationToken = default) =>
        ExecuteCommandAsync(deviceId, new ConfigureMotionStream(enabled), cancellationToken);
    public Task ResetInputAsync(string deviceId, CancellationToken cancellationToken = default) =>
        ExecuteCommandAsync(deviceId, new ConfigurePressureSensor(true), cancellationToken);
    public async Task<ElectricalFeatureState> RefreshFeaturesAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        await ExecuteCommandAsync(deviceId, new QueryDevice(DeviceQuery.All), cancellationToken);
        var status = await Link(deviceId).Call<RemoteStatus>("control.status", new
        {
        }, cancellationToken);
        Update(deviceId, status);
        return new(status.HardwareVersion ?? 0, status.SoftwareVersion ?? 0, status.LightEnabled ?? false);
    }
    public async Task<bool> SetLightAsync(string deviceId, bool enabled, CancellationToken cancellationToken = default)
    {
        await ExecuteCommandAsync(deviceId, new ConfigureLight(enabled), cancellationToken);
        return enabled;
    }
    private void Notification(string method, JsonElement parameters)
    {
        if (method == "lease.revoked")
        {
            var id = RemoteProtocol.Parameter<Guid>(parameters, "lease");
            foreach (var link in _links.Values.Where(l => l.Id == id))
                link.Lost();
        }
        if (method != "control.status")
            return;
        var lease = RemoteProtocol.Parameter<Guid>(parameters, "lease");
        var status = RemoteProtocol.Parameter<RemoteStatus>(parameters, "status");
        foreach (var pair in _links.Where(p => p.Value.Id == lease && p.Value.DeviceId == status.DeviceId))
            Update(pair.Key, status);
    }
    private void Update(string id, RemoteStatus status) => StatusChanged?.Invoke(this, new(id, status.Battery,
        status.ContactA, status.ContactB, lightEnabled: status.LightEnabled, motionAngle: status.Motion?.Angle,
        steps: status.Motion?.Steps, telemetry: status.Telemetry, hardwareVersion: status.HardwareVersion, softwareVersion: status.SoftwareVersion));
    public async Task DisconnectAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        if (_links.TryRemove(deviceId, out var link))
            await link.DisposeAsync();
    }
    public async ValueTask DisposeAsync()
    {
        List<Exception> errors = [];
        foreach (var id in _links.Keys)
        {
            try
            {
                await DisconnectAsync(id);
            }
            catch (Exception error) { errors.Add(error); }
        }
        try
        {
            await _connection.DisposeAsync();
        }
        catch (Exception error) { errors.Add(error); }
        if (errors.Count > 0)
            throw new LocalizedAggregateException("Error.Cleanup", errors);
    }
}
