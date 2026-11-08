using System.Buffers.Binary;
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
        Samples: [new(1, frequency, 100), new(1, frequency, 100)], Family: WaveformFamily.Electrical);
    private static async Task<DeviceController> Controller(RecordingTransport transport, ManualInputClock clock, params string[] ids)
    {
        var controller = new DeviceController(transport, clock);
        foreach (var id in ids) await controller.ConnectAsync(new(id, id, DeviceKind.DualChannel, 80, -40), TestContext.Current.CancellationToken);
        return controller;
    }

    [Fact]
    public void MotionPacketsValidateChecksumSignedAccelerationAndZeroVectors()
    {
        byte[] data = [0x35, 0x71, 6, 0xC0, 0, 0, 0, 0x40, 0, 0, 0, 0, 0, 0, 0];
        var packet = V1Protocol.WithChecksum(data);
        Assert.Equal(45, V1Protocol.Parse(packet)!.MotionAngle!.Value, 8);
        packet[^1] ^= 1; Assert.Null(V1Protocol.Parse(packet));
        Assert.Null(V1Protocol.Parse(V1Protocol.WithChecksum([0x35, 0x71, 6, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0])));
        Assert.Equal(1234, V1Protocol.Parse(V1Protocol.WithChecksum([0x35, 0x71, 5, 4, 0xD2]))!.Steps);
        Assert.Equal(new byte[] { 0x35, 0x14, 1, 0x4A }, V1Protocol.MotionStreaming(true));
        Assert.Equal(new byte[] { 0x35, 0x14, 0, 0x49 }, V1Protocol.MotionStreaming(false));
    }

    [Fact]
    public async Task BluetoothMotionRepliesReachTheControllerAndStreamingEndsWithItsOwningMode()
    {
        var adapter = new FakeBluetoothAdapter(); var clock = new ManualInputClock();
        await using var controller = new DeviceController(new ElectricalBluetoothTransport(adapter), clock);
        var device = Assert.Single(await controller.ScanAsync(TestContext.Current.CancellationToken));
        await controller.ConnectAsync(device, TestContext.Current.CancellationToken);
        var engine = new SessionEngine(controller) { ChannelA = 20 };
        await engine.StartAsync([device.Id], Wave(), null, new(Kind: ModeKind.Restraint));
        var link = adapter.Links[device.Id];
        Assert.Contains(link.Writes, packet => packet.SequenceEqual(V1Protocol.MotionStreaming(true)));
        byte[] data = [0x35, 0x71, 6, 0, 0, 0, 0, 0x40, 0, 0, 0, 0, 0, 0, 0];
        link.Notify(V1Protocol.WithChecksum(data));
        await engine.AdvanceAsync(TimeSpan.FromSeconds(.1));
        BinaryPrimitives.WriteInt16BigEndian(data.AsSpan(3, 2), 16384);
        link.Notify(V1Protocol.WithChecksum(data));
        await engine.AdvanceAsync(TimeSpan.FromSeconds(.2));
        Assert.Equal(45, controller.Connections[device.Id].Motion!.Angle, 8);
        Assert.Equal(20, controller.Connections[device.Id].Output.ChannelA);
        await engine.StopAsync();
        Assert.Contains(link.Writes, packet => packet.SequenceEqual(V1Protocol.MotionStreaming(false)));
        Assert.Equal(0, controller.Connections[device.Id].Output.ChannelA); Assert.Null(controller.DeviceOwner(device.Id));
    }

    [Fact]
    public async Task QueuedMotionKeepsItsArrivalTimeAndCannotBecomeFreshAfterABlockedWrite()
    {
        var clock = new ManualInputClock(); var transport = new BlockingMotionTransport();
        await using var controller = new DeviceController(transport, clock);
        await controller.ConnectAsync(new("one", "one", DeviceKind.DualChannel, 80, -40), TestContext.Current.CancellationToken);
        var engine = new SessionEngine(controller) { ChannelA = 20 };
        await engine.StartAsync(["one"], Wave(), null, new(Kind: ModeKind.Restraint));
        transport.Report(0); await engine.AdvanceAsync(TimeSpan.FromSeconds(.1));
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); transport.Barrier = barrier;
        var pending = controller.SendOwnedAsync(engine.OwnerId, "one", OutputFrame.Zero, TestContext.Current.CancellationToken);
        transport.Report(45); clock.Advance(3); barrier.SetResult(); await pending; transport.Barrier = null;
        for (var attempt = 0; attempt < 100 && controller.Connections["one"].Motion?.Angle != 45; attempt++)
            await Task.Delay(1, TestContext.Current.CancellationToken);
        Assert.Equal(45, controller.Connections["one"].Motion!.Angle);
        await engine.AdvanceAsync(TimeSpan.FromSeconds(.2));
        Assert.Equal(0, transport.Inner.Latest["one"].ChannelA);
        Assert.Equal(InputPhase.Waiting, engine.InputStatus["one"].Phase);
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
        { if (Barrier is { } barrier) await barrier.Task.WaitAsync(cancellationToken); await Inner.SendAsync(deviceId, frame, cancellationToken); }
        public Task DisconnectAsync(string deviceId, CancellationToken cancellationToken = default) => Inner.DisconnectAsync(deviceId, cancellationToken);
        public ValueTask DisposeAsync() => Inner.DisposeAsync();
    }

    [Fact]
    public async Task RestraintUsesEachRuntimeBaselineClampsOutputAndFailsClosedOnStaleMotion()
    {
        var clock = new ManualInputClock(); var transport = new RecordingTransport();
        await using var controller = await Controller(transport, clock, "one", "other");
        var other = new SessionEngine(controller) { ChannelA = 10 };
        await other.StartAsync(["other"], Wave(), null); await other.AdvanceAsync(TimeSpan.FromSeconds(.1));
        var engine = new SessionEngine(controller) { ChannelA = 40, ChannelB = 0 };
        await engine.StartAsync(["one"], Wave(), null, new(Kind: ModeKind.Restraint, OutputLimit: 30, Reactive: new(SafeAngle: 5, RampAngle: 20)));
        await engine.AdvanceAsync(TimeSpan.FromSeconds(.1)); Assert.Equal(0, transport.Latest["one"].ChannelA);
        await controller.SetSimulatedMotionAsync(engine.OwnerId, 20); await engine.AdvanceAsync(TimeSpan.FromSeconds(.2));
        Assert.Equal(0, transport.Latest["one"].ChannelA);
        await controller.SetSimulatedMotionAsync(engine.OwnerId, 30); await engine.AdvanceAsync(TimeSpan.FromSeconds(.3));
        Assert.Equal(10, transport.Latest["one"].ChannelA); Assert.Equal(0, transport.Latest["one"].ChannelB);
        await controller.SetSimulatedMotionAsync(engine.OwnerId, 90); await engine.AdvanceAsync(TimeSpan.FromSeconds(.4));
        Assert.Equal(30, transport.Latest["one"].ChannelA);
        clock.Advance(2.1); await engine.AdvanceAsync(TimeSpan.FromSeconds(.5));
        Assert.Equal(0, transport.Latest["one"].ChannelA); Assert.Equal(InputPhase.Waiting, engine.InputStatus["one"].Phase);
        Assert.Equal(10, transport.Latest["other"].ChannelA);
        await controller.SetSimulatedMotionAsync(engine.OwnerId, 40); await engine.AdvanceAsync(TimeSpan.FromSeconds(.6));
        await engine.RecalibrateMotionAsync(); Assert.Equal(0, transport.Latest["one"].ChannelA);
        await engine.StopAsync(); Assert.True(other.IsRunning); Assert.Equal(10, transport.Latest["other"].ChannelA);
    }

    [Fact]
    public async Task ExerciseResetsItsWaitOnMovementAndStopsAtTheMovementGoal()
    {
        var clock = new ManualInputClock(); var transport = new RecordingTransport();
        await using var controller = await Controller(transport, clock, "one");
        var engine = new SessionEngine(controller) { ChannelA = 20 };
        await engine.StartAsync(["one"], Wave(), null, new(Kind: ModeKind.Exercise,
            Reactive: new(IdleSeconds: 1, BurstSeconds: 1, MovementSensitivity: 100)));
        for (var tick = 1; tick <= 8; tick++)
        {
            clock.Advance(.1); await controller.SetSimulatedMotionAsync(engine.OwnerId, tick == 8 ? 30 : 0);
            await engine.AdvanceAsync(TimeSpan.FromMilliseconds(tick * 100));
        }
        Assert.Equal(1, engine.MovementCount); Assert.Equal(0, transport.Latest["one"].ChannelA);
        for (var tick = 9; tick <= 19; tick++)
        {
            clock.Advance(.1); await controller.SetSimulatedMotionAsync(engine.OwnerId, 30);
            await engine.AdvanceAsync(TimeSpan.FromMilliseconds(tick * 100));
        }
        Assert.Equal(20, transport.Latest["one"].ChannelA);
        for (var tick = 20; tick <= 30; tick++)
        {
            clock.Advance(.1); await controller.SetSimulatedMotionAsync(engine.OwnerId, 30);
            await engine.AdvanceAsync(TimeSpan.FromMilliseconds(tick * 100));
        }
        Assert.Equal(0, transport.Latest["one"].ChannelA);
        await engine.StopAsync(); SessionRecord? completed = null; engine.Completed += (_, record) => completed = record;
        await engine.StartAsync(["one"], Wave(), null, new(Kind: ModeKind.Exercise,
            Reactive: new(MovementSensitivity: 100, MovementGoalEnabled: true, MovementGoal: 1)));
        await controller.SetSimulatedMotionAsync(engine.OwnerId, 0); await engine.AdvanceAsync(TimeSpan.Zero);
        clock.Advance(.8); await controller.SetSimulatedMotionAsync(engine.OwnerId, 30); await engine.AdvanceAsync(TimeSpan.FromSeconds(.8));
        Assert.False(engine.IsRunning); Assert.Equal("Reason.MovementGoal", completed!.Reason);
        Assert.Equal(0, transport.Latest["one"].ChannelA);
    }

    [Fact]
    public async Task SleepIntervalsPauseWithTheActiveClockAndNeverReplayAMissedBurst()
    {
        var clock = new ManualInputClock(); var transport = new RecordingTransport();
        await using var controller = await Controller(transport, clock, "one");
        var engine = new SessionEngine(controller) { ChannelA = 20 };
        await engine.StartAsync(["one"], Wave(), null, new(Kind: ModeKind.SleepDeprivation, Reactive: new(IntervalSeconds: 1, BurstSeconds: 1)));
        await engine.AdvanceAsync(TimeSpan.FromSeconds(.1)); Assert.Equal(0, transport.Latest["one"].ChannelA);
        await engine.AdvanceAsync(TimeSpan.FromSeconds(1.1)); Assert.Equal(20, transport.Latest["one"].ChannelA);
        await engine.SetPausedAsync(true); await engine.AdvanceAsync(TimeSpan.FromSeconds(8));
        Assert.Equal(0, transport.Latest["one"].ChannelA); Assert.Equal(TimeSpan.FromSeconds(1.1), engine.Elapsed);
        await engine.SetPausedAsync(false); await engine.AdvanceAsync(TimeSpan.FromSeconds(1.5));
        Assert.Equal(20, transport.Latest["one"].ChannelA);
        await engine.AdvanceAsync(TimeSpan.FromSeconds(2.2)); Assert.Equal(0, transport.Latest["one"].ChannelA);
        await engine.AdvanceAsync(TimeSpan.FromSeconds(20)); Assert.Equal(0, transport.Latest["one"].ChannelA);
        await engine.AdvanceAsync(TimeSpan.FromSeconds(20.5)); Assert.Equal(0, transport.Latest["one"].ChannelA);
    }

    [Fact]
    public async Task RandomSleepUsesOnlyLocalPoolAndScalesWithoutActivatingAZeroChannel()
    {
        var clock = new ManualInputClock(); var transport = new RecordingTransport();
        await using var controller = await Controller(transport, clock, "one");
        var engine = new SessionEngine(controller, new Random(42)) { ChannelA = 40, ChannelB = 0 };
        var pool = new[] { Wave(20), Wave(40) };
        await engine.StartAsync(["one"], Wave(90), null, new(Kind: ModeKind.SleepDeprivation, Playlist: pool,
            Reactive: new(SleepCycle: SleepCycleKind.Random, MinimumIntervalSeconds: 1, MaximumIntervalSeconds: 1,
                MinimumBurstSeconds: 1, MaximumBurstSeconds: 1, MinimumStrengthPercent: 25, MaximumStrengthPercent: 50)));
        for (var tick = 1; tick <= 12; tick++) await engine.AdvanceAsync(TimeSpan.FromMilliseconds(tick * 100));
        var output = transport.Latest["one"];
        Assert.InRange(output.ChannelA, 10, 20); Assert.Equal(0, output.ChannelB);
        Assert.Contains(output.ReferenceSample!.FrequencyCode, new[] { 20d, 40d });
        await engine.StopAsync();
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => engine.StartAsync(["one"], Wave(), null,
            new(Kind: ModeKind.SleepDeprivation, Playlist: [], Reactive: new(SleepCycle: SleepCycleKind.Random))));
        Assert.Null(controller.DeviceOwner("one"));
    }

    [Fact]
    public async Task ActivityResetSleepWaitsForFreshMotionAndResetsBeforeTheBurst()
    {
        var clock = new ManualInputClock(); var transport = new RecordingTransport();
        await using var controller = await Controller(transport, clock, "one");
        var engine = new SessionEngine(controller) { ChannelA = 20 };
        await engine.StartAsync(["one"], Wave(), null, new(Kind: ModeKind.SleepDeprivation,
            Reactive: new(SleepCycle: SleepCycleKind.ActivityReset, IntervalSeconds: 1, BurstSeconds: 1)));
        await engine.AdvanceAsync(TimeSpan.FromSeconds(.1)); Assert.Equal(0, transport.Latest["one"].ChannelA);
        for (var tick = 2; tick <= 10; tick++)
        {
            clock.Advance(.1); await controller.SetSimulatedMotionAsync(engine.OwnerId, tick >= 9 ? 30 : 0);
            await engine.AdvanceAsync(TimeSpan.FromMilliseconds(tick * 100));
        }
        Assert.Equal(1, engine.MovementCount); Assert.Equal(0, transport.Latest["one"].ChannelA);
        for (var tick = 11; tick <= 21; tick++)
        {
            clock.Advance(.1); await controller.SetSimulatedMotionAsync(engine.OwnerId, 30);
            await engine.AdvanceAsync(TimeSpan.FromMilliseconds(tick * 100));
        }
        Assert.Equal(20, transport.Latest["one"].ChannelA);
    }

    [Fact]
    public void PcmAnalysisKeepsStereoLevelsAndSuppressesNonFiniteSamplesAndAmbientNoise()
    {
        var meter = new AudioFeatureAccumulator(16000);
        var samples = Enumerable.Range(0, 800).SelectMany(_ => new[] { .1f, .5f }).ToArray();
        meter.Process(samples, 2);
        Assert.Equal(-20, meter.Latest!.LeftDb, 5); Assert.Equal(-6.0206, meter.Latest.RightDb, 4);
        var envelope = new AudioEnvelope(); var settings = new AudioSettings();
        Assert.Equal(0, envelope.Process(-90, 1, settings));
        Assert.True(envelope.Process(-56, 1, settings) > 0); Assert.True(envelope.Process(-59, 0, settings) > 0);
        Assert.Equal(0, envelope.Process(-61, 0, settings)); Assert.Equal(0, envelope.Process(-59, 0, settings));
        meter.Reset(); meter.Process(Enumerable.Repeat(float.NaN, 1600).ToArray(), 2);
        Assert.Equal(-120, meter.Latest!.LeftDb);
        meter.Reset(); meter.Process(Enumerable.Range(0, 800).SelectMany(_ => new[] { 1f, float.NaN }).ToArray(), 2);
        Assert.Equal(0, meter.Latest!.LeftDb); Assert.Equal(-120, meter.Latest.RightDb);
        Assert.Equal(0, envelope.Process(double.NaN, 0, settings));
        Assert.True(new AudioEnvelope().Process(-20, 0, settings) > new AudioEnvelope().Process(-20, 0, settings with { Follow = AudioFollowMode.Rhythm }));
    }

    [Fact]
    public async Task AudioFollowsStereoFramesZerosStaleInputAndKeepsModeLimits()
    {
        var clock = new ManualInputClock(); var transport = new RecordingTransport();
        await using var controller = await Controller(transport, clock, "one");
        var engine = new SessionEngine(controller) { ChannelA = 100, ChannelB = 0 };
        await engine.StartAsync(["one"], Wave(), null, new(Kind: ModeKind.Audio, OutputLimit: 20));
        await engine.AdvanceAsync(TimeSpan.FromSeconds(.1)); Assert.Equal(0, transport.Latest["one"].ChannelA);
        await engine.SetAudioFrameAsync(new(1, clock.GetUtcNow(), -6, -120, 1, 0));
        await engine.AdvanceAsync(TimeSpan.FromSeconds(.2));
        Assert.Equal(20, transport.Latest["one"].ChannelA); Assert.Equal(0, transport.Latest["one"].ChannelB);
        await engine.SetPausedAsync(true); await engine.SetPausedAsync(false); await engine.AdvanceAsync(TimeSpan.FromSeconds(.3));
        Assert.Equal(0, transport.Latest["one"].ChannelA);
        await engine.SetAudioFrameAsync(new(2, clock.GetUtcNow(), -6, -6, 1, 1)); await engine.AdvanceAsync(TimeSpan.FromSeconds(.4));
        clock.Advance(2.1); await engine.AdvanceAsync(TimeSpan.FromSeconds(.5)); Assert.Equal(0, transport.Latest["one"].ChannelA);
        await engine.StopAsync(); Assert.Null(controller.DeviceOwner("one"));
    }

    [Fact]
    public async Task AllModeKindsAndReactiveSettingsPersistWithoutDeviceConfiguration()
    {
        using var folder = new TestFolder(); var store = new WorkspaceStore(folder.Path);
        var modes = Enum.GetValues<ModeKind>().Select(kind => new PlaybackModeConfig(Guid.NewGuid(), kind.ToString(), kind,
            Audio: new(AudioSourceKind.MediaFile, "local.wav", Sensitivity: 72), Reactive: new(SafeAngle: 12, SleepCycle: SleepCycleKind.ActivityReset))).ToList();
        await store.SaveAsync(new(5, [], [], new(), Modes: modes));
        Assert.Equivalent(modes, (await new WorkspaceStore(folder.Path).LoadAsync()).Modes);
        Assert.DoesNotContain('\n', await File.ReadAllTextAsync(store.ModesPath, TestContext.Current.CancellationToken));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.SaveAsync(new(5, [], [], new(), Modes:
            [new(Guid.NewGuid(), "bad", Reactive: new(MinimumIntervalSeconds: 20, MaximumIntervalSeconds: 10))])));
    }
}
