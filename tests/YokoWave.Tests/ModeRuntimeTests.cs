using YokoWave.Core;
using YokoWave.Core.Localization;
using Xunit;

namespace YokoWave.Tests;

public sealed class ModeRuntimeTests
{
    private static WaveformPreset Wave(double frequency = 20) => new(Guid.NewGuid(), "constant", WaveShape.Custom, .2, frequency, [1, 1]);
    private static async Task Connect(DeviceController controller, string id, DeviceKind kind = DeviceKind.DualChannel) =>
        await controller.ConnectAsync(new(id, id, kind, 80, -40), TestContext.Current.CancellationToken);

    [Theory]
    [InlineData(ModeKind.Waveform)]
    [InlineData(ModeKind.Touch)]
    [InlineData(ModeKind.Audio)]
    [InlineData(ModeKind.Restraint)]
    [InlineData(ModeKind.Exercise)]
    [InlineData(ModeKind.SleepDeprivation)]
    [InlineData(ModeKind.Pump)]
    public async Task RestartHasFreshInputStateAndStrengthAndCompletesEachSessionOnce(ModeKind kind)
    {
        var clock = new ManualInputClock(); var transport = new RecordingTransport();
        await using var controller = new DeviceController(transport, clock);
        await Connect(controller, "one", kind == ModeKind.Pump ? DeviceKind.Pump : DeviceKind.DualChannel);
        var engine = new SessionEngine(controller) { ChannelA = 40, ChannelB = 20 };
        var program = new SessionProgram(Kind: kind, Pump: new(PumpDirection.Forward, true),
            Reactive: new(IdleSeconds: 1, IntervalSeconds: 1, BurstSeconds: 1));
        var records = new List<SessionRecord>(); engine.Completed += (_, record) => records.Add(record);
        var wave = Wave();
        await engine.StartAsync(["one"], wave, null, program);
        switch (kind)
        {
            case ModeKind.Audio:
                await engine.SetAudioFrameAsync(new(1, clock.GetUtcNow(), -6, -6, 1, 1)); break;
            case ModeKind.Touch:
                await engine.SetTouchAsync(70, 80, true); break;
            case ModeKind.Restraint:
            case ModeKind.Exercise:
                await controller.SetSimulatedMotionAsync(engine.OwnerId, 0); break;
        }
        await engine.AdvanceAsync(TimeSpan.Zero);
        if (kind == ModeKind.Restraint) await controller.SetSimulatedMotionAsync(engine.OwnerId, 60);
        await engine.AdvanceAsync(TimeSpan.FromSeconds(1.1));
        if (kind == ModeKind.Pump) Assert.Equal(PumpDirection.Forward, transport.Latest["one"].Pump!.Direction);
        else Assert.True(transport.Latest["one"].ChannelA > 0);
        await engine.SetBoostAsync(true);
        await engine.StopAsync(); await engine.StopAsync();
        Assert.Single(records); Assert.Equal(OutputFrame.Zero, transport.Latest["one"]); Assert.Null(controller.DeviceOwner("one"));

        await engine.StartAsync(["one"], wave, null, program with { Pump = new() });
        Assert.Empty(engine.InputStatus); Assert.Empty(engine.Recording); Assert.Equal(0, engine.MovementCount);
        await engine.AdvanceAsync(TimeSpan.FromSeconds(.1));
        if (kind == ModeKind.Waveform) Assert.Equal(40, transport.Latest["one"].ChannelA);
        else if (kind == ModeKind.Pump) Assert.Equal(new PumpOutput(), transport.Latest["one"].Pump);
        else Assert.Equal(0, transport.Latest["one"].ChannelA);
        await engine.StopAsync(); Assert.Equal(2, records.Count);
        Assert.All(records, record => Assert.Equal(kind, record.Kind));
        if (!PlaybackModeDefinition.For(kind).UsesWaveforms)
            Assert.All(records, record => { Assert.Null(record.WaveformId); Assert.Null(record.ChannelBWaveformId); });
    }

