using System.Collections.Concurrent;
using System.Text.Json;
using YokoWave.Core;
using YokoWave.Core.Bluetooth;
using YokoWave.Core.Localization;

namespace YokoWave.Remote;

/// <summary>Virtual GATT links feed the existing protocol driver factory on the controlling machine.</summary>
public sealed class WebSocketGattAdapter : IBluetoothAdapter, IAsyncDisposable
{
    private readonly RemoteClientConnection _connection;
    private readonly ConcurrentDictionary<string, RemoteDevice> _devices = new();
    private readonly ConcurrentDictionary<Guid, WebSocketGattConnection> _links = new();
    public WebSocketGattAdapter(RemoteEndpointSettings settings, string token)
    {
        _connection = new(settings, token);
        _connection.Notification += OnNotification;
        _connection.Disconnected += () => { foreach (var link in _links.Values) link.Lost(); };
    }
    public Task ConnectAsync(CancellationToken ct = default) => _connection.ConnectAsync(ct);
    private void OnNotification(string method, JsonElement parameters)
    {
        if (method == "gatt.notification")
        {
            var value = RemoteProtocol.Read<RemoteGattNotification>(parameters);
            if (_links.TryGetValue(value.Lease, out var link))
                link.Notify(value);
        }
        if (method == "lease.revoked" && _links.TryGetValue(RemoteProtocol.Parameter<Guid>(parameters, "lease"), out var revoked))
            revoked.Lost();
    }
    public async Task<IReadOnlyList<BluetoothPeripheral>> ScanAsync(CancellationToken ct)
    {
        await ConnectAsync(ct);
        var devices = await _connection.Peer.CallAsync<RemoteDevice[]>("devices.list", ct: ct);
        _devices.Clear();
        foreach (var device in devices)
            _devices[_connection.Key(device.Id)] = device;
        return _devices.Select(pair => new BluetoothPeripheral(pair.Key, pair.Value.Name, pair.Value.Signal,
            BluetoothDeviceProfiles.FromId(pair.Value.ProtocolId)?.Endpoints.Select(endpoint => endpoint.Address.Service).Distinct().ToArray(),
            DeviceOrigin.Remote, pair.Value.Simulated)).ToArray();
    }
    public async Task<IBluetoothConnection> ConnectAsync(BluetoothPeripheral peripheral, CancellationToken ct)
    {
        if (!_devices.TryGetValue(peripheral.Id, out var device) || _connection.Peer is null)
            throw new LocalizedOperationException("Error.BluetoothRescan");
        var lease = await _connection.Peer.CallAsync<RemoteLease>("lease.acquire", new
        {
            deviceIds = new[] { device.Id },
            type = RemoteLeaseType.Gatt
        }, ct);
        var connection = new WebSocketGattConnection(_connection.Peer, device, lease, _connection.Hello.ClockMilliseconds, _connection.HelloAt, () => _links.TryRemove(lease.Id, out _));
        _links[lease.Id] = connection;
        try
        {
            await connection.Initialize(ct);
            return connection;
        }
        catch (Exception error)
        {
            try
            {
                await connection.DisposeAsync();
            }
            catch (Exception cleanup) { throw new LocalizedAggregateException("Error.Cleanup", [error, cleanup]); }
            throw;
        }
    }
    public async ValueTask DisposeAsync()
    {
        List<Exception> errors = [];
        foreach (var connection in _links.Values)
        {
            try
            {
                await connection.DisposeAsync();
            }
            catch (Exception error) { errors.Add(error); }
        }
        if (_connection.Peer is not null)
        {
            try
            {
                await _connection.DisposeAsync();
            }
            catch (Exception error) { errors.Add(error); }
        }
        if (errors.Count > 0)
            throw new LocalizedAggregateException("Error.Cleanup", errors);
    }
}
