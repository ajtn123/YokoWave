using System.Collections.Concurrent;
using System.Diagnostics;
using YokoWave.Core;
using YokoWave.Core.Bluetooth;

namespace YokoWave.Remote;

internal sealed class RemoteDeviceLease
{
    private long _renewed = Stopwatch.GetTimestamp();
    public Guid Id { get; } = Guid.NewGuid();
    public RemotePeerSession Peer { get; }
    public RemoteLeaseType Type { get; }
    public string[] PublicIds { get; }
    public string[] LocalIds { get; }
    public long Sequence { get; set; }
    public bool Faulted { get; set; }
    public ConcurrentDictionary<(string, GattAddress), byte> Subscriptions { get; } = new();
    public Dictionary<string, GattDescription> Descriptions { get; } = [];
    public RemoteControlSession Control { get; }
    public double AgeMilliseconds => Stopwatch.GetElapsedTime(Volatile.Read(ref _renewed)).TotalMilliseconds;
    public void Renew() => Volatile.Write(ref _renewed, Stopwatch.GetTimestamp());
    public RemoteDeviceLease(RemotePeerSession peer, RemoteLeaseType type, string[] publicIds, string[] localIds, DeviceController controller, Guid owner, double limit, Func<long> clock,
        DeviceController? controlDevices = null)
    {
        Peer = peer;
        Type = type;
        PublicIds = publicIds;
        LocalIds = localIds;
        Control = new(controlDevices ?? controller, owner, publicIds.Zip(localIds).ToDictionary(pair => pair.First, pair => pair.Second), limit,
            status => Peer.Peer.Notify("control.status", new { lease = Id, status }), clock, controller);
    }
}
