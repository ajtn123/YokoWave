using System.Numerics;
using YokoWave.Core.Localization;

namespace YokoWave.Core;

public sealed record DeviceDescriptor(string Id, string Name, DeviceKind Kind, int? Battery, int? Signal,
    string? ProtocolId = null, int? ChannelMask = null, DeviceOrigin Origin = DeviceOrigin.Local, bool? Simulated = null,
    Bluetooth.DeviceCapability? AccessCapabilities = null)
{
    public Bluetooth.BluetoothDeviceProfile? Profile => Bluetooth.BluetoothDeviceProfiles.FromId(ProtocolId);
    public bool IsInput => Kind == DeviceKind.Sensor;
    public bool IsSupported => Profile?.Supported ?? AccessCapabilities != Bluetooth.DeviceCapability.None;
    public Bluetooth.DeviceCapability Capabilities => AccessCapabilities ?? Profile?.Capabilities ?? Bluetooth.DeviceCapability.None;
    public bool Supports(Bluetooth.DeviceCapability capability) => (Capabilities & capability) == capability;
    public int ChannelCount => ChannelMask is { } mask ? BitOperations.PopCount((uint)(mask & 7))
        : Profile?.Channels ?? (Kind == DeviceKind.DualChannel ? 2 : IsInput ? 0 : 1);
    public string KindLabel => Origin == DeviceOrigin.Remote ? L10n.Format("Remote.DeviceKind", LocalKindLabel) : LocalKindLabel;
    private string LocalKindLabel => Profile is { } profile ? L10n.Get(profile.NameKey) : Kind switch
    {
        DeviceKind.DualChannel => L10n.Get("Device.DualChannel"),
        DeviceKind.Vibration => L10n.Get("Device.Vibration"),
        DeviceKind.Pump => L10n.Get("Device.Pump"),
        DeviceKind.Sensor => L10n.Get("Device.Sensor"),
        _ => L10n.Get("Device.Linear")
    };
    public bool HasChannel(int index) => index is >= 0 and < 3 && (ChannelMask is { } mask ? (mask & (1 << index)) != 0 : index < ChannelCount);
}
