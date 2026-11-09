using YokoWave.Core;
using YokoWave.Core.Bluetooth;
using Xunit;

namespace YokoWave.Tests;

internal sealed class ManualInputClock : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(double seconds) => _now += TimeSpan.FromSeconds(seconds);
}

public sealed class ReactiveTests
{
    private static WaveformPreset Wave(double frequency = 20) => new(Guid.NewGuid(), "local", WaveShape.Sampled, 4, frequency,
        Samples: [new(1, frequency, 1), new(1, frequency, 1)]);
    private static async Task<DeviceController> Controller(RecordingTransport transport, ManualInputClock clock, params string[] ids)
    {
        var controller = new DeviceController(transport, clock);
        foreach (var id in ids)
            await controller.ConnectAsync(new(id, id, DeviceKind.DualChannel, 80, -40), TestContext.Current.CancellationToken);
        return controller;
    }

    [Fact]
    public void MotionPacketsValidateChecksumSignedAccelerationAndZeroVectors()
    {
        byte[] data = [0x35, 0x71, 6, 0xC0, 0, 0, 0, 0x40, 0, 0, 0, 0, 0, 0, 0];
        var packet = V1Protocol.WithChecksum(data);
        Assert.Equal(45, V1Protocol.Parse(packet)!.MotionAngle!.Value, 8);
        packet[^1] ^= 1;
        Assert.Null(V1Protocol.Parse(packet));
        var zero = V1Protocol.Parse(V1Protocol.WithChecksum([0x35, 0x71, 6, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]));
        Assert.Null(zero!.MotionAngle);
        Assert.Equal(new SensorAxes(0, 0, 0), zero.AccelerationAxes);
        Assert.Equal(1234, V1Protocol.Parse(V1Protocol.WithChecksum([0x35, 0x71, 5, 4, 0xD2]))!.Steps);
        Assert.Equal(new byte[] { 0x35, 0x14, 1, 0x4A }, V1Protocol.MotionStreaming(true));
        Assert.Equal(new byte[] { 0x35, 0x14, 0, 0x49 }, V1Protocol.MotionStreaming(false));
    }

    private sealed class BlockingMotionTransport : IDeviceTransport, IDeviceStatusSource
    {
        public RecordingTransport Inner { get; } = new();
        public TaskCompletionSource? Barrier { get; set; }
        public string DisplayName => "test";
        public event EventHandler<DeviceStatusUpdate>? StatusChanged;
        public event EventHandler<DeviceTransportFault>? Faulted { add { } remove { } }
        public void Report(double angle) => StatusChanged?.Invoke(this, new("one", motionAngle: angle));
        public Task<IReadOnlyList<DeviceDescriptor>> ScanAsync(CancellationToken cancellationToken = default) => Inner.ScanAsync(cancellationToken);
        public Task ConnectAsync(DeviceDescriptor device, CancellationToken cancellationToken = default) => Inner.ConnectAsync(device, cancellationToken);
        public async Task SendAsync(string deviceId, OutputFrame frame, CancellationToken cancellationToken = default)
        {
            if (Barrier is { } barrier)
                await barrier.Task.WaitAsync(cancellationToken);
            await Inner.SendAsync(deviceId, frame, cancellationToken);
        }
        public Task DisconnectAsync(string deviceId, CancellationToken cancellationToken = default) => Inner.DisconnectAsync(deviceId, cancellationToken);
        public ValueTask DisposeAsync() => Inner.DisposeAsync();
    }

    [Fact]
    public void PcmAnalysisKeepsStereoLevelsAndSuppressesNonFiniteSamplesAndAmbientNoise()
    {
        var meter = new AudioFeatureAccumulator(16000);
        var samples = Enumerable.Range(0, 800).SelectMany(_ => new[] { .1f, .5f }).ToArray();
        meter.Process(samples, 2);
        Assert.Equal(-20, meter.Latest!.LeftDb, 5);
        Assert.Equal(-6.0206, meter.Latest.RightDb, 4);
        var envelope = new AudioEnvelope();
        var settings = new AudioSettings();
        Assert.Equal(0, envelope.Process(-90, 1, settings));
        Assert.True(envelope.Process(-56, 1, settings) > 0);
        Assert.True(envelope.Process(-59, 0, settings) > 0);
        Assert.Equal(0, envelope.Process(-61, 0, settings));
        Assert.Equal(0, envelope.Process(-59, 0, settings));
        meter.Reset();
        meter.Process(Enumerable.Repeat(float.NaN, 1600).ToArray(), 2);
        Assert.Equal(-120, meter.Latest!.LeftDb);
        meter.Reset();
        meter.Process(Enumerable.Range(0, 800).SelectMany(_ => new[] { 1f, float.NaN }).ToArray(), 2);
        Assert.Equal(0, meter.Latest!.LeftDb);
        Assert.Equal(-120, meter.Latest.RightDb);
        Assert.Equal(0, envelope.Process(double.NaN, 0, settings));
        Assert.True(new AudioEnvelope().Process(-20, 0, settings) > new AudioEnvelope().Process(-20, 0, settings with
        {
            Follow = AudioFollowMode.Rhythm
        }));
    }

}
