using System.Text.Json;
using YokoWave.Core.Bluetooth;

namespace YokoWave.Sdk;

internal sealed class WebSocketGattConnection(WebSocketRpcPeer peer, RemoteDevice device, RemoteLease lease, long serverClock, long clockAt, Action removed) : IBluetoothConnection
{
    private readonly RemoteLeaseLink _link = new(peer, lease, serverClock, clockAt, device.Id);
    private RemoteGattDescription? _description;
    public int MaximumWriteLength => _description?.MaximumWriteLength ?? 20;
    public IReadOnlyList<GattAttribute> Attributes => _description?.Attributes ?? [];
    private GattAddress Primary => BluetoothDeviceProfiles.FromId(device.ProtocolId)!.Notify;
    public event Action<byte[]>? NotificationReceived;
    public event Action<GattNotification>? AdditionalNotificationReceived;
    public event Action? Disconnected { add => _link.Disconnected += value; remove => _link.Disconnected -= value; }
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        _description = await OperationAsync<RemoteGattDescription>("gatt.describe", new
        {
        }, cancellationToken).ConfigureAwait(false);
        foreach (var attribute in Attributes.Where(attribute => attribute.Notify))
        {
            await OperationAsync<bool>("gatt.subscribe", new
            {
                address = attribute.Address
            }, cancellationToken).ConfigureAwait(false);
        }

        _link.Start();
    }
    public Task WriteAsync(byte[] packet, CancellationToken cancellationToken) => WriteAsync(BluetoothDeviceProfiles.FromId(device.ProtocolId)!.Write, packet, cancellationToken);
    public Task WriteAsync(GattAddress address, byte[] packet, CancellationToken cancellationToken) => WriteAsync(new GattWrite(address, packet), cancellationToken);
    public Task WriteAsync(GattWrite write, CancellationToken cancellationToken) => OperationAsync<bool>("gatt.write", new { write }, cancellationToken);
    public Task WriteBatchAsync(IReadOnlyList<GattWrite> writes, CancellationToken cancellationToken) => OperationAsync<bool>("gatt.batch", new { writes }, cancellationToken);
    public async Task<byte[]?> ReadAsync(GattAddress address, CancellationToken cancellationToken)
    {
        var value = await OperationAsync<JsonElement>("gatt.read", new
        {
            address
        }, cancellationToken).ConfigureAwait(false);
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
            await _link.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            removed();
        }
    }
    private Task<T> OperationAsync<T>(string method, object args, CancellationToken cancellationToken) => _link.CallAsync<T>(method, args, cancellationToken);
}
