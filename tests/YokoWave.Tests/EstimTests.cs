using System.Buffers.Binary;
using YokoWave.Core;
using YokoWave.Core.Bluetooth;
using Xunit;

namespace YokoWave.Tests;

public sealed class EstimTests
{
    private static WaveformPreset Wave(string name, double period = 1, double frequency = 20, double pulse = 30) =>
        new(Guid.NewGuid(), name, WaveShape.Sampled, period, frequency,
            Samples: [new(0.3, frequency, pulse), new(0.7, frequency + 1, pulse + 1)], Family: WaveformFamily.Electrical);
    private static async Task<DeviceController> Controller(RecordingTransport transport, double limit = 60)
    {
        var controller = new DeviceController(transport);
        await controller.ConnectAsync(new("one", "one", DeviceKind.DualChannel, 80, -40), TestContext.Current.CancellationToken);
        return controller;
    }

    [Fact]
    public async Task IndependentChannelsUseTheirOwnWavesAndLiveChangesDoNotResetTheOtherChannel()
    {
        var transport = new RecordingTransport(); await using var controller = await Controller(transport);
        var engine = new SessionEngine(controller) { ChannelA = 20, ChannelB = 10 };
        var a = Wave("A", 4, 20, 30); var b = Wave("B", 2, 40, 60);
        await engine.StartAsync(["one"], a, TimeSpan.FromSeconds(10), new(ChannelBWaveform: b));
        await engine.AdvanceAsync(TimeSpan.FromSeconds(1.25));
        Assert.Equal(a.Samples![0], transport.Latest["one"].ReferenceSample);
        Assert.Equal(b.Samples![1], transport.Latest["one"].ReferenceSampleB);
        var changedB = Wave("B2", 2, 60, 70);
        await engine.ChangeChannelBWaveformAsync(changedB);
        await engine.AdvanceAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(a.Samples[1], transport.Latest["one"].ReferenceSample);
        Assert.Equal(changedB.Samples![0], transport.Latest["one"].ReferenceSampleB);
        Assert.Equal(TimeSpan.FromSeconds(8), engine.Remaining);
    }

    [Fact]
    public async Task SequentialAndRandomPlaybackAdvanceLocallyWithoutEndingTheSession()
    {
        var transport = new RecordingTransport(); await using var controller = await Controller(transport);
        var engine = new SessionEngine(controller, new Random(42)) { ChannelA = 20 };
        var waves = new[] { Wave("one"), Wave("two"), Wave("three") };
        await engine.StartAsync(["one"], waves[0], null, new(new(PlaybackOrder.Sequential), waves));
        await engine.AdvanceAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(waves[1].Id, engine.CurrentWaveform!.Id);
        Assert.Equal(TimeSpan.Zero, engine.WaveformElapsed);
        await engine.AdvanceAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(waves[2].Id, engine.CurrentWaveform.Id);
        await engine.AdvanceAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(waves[0].Id, engine.CurrentWaveform.Id);
        await engine.StopAsync();
        await engine.StartAsync(["one"], waves[0], null, new(new(PlaybackOrder.Random), waves));
        for (var second = 1; second <= 8; second++)
        {
            var previous = engine.CurrentWaveform;
            await engine.AdvanceAsync(TimeSpan.FromSeconds(second));
            Assert.NotEqual(previous!.Id, engine.CurrentWaveform!.Id);
            Assert.Contains(engine.CurrentWaveform, waves);
            Assert.True(engine.IsRunning);
        }
    }

