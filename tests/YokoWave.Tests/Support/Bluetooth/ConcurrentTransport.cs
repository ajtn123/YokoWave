using System.Collections.Concurrent;
using YokoWave.Core;

namespace YokoWave.Tests.Support.Bluetooth;

internal sealed class ConcurrentTransport : IDeviceTransport, IDeviceStatusSource, IElectricalDeviceFeatures, IInputDeviceFeatures, IElectricalMotionSource
{
    private readonly IOException _motionError = new("motion startup");
    public string DisplayName => "test";
    public ConcurrentDictionary<string, OutputFrame> Outputs { get; } = new();
    public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource FastOutput { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public string? BlockedId { get; set; }
    public bool BlockAllWrites { get; set; }
    public int FeatureCalls { get; private set; }
    public bool FailMotionStart { get; set; }
    public bool FailZeroAfterMotion { get; set; }

    public event EventHandler<DeviceStatusUpdate>? StatusChanged;
    public event EventHandler<DeviceTransportFault>? Faulted;
    public void Report(DeviceStatusUpdate update) => StatusChanged?.Invoke(this, update);
    public void ReportFault(DeviceTransportFault fault) => Faulted?.Invoke(this, fault);
    public Task<IReadOnlyList<DeviceDescriptor>> ScanAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<DeviceDescriptor>>([new("slow", "slow", DeviceKind.DualChannel, 80, -40), new("fast", "fast", DeviceKind.DualChannel, 80, -40)]);
    public Task ConnectAsync(DeviceDescriptor device, CancellationToken cancellationToken = default)
    {
        Outputs[device.Id] = OutputFrame.Zero;
        return Task.CompletedTask;
    }

    public async Task SendAsync(string deviceId, OutputFrame frame, CancellationToken cancellationToken = default)
    {
        if (FailZeroAfterMotion && frame == OutputFrame.Zero)
            throw new IOException("rollback stop");
        if (BlockAllWrites || deviceId == BlockedId && frame.ChannelA > 0)
        {
            Blocked.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
        }

        Outputs[deviceId] = frame;
        if (deviceId == "fast" && frame.ChannelA > 0)
            FastOutput.TrySetResult();
    }

    public Task DisconnectAsync(string deviceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public Task SetMotionStreamingAsync(string deviceId, bool enabled, CancellationToken cancellationToken = default)
    {
        if (enabled && FailMotionStart)
        {
            FailZeroAfterMotion = true;
            throw _motionError;
        }

        return Task.CompletedTask;
    }

    public Task<ElectricalFeatureState> RefreshFeaturesAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        FeatureCalls++;
        return Task.FromResult(new ElectricalFeatureState(1, 1, true));
    }

    public Task<bool> SetLightAsync(string deviceId, bool enabled, CancellationToken cancellationToken = default)
    {
        FeatureCalls++;
        return Task.FromResult(enabled);
    }

    public Task ResetInputAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        FeatureCalls++;
        return Task.CompletedTask;
    }
}