    [Fact]
    public async Task StereoEnvelopeIsUpdatedOnceForAllTargetsAndIndependentModesKeepTheirOwnInput()
    {
        var clock = new ManualInputClock(); var transport = new RecordingTransport();
        await using var controller = new DeviceController(transport, clock);
        foreach (var id in new[] { "one", "two", "other" }) await Connect(controller, id);
        var first = new SessionEngine(controller) { ChannelA = 40, ChannelB = 30 };
        var other = new SessionEngine(controller) { ChannelA = 40, ChannelB = 30 };
        await first.StartAsync(["one", "two"], Wave(), null, new(Kind: ModeKind.Audio));
        await other.StartAsync(["other"], Wave(), null, new(Kind: ModeKind.Audio));
        await first.SetAudioFrameAsync(new(1, clock.GetUtcNow(), -30, -40, 1, 1));
        await first.AdvanceAsync(TimeSpan.FromSeconds(.1)); await other.AdvanceAsync(TimeSpan.FromSeconds(.1));
        Assert.Equal(transport.Latest["one"], transport.Latest["two"]);
        Assert.True(transport.Latest["one"].ChannelA > 0); Assert.Equal(0, transport.Latest["other"].ChannelA);
        await first.SetAudioFrameAsync(new(2, clock.GetUtcNow(), -45, -50, 0, 0));
        await first.AdvanceAsync(TimeSpan.FromSeconds(.2));
        Assert.Equal(transport.Latest["one"], transport.Latest["two"]);
        await first.StopAsync();
        await other.SetAudioFrameAsync(new(1, clock.GetUtcNow(), -6, -6, 1, 1));
        await other.AdvanceAsync(TimeSpan.FromSeconds(.2));
        Assert.True(other.IsRunning); Assert.True(transport.Latest["other"].ChannelA > 0);
        Assert.Equal(OutputFrame.Zero, transport.Latest["one"]);
        await other.StopAsync();
    }

    [Theory]
    [InlineData(ModeKind.Waveform)]
    [InlineData(ModeKind.SleepDeprivation)]
    public async Task PlaylistIsASessionSnapshotEvenIfTheCallerEditsItsList(ModeKind kind)
    {
        var transport = new RecordingTransport(); await using var controller = new DeviceController(transport);
        await Connect(controller, "one"); var engine = new SessionEngine(controller) { ChannelA = 30 };
        var waves = new List<WaveformPreset> { Wave(20), Wave(40) }; var second = waves[1];
        await engine.StartAsync(["one"], waves[0], null, new(new(PlaybackOrder.Sequential), waves, Kind: kind,
            Reactive: new(SleepCycle: SleepCycleKind.Random, MinimumIntervalSeconds: 1, MaximumIntervalSeconds: 1,
                MinimumBurstSeconds: 1, MaximumBurstSeconds: 1)));
        waves.Clear();
        if (kind == ModeKind.Waveform)
        { await engine.AdvanceAsync(TimeSpan.FromSeconds(.3)); Assert.Equal(second.Id, engine.CurrentWaveform!.Id); }
        else
        {
            for (var tick = 0; tick <= 12; tick++) await engine.AdvanceAsync(TimeSpan.FromSeconds(tick / 10d));
            Assert.Contains(transport.Latest["one"].ReferenceSample!.FrequencyHz, new[] { 20d, 40d });
            Assert.True(transport.Latest["one"].ChannelA > 0);
        }
        Assert.True(engine.UsesWaveform(second.Id)); await engine.StopAsync();
    }

    [Theory]
    [InlineData(PlaybackOrder.Sequential, false)]
    [InlineData(PlaybackOrder.Sequential, true)]
    [InlineData(PlaybackOrder.Random, false)]
    [InlineData(PlaybackOrder.Random, true)]
    public async Task PlaylistPlaybackRejectsMissingPoolsBeforeAcquiringDevices(PlaybackOrder order, bool empty)
    {
        var transport = new RecordingTransport(); await using var controller = new DeviceController(transport);
        await Connect(controller, "one"); var engine = new SessionEngine(controller);
        var writes = transport.Events.Count;
        await Assert.ThrowsAsync<LocalizedOperationException>(() => engine.StartAsync(["one"], Wave(), null,
            new(new(order), empty ? [] : null)));
        Assert.False(engine.IsRunning); Assert.Null(controller.DeviceOwner("one")); Assert.Equal(writes, transport.Events.Count);
    }
}