    [Fact]
    public async Task PlayOnceWaitsForBothIndependentChannelsThenStopsAndRecordsOnce()
    {
        var transport = new RecordingTransport(); await using var controller = await Controller(transport);
        var engine = new SessionEngine(controller) { ChannelA = 20, ChannelB = 10 };
        var records = new List<SessionRecord>(); engine.Completed += (_, record) => records.Add(record);
        await engine.StartAsync(["one"], Wave("short", 1), null, new(new(PlaybackOrder.Once), ChannelBWaveform: Wave("long", 2)));
        await engine.AdvanceAsync(TimeSpan.FromSeconds(1));
        Assert.True(engine.IsRunning); Assert.Equal(0, transport.Latest["one"].ChannelA); Assert.Equal(10, transport.Latest["one"].ChannelB);
        await engine.AdvanceAsync(TimeSpan.FromSeconds(2));
        Assert.False(engine.IsRunning); Assert.Equal(0, transport.Latest["one"].ChannelB);
        Assert.Equal("Reason.WaveComplete", Assert.Single(records).Reason);
        await engine.AdvanceAsync(TimeSpan.FromSeconds(3)); Assert.Single(records);
    }

    [Fact]
    public async Task PauseZerosOutputFreezesPhaseAndCannotBeBypassedByBoostOrTicks()
    {
        var transport = new RecordingTransport(); await using var controller = await Controller(transport);
        var engine = new SessionEngine(controller) { ChannelA = 20 };
        await engine.StartAsync(["one"], Wave("wave", 4), TimeSpan.FromSeconds(10));
        await engine.AdvanceAsync(TimeSpan.FromSeconds(1));
        await engine.SetPausedAsync(true); await engine.SetBoostAsync(true);
        await engine.AdvanceAsync(TimeSpan.FromSeconds(8));
        Assert.True(engine.IsRunning); Assert.True(engine.IsPaused);
        Assert.Equal(TimeSpan.FromSeconds(1), engine.Elapsed); Assert.Equal(TimeSpan.FromSeconds(9), engine.Remaining);
        Assert.Equal(0, transport.Latest["one"].ChannelA);
        await engine.SetPausedAsync(false); await engine.AdvanceAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(20, transport.Latest["one"].ChannelA);
        await engine.StopAsync(); await engine.SetBoostAsync(true); await engine.SetPausedAsync(false);
        Assert.False(engine.IsRunning); Assert.Equal(0, transport.Latest["one"].ChannelA);
    }

    [Fact]
    public async Task StepRandomAndBoostRespectLimitsKeepZeroChannelsZeroAndCanReset()
    {
        var transport = new RecordingTransport(); await using var controller = await Controller(transport);
        var engine = new SessionEngine(controller, new Random(7)) { ChannelA = 20, ChannelB = 0 };
        await engine.StartAsync(["one"], Wave("wave"), null,
            new(new(Strength: new(StrengthMode.StepIncrease, 2, Step: 3, Ceiling: 24, Boost: 10)), OutputLimit: 25));
        await engine.AdvanceAsync(TimeSpan.FromSeconds(2)); Assert.Equal(23, transport.Latest["one"].ChannelA);
        await engine.AdvanceAsync(TimeSpan.FromSeconds(4)); Assert.Equal(24, transport.Latest["one"].ChannelA);
        await engine.SetBoostAsync(true); Assert.Equal(25, transport.Latest["one"].ChannelA); Assert.Equal(0, transport.Latest["one"].ChannelB);
        await engine.SetBoostAsync(false); Assert.Equal(24, transport.Latest["one"].ChannelA);
        await engine.ResetStrengthAsync(); Assert.Equal(20, transport.Latest["one"].ChannelA);
        await engine.StopAsync();
        await engine.StartAsync(["one"], Wave("wave"), null, new(new(Strength: new(StrengthMode.RandomIncrease, 2, RandomAddition: 20)), OutputLimit: 25));
        await engine.AdvanceAsync(TimeSpan.FromSeconds(1)); Assert.Equal(20, transport.Latest["one"].ChannelA);
        await engine.AdvanceAsync(TimeSpan.FromSeconds(2)); var output = transport.Latest["one"].ChannelA;
        Assert.InRange(output, 20, 25); Assert.Equal(0, transport.Latest["one"].ChannelB);
        await engine.AdvanceAsync(TimeSpan.FromSeconds(3)); Assert.Equal(output, transport.Latest["one"].ChannelA);
        await engine.ResetStrengthAsync(); Assert.Equal(20, transport.Latest["one"].ChannelA);
    }

