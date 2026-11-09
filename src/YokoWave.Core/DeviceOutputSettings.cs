using YokoWave.Core.Bluetooth;
using YokoWave.Core.Localization;

namespace YokoWave.Core;

public sealed record DeviceOutputSettings(string DeviceId, bool Gentle = false, HardwareOutputSettings? Hardware = null, DgBalanceSettings? Balances = null)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(DeviceId) || DeviceId.Length > 512)
            throw new LocalizedArgumentException("Error.InvalidSettings");
        Hardware?.Validate();
    }
    public bool Accepts(DeviceDescriptor device, bool motion = false) => Hardware?.Accepts(device, motion) != false &&
        (Balances is null || device.Profile?.Supports(DeviceCapability.Balance) == true) &&
        (Hardware is not { ChannelALimit: not 100 } and not { ChannelBLimit: not 100 } || device.Profile?.Supports(DeviceCapability.ChannelLimits) == true);
}
