using System.Collections.Concurrent;
using YokoWave.Core;

namespace YokoWave.Tests.Support;

internal sealed class RecordingTransport : IDeviceTransport
{
    public string DisplayName => "test";
    public ConcurrentDictionary<string, OutputFrame> Latest { get; } = new();
    public ConcurrentQueue<string> Events { get; } = new();
    public string? FailId { get; set; }

    public Task<IReadOnlyList<DeviceDescriptor>> ScanAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<DeviceDescriptor>>([]);
    public Task ConnectAsync(DeviceDescriptor device, CancellationToken cancellationToken = default)
    {
        Assert.True(Latest.TryAdd(device.Id, OutputFrame.Zero));
        return Task.CompletedTask;
    }

    public Task SendAsync(string deviceId, OutputFrame frame, CancellationToken cancellationToken = default)
    {
        if (deviceId == FailId)
            throw new IOException("Test connection lost");
        Latest[deviceId] = frame;
        Events.Enqueue($"send:{deviceId}:{frame.ChannelA}:{frame.ChannelB}");
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        Events.Enqueue($"disconnect:{deviceId}");
        Latest.TryRemove(deviceId, out _);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
