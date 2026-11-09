using YokoWave.Core;

namespace YokoWave.App.ViewModels;

internal static class ChannelChoices
{
    public static SessionOption[] Create(DeviceDescriptor? device = null) => Enumerable.Range(0, 3)
        .Where(channel => device is null || !device.IsInput && device.Kind != DeviceKind.Pump && device.HasChannel(channel))
        .Select(channel => new SessionOption(channel, "Channel." + (char)('A' + channel))).ToArray();
}