    [Theory]
    [InlineData(TouchPlayback.Momentary)]
    [InlineData(TouchPlayback.RecordedLoop)]
    [InlineData(TouchPlayback.HoldLast)]
    public async Task TouchModesRecordOnlyLocalParametersAndApplyTheirReleaseBehavior(TouchPlayback mode)
    {
        var transport = new RecordingTransport(); await using var controller = await Controller(transport);
        var engine = new SessionEngine(controller) { ChannelA = 20, ChannelB = 10 };
        await engine.StartAsync(["one"], Wave("wave"), null, new(new(TouchPlayback: mode), Kind: ModeKind.Touch));
        await engine.AdvanceAsync(TimeSpan.Zero); Assert.Equal(0, transport.Latest["one"].ChannelA);
        await engine.SetTouchAsync(25, 40, true); await engine.AdvanceAsync(TimeSpan.FromSeconds(0.1));
        await engine.SetTouchAsync(50, 80, true); await engine.AdvanceAsync(TimeSpan.FromSeconds(0.2));
        Assert.Equal(2, engine.Recording.Count);
        await engine.SetTouchAsync(50, 80, false);
        if (mode == TouchPlayback.Momentary) Assert.Equal(0, transport.Latest["one"].ChannelA);
        else
        {
            Assert.Equal(20, transport.Latest["one"].ChannelA);
            Assert.Equal(mode == TouchPlayback.RecordedLoop ? 25 : 50, transport.Latest["one"].ReferenceSample!.FrequencyCode);
        }
        await engine.StopAsync(); await engine.SetTouchAsync(100, 100, true);
        Assert.Equal(0, transport.Latest["one"].ChannelA);
    }

    [Fact]
    public async Task TouchRecordingIsBoundedAndInvalidParametersNeverWrite()
    {
        var transport = new RecordingTransport(); await using var controller = await Controller(transport);
        var engine = new SessionEngine(controller) { ChannelA = 20 };
        await engine.StartAsync(["one"], Wave("wave"), null, new(Kind: ModeKind.Touch));
        await engine.SetTouchAsync(50, 50, true);
        for (var frame = 0; frame < 300; frame++) await engine.AdvanceAsync(TimeSpan.FromMilliseconds(frame * 100));
        Assert.Equal(SessionEngine.MaxRecordingFrames, engine.Recording.Count);
        var writes = transport.Events.Count;
        await Assert.ThrowsAnyAsync<ArgumentException>(() => engine.SetTouchAsync(double.NaN, 50, true));
        Assert.Equal(writes, transport.Events.Count);
    }

