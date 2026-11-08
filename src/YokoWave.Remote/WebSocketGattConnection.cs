using System.Net.WebSockets;
using System.Text.Json;
using YokoWave.Core.Bluetooth;
using YokoWave.Core.Localization;

namespace YokoWave.Remote;

internal sealed class WebSocketGattConnection(WebSocketRpcPeer peer, RemoteDevice device, RemoteLease lease, long serverClock, long clockAt, Action removed) : IBluetoothConnection
{
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private RemoteGattDescription? _description;
    private Task _renew = Task.CompletedTask;
    private long _sequence;
    private int _lost, _disposed;
    private readonly RemoteClock _clock = new(serverClock, clockAt);
    public int MaximumWriteLength => _description?.MaximumWriteLength ?? 20;
    public IReadOnlyList<GattAttribute> Attributes => _description?.Attributes ?? [];
    public event Action<byte[]>? NotificationReceived;
    public event Action<GattNotification>? AdditionalNotificationReceived;
    public event Action? Disconnected;
    private GattAddress Primary => BluetoothDeviceProfiles.FromId(device.ProtocolId)!.Notify;
    public async Task Initialize(CancellationToken ct)
    {
        _description = await Operation<RemoteGattDescription>("gatt.describe", new { }, ct);
        foreach (var attribute in Attributes.Where(attribute => attribute.Notify))
            await Operation<bool>("gatt.subscribe", new { address = attribute.Address }, ct);
        _renew = Renew();
    }
    private async Task Renew()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(Math.Max(100, lease.LeaseMilliseconds / 3)));
            while (await timer.WaitForNextTickAsync(_lifetime.Token))
            {
                // Cancelling a heartbeat during normal link disposal must not close other device links.
                var heartbeat = await peer.CallAsync<RemoteHeartbeat>("lease.renew", new { lease = lease.Id }, _lifetime.Token, abortOnCancellation: false);
                _clock.Update(heartbeat.ClockMilliseconds);
            }
        }
        catch (Exception error) when (error is not StackOverflowException and not OutOfMemoryException)
        {
            if (!_lifetime.IsCancellationRequested) Lost();
        }
    }
    private async Task<T> Operation<T>(string method, object args, CancellationToken ct)
    {
        await _operations.WaitAsync(ct);
        try
        {
            if (Volatile.Read(ref _lost) != 0 || Volatile.Read(ref _disposed) != 0) throw new LocalizedIOException("Error.RemoteDisconnected");
            var parameters = RemoteProtocol.Element(args).EnumerateObject().ToDictionary(property => property.Name, property => (object?)property.Value.Clone());
            parameters["lease"] = lease.Id;
            parameters["deviceId"] = device.Id;
            parameters["sequence"] = ++_sequence;
            parameters["deadline"] = _clock.Now + 500;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
            return await peer.CallAsync<T>(method, parameters, linked.Token);
        }
        finally { _operations.Release(); }
    }
    public Task WriteAsync(byte[] packet, CancellationToken ct) => WriteAsync(BluetoothDeviceProfiles.FromId(device.ProtocolId)!.Write, packet, ct);
    public async Task WriteAsync(GattAddress address, byte[] packet, CancellationToken ct) => await WriteAsync(new GattWrite(address, packet), ct);
    public async Task WriteAsync(GattWrite write, CancellationToken ct) => await Operation<bool>("gatt.write", new { write }, ct);
    public async Task WriteBatchAsync(IReadOnlyList<GattWrite> writes, CancellationToken ct) => await Operation<bool>("gatt.batch", new { writes }, ct);
    public async Task<byte[]?> ReadAsync(GattAddress address, CancellationToken ct)
    {
        var value = await Operation<JsonElement>("gatt.read", new { address }, ct);
        return value.ValueKind == JsonValueKind.Null ? null : RemoteProtocol.Read<byte[]>(value);
    }
    public void Notify(RemoteGattNotification notification)
    {
        if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _lost) != 0 || notification.DeviceId != device.Id || _clock.Now - notification.ClockMilliseconds > 500) return;
        if (notification.Notification.Address == Primary) NotificationReceived?.Invoke(notification.Notification.Data);
        else AdditionalNotificationReceived?.Invoke(notification.Notification);
    }
    public void Lost()
    {
        if (Interlocked.Exchange(ref _lost, 1) == 0)
        {
            _lifetime.Cancel();
            Disconnected?.Invoke();
        }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
        try
        {
            if (Volatile.Read(ref _lost) == 0) await peer.CallAsync<bool>("lease.release", new { lease = lease.Id });
        }
        catch (Exception error) when (error is RemoteRpcException or IOException or OperationCanceledException or WebSocketException) { }
        finally
        {
            removed();
            await _renew;
            _lifetime.Dispose();
        }
    }
}
