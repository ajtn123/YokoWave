namespace YokoWave.Core.Bluetooth;

public enum GattWriteMode { Auto, WithResponse, WithoutResponse }
public sealed record GattAttribute(GattAddress Address, bool Read, bool Write, bool WriteWithoutResponse, bool Notify);
public sealed record GattWrite(GattAddress Address, byte[] Data, GattWriteMode Mode = GattWriteMode.Auto, int DelayAfterMilliseconds = 0);
public sealed record GattDescription(DeviceDescriptor Device, IReadOnlyList<GattAttribute> Attributes, int MaximumWriteLength);
public sealed record GattDeviceNotification(string DeviceId, GattNotification Notification);

public interface IRawGattTransport
{
    event Action<GattDeviceNotification>? GattNotificationReceived;
    Task<GattDescription> OpenGattAsync(string id, Guid lease, CancellationToken cancellationToken = default);
    Task<byte[]?> ReadGattAsync(string id, Guid lease, GattAddress address, CancellationToken cancellationToken = default);
    Task WriteGattAsync(string id, Guid lease, IReadOnlyList<GattWrite> writes, CancellationToken cancellationToken = default);
    Task CloseGattAsync(string id, Guid lease, CancellationToken cancellationToken = default);
}
