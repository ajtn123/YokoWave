using System.Collections.Concurrent;
using System.Text.Json;
using YokoWave.Core;
using YokoWave.Core.Bluetooth;
using YokoWave.Core.Localization;

namespace YokoWave.Remote;

/// <summary>One lease's GATT requests, open descriptions, and notification subscriptions.</summary>
internal sealed class RemoteGattSession(DeviceController devices, Guid lease, IReadOnlyDictionary<string, string> exports,
    Func<string, RemoteDevice> describe, Action<RemoteGattNotification> notify, Func<long> clock)
{
    private readonly Dictionary<string, GattDescription> _descriptions = [];
    private readonly ConcurrentDictionary<(string Device, GattAddress Address), byte> _subscriptions = new();
    private readonly IReadOnlyDictionary<string, string> _publicIds = exports.ToDictionary(pair => pair.Value, pair => pair.Key);

    public async Task OpenAsync(CancellationToken ct)
    {
        foreach (var id in exports.Values)
            _descriptions[id] = await devices.RawGatt.OpenGattAsync(id, lease, ct);
    }

    public async Task<object?> ExecuteAsync(string method, JsonElement parameters, CancellationToken ct)
    {
        var publicId = RemoteProtocol.Parameter<string>(parameters, "deviceId");
        if (!exports.TryGetValue(publicId, out var local))
            throw new RemoteRpcException(-32003, "Error.RemoteLease");
        switch (method)
        {
            case "gatt.describe":
                var description = _descriptions[local];
                return new RemoteGattDescription(describe(publicId), description.Attributes.ToArray(), description.MaximumWriteLength);
            case "gatt.read":
                return await devices.RawGatt.ReadGattAsync(local, lease, RemoteProtocol.Parameter<GattAddress>(parameters, "address"), ct);
            case "gatt.write":
                await devices.RawGatt.WriteGattAsync(local, lease, [RemoteProtocol.Parameter<GattWrite>(parameters, "write")], ct);
                return true;
            case "gatt.batch":
                await devices.RawGatt.WriteGattAsync(local, lease, RemoteProtocol.Parameter<GattWrite[]>(parameters, "writes"), ct);
                return true;
            case "gatt.subscribe":
            case "gatt.unsubscribe":
                SetSubscription(local, RemoteProtocol.Parameter<GattAddress>(parameters, "address"), method == "gatt.subscribe");
                return true;
            default:
                throw new RemoteRpcException(-32601, "Error.RemoteMethod");
        }
    }

    private void SetSubscription(string local, GattAddress address, bool subscribe)
    {
        if (!_descriptions[local].Attributes.Any(attribute => attribute.Notify && attribute.Address == address))
            throw new RemoteRpcException(-32602, "Error.DeviceFeatures");
        if (subscribe)
            _subscriptions.TryAdd((local, address), 0);
        else
            _subscriptions.TryRemove((local, address), out _);
    }

    public void OnNotification(GattDeviceNotification notification)
    {
        if (_subscriptions.ContainsKey((notification.DeviceId, notification.Notification.Address)))
            notify(new(lease, _publicIds[notification.DeviceId], notification.Notification, clock()));
    }

    public async Task CloseAsync(CancellationToken ct)
    {
        List<Exception> errors = [];
        foreach (var local in _descriptions.Keys)
        {
            if (!devices.Connections.ContainsKey(local))
                continue;
            try
            {
                await devices.RawGatt.CloseGattAsync(local, lease, ct);
            }
            catch (Exception error)
            {
                errors.Add(error);
            }
        }
        if (errors.Count > 0)
            throw new LocalizedAggregateException("Error.StopUnconfirmed", errors);
    }
}
