using YokoWave.Core;

namespace YokoWave.Tests;

internal sealed class RecordingTransport : IDeviceTransport
{
    public string DisplayName => "test";
    public Dictionary<string, OutputFrame> Latest { get; } = [];
    public List<string> Events { get; } = [];
    public string? FailId { get; set; }
    public Task<IReadOnlyList<DeviceDescriptor>> ScanAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<DeviceDescriptor>>([]);
    public Task ConnectAsync(DeviceDescriptor device, CancellationToken cancellationToken = default)
    {
        Latest.Add(device.Id, OutputFrame.Zero);
        return Task.CompletedTask;
    }
    public Task SendAsync(string deviceId, OutputFrame frame, CancellationToken cancellationToken = default)
    {
        if (deviceId == FailId)
            throw new IOException("Test connection lost");
        Latest[deviceId] = frame;
        Events.Add($"send:{deviceId}:{frame.ChannelA}:{frame.ChannelB}");
        return Task.CompletedTask;
    }
    public Task DisconnectAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        Events.Add($"disconnect:{deviceId}");
        Latest.Remove(deviceId);
        return Task.CompletedTask;
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
