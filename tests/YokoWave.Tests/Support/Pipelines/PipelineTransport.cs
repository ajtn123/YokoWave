using System.Collections.Concurrent;
using YokoWave.Core;
using static YokoWave.Tests.Support.Pipelines.PipelineTestGraphs;

namespace YokoWave.Tests.Support.Pipelines;

internal sealed class PipelineTransport : IDeviceTransport
{
    private OutputFrame _last = OutputFrame.Zero;
    public ConcurrentQueue<OutputFrame> Frames { get; } = new();
    public OutputFrame Last => _last;
    public bool FailZero { get; set; }
    public bool FailNonzero { get; set; }
    public bool BlockNonzero { get; set; }
    public TaskCompletionSource WriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleaseWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource FastWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public string DisplayName => "test";

    public Task<IReadOnlyList<DeviceDescriptor>> ScanAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<DeviceDescriptor>>([Device]);
    public Task ConnectAsync(DeviceDescriptor device, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task DisconnectAsync(string deviceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public async Task SendAsync(string deviceId, OutputFrame frame, CancellationToken cancellationToken = default)
    {
        if (FailZero && frame.ChannelA == 0)
            throw new IOException("zero failed");
        if (FailNonzero && frame.ChannelA > 0)
            throw new IOException("write failed");
        if (frame.ChannelA > 0 && deviceId == "one" && BlockNonzero)
        {
            WriteStarted.TrySetResult();
            await ReleaseWrite.Task.WaitAsync(cancellationToken);
        }

        _last = frame;
        Frames.Enqueue(frame);
        if (frame.ChannelA > 0 && deviceId == "fast")
            FastWrite.TrySetResult();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
