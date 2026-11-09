using System.Text.Json;
using YokoWave.Core.Bluetooth;

namespace YokoWave.Remote;

internal sealed class WebSocketGattConnection(WebSocketRpcPeer peer, RemoteDevice device, RemoteLease lease, long serverClock, long clockAt, Action removed) : IBluetoothConnection
{
    private readonly RemoteDeviceLink _link = new(peer, device, lease, serverClock, clockAt);
    private RemoteGattDescription? _description;
    public int MaximumWriteLength => _description?.MaximumWriteLength ?? 20;
    public IReadOnlyList<GattAttribute> Attributes => _description?.Attributes ?? [];
    public event Action<byte[]>? NotificationReceived;
    public event Action<GattNotification>? AdditionalNotificationReceived;
    public event Action? Disconnected { add => _link.Disconnected += value; remove => _link.Disconnected -= value; }
    private GattAddress Primary => BluetoothDeviceProfiles.FromId(device.ProtocolId)!.Notify;
    public async Task Initialize(CancellationToken ct)
    {
        _description = await Operation<RemoteGattDescription>("gatt.describe", new
        {
        }, ct);
        foreach (var attribute in Attributes.Where(attribute => attribute.Notify))
            await Operation<bool>("gatt.subscribe", new
            {
                address = attribute.Address
            }, ct);
        _link.Start();
    }
    private Task<T> Operation<T>(string method, object args, CancellationToken ct) => _link.Call<T>(method, args, ct);
    public Task WriteAsync(byte[] packet, CancellationToken ct) => WriteAsync(BluetoothDeviceProfiles.FromId(device.ProtocolId)!.Write, packet, ct);
    public async Task WriteAsync(GattAddress address, byte[] packet, CancellationToken ct) => await WriteAsync(new GattWrite(address, packet), ct);
    public async Task WriteAsync(GattWrite write, CancellationToken ct) => await Operation<bool>("gatt.write", new { write }, ct);
    public async Task WriteBatchAsync(IReadOnlyList<GattWrite> writes, CancellationToken ct) => await Operation<bool>("gatt.batch", new { writes }, ct);
    public async Task<byte[]?> ReadAsync(GattAddress address, CancellationToken ct)
    {
        var value = await Operation<JsonElement>("gatt.read", new
        {
            address
        }, ct);
        return value.ValueKind == JsonValueKind.Null ? null : RemoteProtocol.Read<byte[]>(value);
    }
    public void Notify(RemoteGattNotification notification)
    {
        if (notification.DeviceId != device.Id || !_link.IsFresh(notification.ClockMilliseconds))
            return;
        if (notification.Notification.Address == Primary)
            NotificationReceived?.Invoke(notification.Notification.Data);
        else
            AdditionalNotificationReceived?.Invoke(notification.Notification);
    }
    public void Lost() => _link.Lost();
    public async ValueTask DisposeAsync()
    {
        try
        {
            await _link.DisposeAsync();
        }
        finally { removed(); }
    }
}
