using System.Diagnostics;
using YokoWave.Core;

namespace YokoWave.Remote;

internal sealed class RemoteDeviceLease
{
    private long _renewed = Stopwatch.GetTimestamp();
    public Guid Id { get; } = Guid.NewGuid();
    public RemotePeerSession Peer { get; }
    public RemoteLeaseType Type { get; }
    public string[] PublicIds { get; }
    public long Sequence { get; set; }
    public bool Faulted { get; set; }
    public RemoteGattSession? Gatt { get; }
    public RemoteControlSession Control { get; }
    public double AgeMilliseconds => Stopwatch.GetElapsedTime(Volatile.Read(ref _renewed)).TotalMilliseconds;
    public RemoteDeviceLease(RemotePeerSession peer, RemoteLeaseType type, string[] publicIds, string[] localIds, DeviceController controller, Guid owner, double limit, Func<long> clock, Func<string, RemoteDevice> describe,
        DeviceController? controlDevices = null)
    {
        Peer = peer;
        Type = type;
        PublicIds = publicIds;
        var exports = publicIds.Zip(localIds).ToDictionary(pair => pair.First, pair => pair.Second);
        if (type == RemoteLeaseType.Gatt)
            Gatt = new(controller, Id, exports, describe, notification => Peer.Peer.Notify("gatt.notification", notification), clock);
        Control = new(controlDevices ?? controller, owner, exports, limit,
            status => Peer.Peer.Notify("control.status", new { lease = Id, status }), clock, controller);
    }
    public void Renew() => Volatile.Write(ref _renewed, Stopwatch.GetTimestamp());
}
