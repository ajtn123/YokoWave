using YokoWave.Core;
using Xunit;

namespace YokoWave.Tests;

public sealed class ModeTests
{
    private static WaveformPreset Wave() => new(Guid.NewGuid(), "constant", WaveShape.Custom, 4, 20, [1, 1]);
    private static async Task Connect(DeviceController controller, params string[] ids)
    {
        foreach (var id in ids) await controller.ConnectAsync(new(id, id, DeviceKind.DualChannel, 80, -40), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ThreeModesCanOwnOneOneAndTwoDevicesWithoutStoppingEachOther()
    {
        var transport = new RecordingTransport(); await using var controller = new DeviceController(transport);
        await Connect(controller, "one", "two", "three", "four");
        var first = new SessionEngine(controller) { ChannelA = 40 };
        var second = new SessionEngine(controller) { ChannelA = 50 };
        var third = new SessionEngine(controller) { ChannelA = 60 };
        await first.StartAsync(["one"], Wave(), null, new(OutputLimit: 20)); await first.AdvanceAsync(TimeSpan.FromSeconds(1));
        await second.StartAsync(["two"], Wave(), null, new(OutputLimit: 30)); await second.AdvanceAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(20, transport.Latest["one"].ChannelA);
        await third.StartAsync(["three", "four"], Wave(), null, new(OutputLimit: 10)); await third.AdvanceAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(20, transport.Latest["one"].ChannelA); Assert.Equal(30, transport.Latest["two"].ChannelA);
        Assert.Equal(10, transport.Latest["three"].ChannelA); Assert.Equal(10, transport.Latest["four"].ChannelA);
        await third.SetOutputLimitAsync(5);
        Assert.Equal(5, transport.Latest["three"].ChannelA); Assert.Equal(5, transport.Latest["four"].ChannelA);
        Assert.Equal(30, transport.Latest["two"].ChannelA);
        await first.StopAsync();
        Assert.Equal(0, transport.Latest["one"].ChannelA); Assert.True(second.IsRunning); Assert.True(third.IsRunning);
        Assert.Equal(30, transport.Latest["two"].ChannelA); Assert.Equal(5, transport.Latest["four"].ChannelA);
    }

    [Fact]
    public async Task DeviceClaimsAreAtomicRetainedDuringPauseAndReleasedAfterStop()
    {
        var transport = new RecordingTransport(); await using var controller = new DeviceController(transport);
        await Connect(controller, "one", "two");
        var first = new SessionEngine(controller) { ChannelA = 20 };
        var second = new SessionEngine(controller) { ChannelA = 30 };
        await first.StartAsync(["one"], Wave(), null); await first.AdvanceAsync(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => second.StartAsync(["two", "one"], Wave(), null));
        Assert.Null(controller.DeviceOwner("two")); Assert.Equal(20, transport.Latest["one"].ChannelA);
        await first.SetPausedAsync(true);
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => second.StartAsync(["one"], Wave(), null));
        await second.StartAsync(["two"], Wave(), null); await second.AdvanceAsync(TimeSpan.FromSeconds(1));
        await first.StopAsync(); Assert.Null(controller.DeviceOwner("one"));
        Assert.Equal(30, transport.Latest["two"].ChannelA);
        await first.StartAsync(["one"], Wave(), null); await first.AdvanceAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(20, transport.Latest["one"].ChannelA); Assert.Equal(30, transport.Latest["two"].ChannelA);
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => controller.SendAsync("one", new(100, 0, 20), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FailedStopKeepsTheDeviceClaimUntilOutputCanBeStopped()
    {
        var transport = new RecordingTransport(); await using var controller = new DeviceController(transport);
        await Connect(controller, "one");
        var first = new SessionEngine(controller) { ChannelA = 20 }; var second = new SessionEngine(controller);
        await first.StartAsync(["one"], Wave(), null); await first.AdvanceAsync(TimeSpan.FromSeconds(1));
        transport.FailId = "one";
        await Assert.ThrowsAnyAsync<AggregateException>(() => first.StopAsync());
        Assert.Equal(first.OwnerId, controller.DeviceOwner("one"));
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => second.StartAsync(["one"], Wave(), null));
        transport.FailId = null; await first.StopAsync();
        await second.StartAsync(["one"], Wave(), null);
        Assert.Equal(second.OwnerId, controller.DeviceOwner("one"));
    }

    [Fact]
    public async Task ModeConfigsPersistInTheirOwnFileWithoutDeviceConfiguration()
    {
        using var folder = new TestFolder(); var store = new WorkspaceStore(folder.Path);
        var configs = new List<PlaybackModeConfig>
        {
            new(Guid.NewGuid(), "First", ChannelA: 20, OutputLimit: 30, TargetDeviceIds: ["one"]),
            new(Guid.NewGuid(), "Second", ChannelA: 40, OutputLimit: 50, DurationSeconds: 30, TargetDeviceIds: ["two"]),
            new(Guid.NewGuid(), "Third", TouchControl: true, TargetDeviceIds: ["three", "four"])
        };
        await store.SaveAsync(new(3, [], [], new(), Modes: configs));
        var loaded = await new WorkspaceStore(folder.Path).LoadAsync(); Assert.Equivalent(configs, loaded.Modes);
        Assert.DoesNotContain('\n', await File.ReadAllTextAsync(store.ModesPath, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(folder.Path, "*device*"));
        Assert.DoesNotContain("ChannelA", await File.ReadAllTextAsync(store.SettingsPath, TestContext.Current.CancellationToken));
    }
}
