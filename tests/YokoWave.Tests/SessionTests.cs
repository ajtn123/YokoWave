using YokoWave.Core;
using YokoWave.Core.Bluetooth;
using Xunit;

namespace YokoWave.Tests;

public sealed class SessionTests
{
    private static WaveformPreset Wave(string name = "wave") => new(Guid.NewGuid(), name, WaveShape.Sine, 4, 20);

    [Fact]
    public async Task DisabledTimerDoesNotStopAtThePreviousTwoHourLimit()
    {
        var transport = new RecordingTransport();
        await using var controller = new DeviceController(transport);
        await controller.ConnectAsync(new("one", "one", DeviceKind.DualChannel, 90, -40), ct: TestContext.Current.CancellationToken);
        var engine = new SessionEngine(controller) { ChannelA = 20 };
        SessionRecord? completed = null;
        engine.Completed += (_, record) => completed = record;
        await engine.StartAsync(["one"], Wave(), null);
        await engine.AdvanceAsync(TimeSpan.FromHours(10) + TimeSpan.FromSeconds(1));
        Assert.True(engine.IsRunning);
        Assert.Null(engine.Remaining);
        Assert.Null(completed);
        Assert.Equal(10, WaveformRendering.Strength(transport.Latest["one"], 0), 8);
        await engine.StopAsync();
        Assert.Equal(36001, completed!.DurationSeconds);
        Assert.Equal(0, transport.Latest["one"].ChannelA);
    }

    [Fact]
    public async Task TimerStopsExactlyAtTheSecondBoundary()
    {
        var transport = new RecordingTransport();
        await using var controller = new DeviceController(transport);
        await controller.ConnectAsync(new("one", "one", DeviceKind.DualChannel, 90, -40), ct: TestContext.Current.CancellationToken);
        var engine = new SessionEngine(controller) { ChannelA = 20 };
        SessionRecord? completed = null;
        engine.Completed += (_, record) => completed = record;
        await engine.StartAsync(["one"], Wave(), TimeSpan.FromSeconds(3));
        await engine.AdvanceAsync(TimeSpan.FromMilliseconds(2999));
        Assert.True(engine.IsRunning);
        Assert.Equal(TimeSpan.FromMilliseconds(1), engine.Remaining);
        await engine.AdvanceAsync(TimeSpan.FromSeconds(3));
        Assert.False(engine.IsRunning);
        Assert.Equal(TimeSpan.Zero, engine.Remaining);
        Assert.Equal("Reason.Timer", completed!.Reason);
        Assert.Equal(0, transport.Latest["one"].ChannelA);
    }

    [Fact]
    public async Task LiveWaveChangeKeepsTargetsLimitsAndDeadlineAndRestartsOnlyWavePhase()
    {
        var transport = new RecordingTransport();
        await using var controller = new DeviceController(transport);
        await controller.ConnectAsync(new("one", "one", DeviceKind.DualChannel, 90, -40), TestContext.Current.CancellationToken);
        await controller.ConnectAsync(new("two", "two", DeviceKind.DualChannel, 90, -40), TestContext.Current.CancellationToken);
        var engine = new SessionEngine(controller) { ChannelA = 20, OffsetChannelB = false };
        var completed = 0;
        engine.Completed += (_, _) => completed++;
        await engine.StartAsync(["one", "two"], Wave(), TimeSpan.FromSeconds(5));
        await engine.AdvanceAsync(TimeSpan.FromSeconds(1.25));
        var writes = transport.Events.Count;
        var next = new WaveformPreset(Guid.NewGuid(), "next", WaveShape.Custom, 2, 40, [0.25, 0.75]);
        Assert.True(await engine.ChangeWaveformAsync(next));
        Assert.Equal(writes, transport.Events.Count);
        Assert.Equal(TimeSpan.FromSeconds(1.25), engine.Elapsed);
        Assert.Equal(TimeSpan.FromSeconds(3.75), engine.Remaining);
        Assert.Equal(0, completed);
        await engine.AdvanceAsync(TimeSpan.FromSeconds(1.5));
        Assert.Equal(TimeSpan.Zero, engine.WaveformElapsed);
        Assert.Equal(5, WaveformRendering.Strength(transport.Latest["one"], 0));
        Assert.Equal(40, transport.Latest["two"].Frequency);
        await engine.AdvanceAsync(TimeSpan.FromSeconds(2.5));
        Assert.Equal(10, WaveformRendering.Strength(transport.Latest["one"], 0));
        Assert.Equal(10, WaveformRendering.Strength(transport.Latest["two"], 0));
        await engine.AdvanceAsync(TimeSpan.FromSeconds(5));
        Assert.False(engine.IsRunning);
        Assert.Equal(1, completed);
    }

    [Fact]
    public async Task WaveChangeCannotRestartOutputAfterConcurrentStop()
    {
        var transport = new RecordingTransport();
        await using var controller = new DeviceController(transport);
        await controller.ConnectAsync(new("one", "one", DeviceKind.DualChannel, 90, -40), ct: TestContext.Current.CancellationToken);
        var engine = new SessionEngine(controller) { ChannelA = 20 };
        await engine.StartAsync(["one"], Wave(), null);
        await Task.WhenAll(engine.AdvanceAsync(TimeSpan.FromSeconds(1)), engine.ChangeWaveformAsync(Wave("next")), engine.StopAsync());
        await engine.AdvanceAsync(TimeSpan.FromSeconds(2));
        Assert.False(engine.IsRunning);
        Assert.Equal(0, transport.Latest["one"].ChannelA);
    }

    [Fact]
    public async Task IncompatibleLiveWaveKeepsTheCurrentElectricalSessionRunning()
    {
        var adapter = new FakeBluetoothAdapter();
        await using var controller = new DeviceController(new BluetoothDeviceTransport(adapter));
        var device = Assert.Single(await controller.ScanAsync(TestContext.Current.CancellationToken));
        await controller.ConnectAsync(device, ct: TestContext.Current.CancellationToken);
        var engine = new SessionEngine(controller) { ChannelA = 10 };
        var current = DefaultWaveformCatalog.Presets[0];
        await engine.StartAsync([device.Id], current, null);
        await Assert.ThrowsAnyAsync<ArgumentException>(() => engine.ChangeWaveformAsync(
            current with { FormatVersion = 999 }));
        Assert.True(engine.IsRunning);
        Assert.Equal(current.Id, engine.CurrentWaveform!.Id);
        await engine.AdvanceAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(10, controller.Connections[device.Id].Output.ChannelA);
        await engine.StopAsync();
    }
}
