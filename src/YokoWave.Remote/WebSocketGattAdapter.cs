using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using YokoWave.Core;
using YokoWave.Core.Bluetooth;
using YokoWave.Core.Localization;

namespace YokoWave.Remote;

/// <summary>Virtual GATT links feed the existing protocol driver factory on the controlling machine.</summary>
public sealed class WebSocketGattAdapter(RemoteEndpointSettings settings, string token) : IBluetoothAdapter, IAsyncDisposable
{
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private readonly ConcurrentDictionary<string, RemoteDevice> _devices = new();
    private readonly ConcurrentDictionary<Guid, WebSocketGattConnection> _links = new();
    private WebSocketRpcPeer? _peer;
    private RemoteHello? _hello;
    private long _helloAt;
    private bool _disposed;
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        settings.Validate();
        await _connectionGate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_peer is { Completion.IsCompleted: false }) return;
            if (_peer is not null) await _peer.DisposeAsync();
            var socket = new ClientWebSocket();
            socket.Options.AddSubProtocol(RemoteProtocol.SubProtocol);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                await socket.ConnectAsync(new Uri(settings.Url), timeout.Token);
                var peer = new WebSocketRpcPeer(socket);
                peer.Notification += OnNotification;
                peer.Start();
                _peer = peer;
                await peer.CallAsync<bool>("system.authenticate", new { token }, timeout.Token);
                _hello = await peer.CallAsync<RemoteHello>("system.hello", ct: timeout.Token);
                _helloAt = System.Diagnostics.Stopwatch.GetTimestamp();
                if (_hello.Version != 1) throw new LocalizedOperationException("Error.RemoteVersion");
                _ = Observe(peer);
            }
            catch (Exception error)
            {
                try
                {
                    if (_peer is not null) await _peer.DisposeAsync();
                    else socket.Dispose();
                }
                catch (Exception cleanup) { throw new LocalizedAggregateException("Error.Cleanup", [error, cleanup]); }
                _peer = null;
                throw;
            }
        }
        finally { _connectionGate.Release(); }
    }
    private async Task Observe(WebSocketRpcPeer peer)
    {
        await peer.Completion;
        foreach (var link in _links.Values) link.Lost();
    }
    private void OnNotification(string method, JsonElement parameters)
    {
        if (method == "gatt.notification")
        {
            var value = RemoteProtocol.Read<RemoteGattNotification>(parameters);
            if (_links.TryGetValue(value.Lease, out var link)) link.Notify(value);
        }
        if (method == "lease.revoked" && _links.TryGetValue(RemoteProtocol.Parameter<Guid>(parameters, "lease"), out var revoked)) revoked.Lost();
    }
    public async Task<IReadOnlyList<BluetoothPeripheral>> ScanAsync(CancellationToken ct)
    {
        await ConnectAsync(ct);
        var devices = await _peer!.CallAsync<RemoteDevice[]>("devices.list", ct: ct);
        _devices.Clear();
        foreach (var device in devices) _devices[Key(device.Id)] = device;
        return _devices.Select(pair => new BluetoothPeripheral(pair.Key, pair.Value.Name, pair.Value.Signal,
            BluetoothDeviceProfiles.FromId(pair.Value.ProtocolId)?.Endpoints.Select(endpoint => endpoint.Address.Service).Distinct().ToArray(),
            DeviceOrigin.Remote, pair.Value.Simulated)).ToArray();
    }
    private string Key(string exported) => $"ws:{settings.Id:N}:{_hello!.ServerId:N}:{exported}";
    public async Task<IBluetoothConnection> ConnectAsync(BluetoothPeripheral peripheral, CancellationToken ct)
    {
        if (!_devices.TryGetValue(peripheral.Id, out var device) || _peer is null) throw new LocalizedOperationException("Error.BluetoothRescan");
        var lease = await _peer.CallAsync<RemoteLease>("lease.acquire", new { deviceIds = new[] { device.Id }, type = RemoteLeaseType.Gatt }, ct);
        var connection = new WebSocketGattConnection(_peer, device, lease, _hello!.ClockMilliseconds, _helloAt, () => _links.TryRemove(lease.Id, out _));
        _links[lease.Id] = connection;
        try
        {
            await connection.Initialize(ct);
            return connection;
        }
        catch (Exception error)
        {
            try { await connection.DisposeAsync(); }
            catch (Exception cleanup) { throw new LocalizedAggregateException("Error.Cleanup", [error, cleanup]); }
            throw;
        }
    }
    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        List<Exception> errors = [];
        foreach (var connection in _links.Values)
        {
            try { await connection.DisposeAsync(); }
            catch (Exception error) { errors.Add(error); }
        }
        if (_peer is not null)
        {
            try { await _peer.DisposeAsync(); }
            catch (Exception error) { errors.Add(error); }
        }
        if (errors.Count > 0) throw new LocalizedAggregateException("Error.Cleanup", errors);
    }
}