    [Fact]
    public async Task DeviceLightRequiresVerifiedFirmwareAndWaitsForConfirmedState()
    {
        var adapter = new FakeBluetoothAdapter();
        await using var controller = new DeviceController(new ElectricalBluetoothTransport(adapter));
        var device = Assert.Single(await controller.ScanAsync(TestContext.Current.CancellationToken));
        await controller.ConnectAsync(device, ct: TestContext.Current.CancellationToken);
        var link = adapter.Links[device.Id];
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => controller.SetLightAsync(device.Id, false, TestContext.Current.CancellationToken));
        Assert.DoesNotContain(link.Writes, packet => packet[1] == 0x15);
        await controller.RefreshFeaturesAsync(device.Id, TestContext.Current.CancellationToken);
        Assert.Equal((byte)1, controller.Connections[device.Id].HardwareVersion);
        Assert.Equal((byte)2, controller.Connections[device.Id].SoftwareVersion);
        Assert.True(controller.Connections[device.Id].LightEnabled);
        Assert.Contains(link.Writes, packet => packet.SequenceEqual(Convert.FromHexString("3613000035")));
        await controller.SetLightAsync(device.Id, false, TestContext.Current.CancellationToken);
        Assert.False(controller.Connections[device.Id].LightEnabled);
        Assert.Contains(link.Writes, packet => packet.SequenceEqual(Convert.FromHexString("3515004A")));
        Assert.Equal(Convert.FromHexString("357107AD"), link.Writes[^1]);
        Assert.Null(V1Protocol.Parse(Convert.FromHexString("361301024D")));
        Assert.Null(V1Protocol.Parse(V1Protocol.WithChecksum([0x35, 0x71, 7, 2])));
    }

    [Fact]
    public async Task MissingFirmwareNeverEnablesLightControlOrClosesAWorkingConnection()
    {
        var adapter = new FakeBluetoothAdapter();
        await using var controller = new DeviceController(new ElectricalBluetoothTransport(adapter));
        var device = Assert.Single(await controller.ScanAsync(TestContext.Current.CancellationToken));
        await controller.ConnectAsync(device, ct: TestContext.Current.CancellationToken);
        var link = adapter.Links[device.Id]; link.ReplyToFeatures = false;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => controller.RefreshFeaturesAsync(device.Id, cancellation.Token));
        Assert.Null(controller.Connections[device.Id].HardwareVersion);
        Assert.Null(controller.Connections[device.Id].LightEnabled);
        Assert.False(link.Closed);
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => controller.SetLightAsync(device.Id, true, TestContext.Current.CancellationToken));
        Assert.DoesNotContain(link.Writes, packet => packet[1] == 0x15);
    }

    [Fact]
    public async Task EstimPreferencesAndIndependentChannelHistoryRoundTripLocally()
    {
        using var folder = new TestFolder();
        var options = new EstimSettings(PlaybackOrder.Random, new(StrengthMode.StepIncrease, 3, 5, 2, 40, 8), true, true, false, TouchPlayback.RecordedLoop);
        var settings = new AppSettings();
        var mode = new PlaybackModeConfig(Guid.NewGuid(), "Preset", Estim: options);
        var a = Wave("A"); var b = Wave("B");
        var record = new SessionRecord(Guid.NewGuid(), DateTimeOffset.Now, 1, a.Name, 1, 20, 10, "Reason.Manual", a.Id, b.Id, b.Name, Gentle: true);
        var store = new WorkspaceStore(folder.Path);
        await store.SaveAsync(new(3, [a, b], [record], settings, Modes: [mode]));
        var restored = await new WorkspaceStore(folder.Path).LoadAsync();
        Assert.Equal(options, Assert.Single(restored.Modes!).Estim);
        Assert.Equal(record, Assert.Single(restored.Sessions));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.SaveAsync(new(3, [], [], new(), Modes: [mode with { Estim = options with { Strength = new(IntervalSeconds: 0) } }])));
    }

    [Fact]
    public void GentleModeUsesLowerWireStrengthForBothGenerationsAndPreservesZero()
    {
        var gentle = new OutputFrame(100, 50, 20, Gentle: true);
        var v1 = V1Protocol.Encode(gentle); var v2 = V2Protocol.Encode(gentle);
        Assert.Equal(180, BinaryPrimitives.ReadUInt16BigEndian(v1[0].AsSpan(4, 2)));
        Assert.Equal(90, BinaryPrimitives.ReadUInt16BigEndian(v1[1].AsSpan(4, 2)));
        Assert.Equal(180, BinaryPrimitives.ReadUInt16BigEndian(v2.AsSpan(3, 2)));
        Assert.Equal(90, BinaryPrimitives.ReadUInt16BigEndian(v2.AsSpan(7, 2)));
        Assert.Equal(V2Protocol.Stop(), V2Protocol.Encode(OutputFrame.Zero with { Gentle = true }));
    }
}
