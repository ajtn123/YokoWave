using System.Text.Json;
using YokoWave.Core;
using Xunit;

namespace YokoWave.Tests;

public sealed class CoreTests
{
    private static DeviceDescriptor Device(string id = "one", DeviceKind kind = DeviceKind.DualChannel) => new(id, id, kind, 90, -40);

    [Theory]
    [InlineData(WaveShape.Sine)]
    [InlineData(WaveShape.Triangle)]
    [InlineData(WaveShape.Pulse)]
    [InlineData(WaveShape.Ramp)]
    [InlineData(WaveShape.Custom)]
    public void WaveformsStayNormalizedAndRepeat(WaveShape shape)
    {
        var preset = new WaveformPreset(Guid.NewGuid(), "test", shape, 4, 20, "", [0, 0.3, 1, 0]);
        for (var i = -100; i < 100; i++)
        {
            var time = i / 10d;
            var value = WaveformGenerator.Sample(preset, time);
            Assert.InRange(value, 0, 1);
            Assert.Equal(value, WaveformGenerator.Sample(preset, time + 4), 8);
        }
    }

    [Fact]
    public async Task ControllerClampsOutputsAndAppliesLowerLimitImmediately()
    {
        var transport = new RecordingTransport();
        await using var controller = new DeviceController(transport);
        await controller.ConnectAsync(Device(), 40, TestContext.Current.CancellationToken);
        Assert.Equal(OutputFrame.Zero, transport.Latest["one"]);
        await controller.SendAsync("one", new(99, double.NaN, double.PositiveInfinity), TestContext.Current.CancellationToken);
        Assert.Equal(new OutputFrame(40, 0, 1), transport.Latest["one"]);
        await controller.SetLimitAsync("one", 12, TestContext.Current.CancellationToken);
        Assert.Equal(12, transport.Latest["one"].ChannelA);
        await controller.DisconnectAsync("one", TestContext.Current.CancellationToken);
        Assert.Equal("send:one:0:0", transport.Events[^2]);
        Assert.Equal("disconnect:one", transport.Events[^1]);
    }

    [Fact]
    public async Task SingleChannelDeviceNeverReceivesChannelB()
    {
        var transport = new RecordingTransport();
        await using var controller = new DeviceController(transport);
        await controller.ConnectAsync(Device(kind: DeviceKind.Pump), ct: TestContext.Current.CancellationToken);
        await controller.SendAsync("one", new(20, 40, 30), TestContext.Current.CancellationToken);
        Assert.Equal(0, transport.Latest["one"].ChannelB);
    }

    [Fact]
    public async Task StopAttemptsEveryDeviceEvenWhenOneWriteFails()
    {
        var transport = new RecordingTransport();
        var controller = new DeviceController(transport);
        await controller.ConnectAsync(Device("one"), ct: TestContext.Current.CancellationToken);
        await controller.ConnectAsync(Device("two"), ct: TestContext.Current.CancellationToken);
        await controller.SendAsync("two", new(25, 15, 20), TestContext.Current.CancellationToken);
        transport.FailId = "one";
        await Assert.ThrowsAsync<AggregateException>(() => controller.StopAllAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, transport.Latest["two"].ChannelA);
        transport.FailId = null;
        await controller.DisposeAsync();
    }

    [Fact]
    public async Task SessionLinksTargetsRespectsTheirLimitsAndTimesOut()
    {
        var transport = new RecordingTransport();
        await using var controller = new DeviceController(transport);
        await controller.ConnectAsync(Device("one"), 15, TestContext.Current.CancellationToken);
        await controller.ConnectAsync(Device("two"), 50, TestContext.Current.CancellationToken);
        var engine = new SessionEngine(controller) { ChannelA = 40, ChannelB = 30, OffsetChannelB = false };
        SessionRecord? completed = null;
        engine.Completed += (_, record) => completed = record;
        await engine.StartAsync(["one", "two"], WaveformPreset.BuiltIn[0], TimeSpan.FromSeconds(4));
        await engine.AdvanceAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(15, transport.Latest["one"].ChannelA);
        Assert.Equal(40, transport.Latest["two"].ChannelA);
        await engine.AdvanceAsync(TimeSpan.FromSeconds(5));
        Assert.False(engine.IsRunning);
        Assert.All(transport.Latest.Values, frame => Assert.Equal(0, frame.ChannelA));
        Assert.NotNull(completed);
        Assert.Equal(4, completed.DurationSeconds);
        Assert.Equal(2, completed.DeviceCount);
        Assert.Equal("定时结束", completed.Reason);
        await engine.AdvanceAsync(TimeSpan.FromSeconds(6));
        Assert.All(transport.Latest.Values, frame => Assert.Equal(0, frame.ChannelA));
    }

    [Fact]
    public async Task SessionFailureStopsOtherTargetsAndReportsError()
    {
        var transport = new RecordingTransport();
        await using var controller = new DeviceController(transport);
        await controller.ConnectAsync(Device("one"), ct: TestContext.Current.CancellationToken);
        await controller.ConnectAsync(Device("two"), ct: TestContext.Current.CancellationToken);
        var engine = new SessionEngine(controller) { ChannelA = 30 };
        await engine.StartAsync(["one", "two"], WaveformPreset.BuiltIn[0], TimeSpan.FromMinutes(1));
        transport.FailId = "two";
        await Assert.ThrowsAnyAsync<Exception>(() => engine.AdvanceAsync(TimeSpan.FromSeconds(2)));
        Assert.False(engine.IsRunning);
        Assert.Equal(0, transport.Latest["one"].ChannelA);
        transport.FailId = null;
    }

    [Fact]
    public async Task ConcurrentTickAndStopCannotWriteNonZeroAfterStop()
    {
        var transport = new RecordingTransport();
        await using var controller = new DeviceController(transport);
        await controller.ConnectAsync(Device(), ct: TestContext.Current.CancellationToken);
        var engine = new SessionEngine(controller) { ChannelA = 40 };
        await engine.StartAsync(["one"], WaveformPreset.BuiltIn[0], TimeSpan.FromMinutes(1));
        await Task.WhenAll(engine.AdvanceAsync(TimeSpan.FromSeconds(2)), engine.StopAsync());
        await engine.AdvanceAsync(TimeSpan.FromSeconds(3));
        Assert.False(engine.IsRunning);
        Assert.Equal(0, transport.Latest["one"].ChannelA);
    }

    [Fact]
    public async Task WorkspaceRoundTripsAndPreservesCorruptOriginal()
    {
        using var folder = new TestFolder();
        var store = new WorkspaceStore(folder.Path);
        var custom = WaveformPreset.BuiltIn[0] with { Id = Guid.NewGuid(), Name = "自定义" };
        await store.SaveAsync(new(1, [custom], [], new(35, 5, false)));
        var loaded = await store.LoadAsync();
        Assert.Equal(custom, Assert.Single(loaded.Presets));
        Assert.Equal(35, loaded.Settings.DefaultLimit);
        await File.WriteAllTextAsync(store.FilePath, "broken", TestContext.Current.CancellationToken);
        Assert.Empty((await store.LoadAsync()).Presets);
        Assert.NotNull(store.LoadWarning);
        await Assert.ThrowsAsync<IOException>(() => store.SaveAsync(WorkspaceData.Empty));
        Assert.Equal("broken", await File.ReadAllTextAsync(store.FilePath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void WaveImportRejectsInvalidRangesAndDuplicateIdentifiers()
    {
        var preset = WaveformPreset.BuiltIn[0];
        Assert.Throws<ArgumentException>(() => WorkspaceStore.ImportPresets(WorkspaceStore.ExportPresets([preset, preset])));
        Assert.Throws<ArgumentException>(() => WorkspaceStore.ImportPresets(WorkspaceStore.ExportPresets([preset with { PeriodSeconds = 0 }])));
        Assert.Throws<ArgumentException>(() => WorkspaceStore.ImportPresets(WorkspaceStore.ExportPresets([preset with { Shape = WaveShape.Custom, Points = [0, 2] }])));
        Assert.Throws<JsonException>(() => WorkspaceStore.ImportPresets("not json"));
        Assert.Single(WorkspaceStore.ImportPresets(WorkspaceStore.ExportPresets([preset])));
    }
}

internal sealed class RecordingTransport : IDeviceTransport
{
    public string DisplayName => "test";
    public Dictionary<string, OutputFrame> Latest { get; } = [];
    public List<string> Events { get; } = [];
    public string? FailId { get; set; }
    public Task<IReadOnlyList<DeviceDescriptor>> ScanAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<DeviceDescriptor>>([]);
    public Task ConnectAsync(DeviceDescriptor device, CancellationToken cancellationToken = default)
    { Latest.Add(device.Id, OutputFrame.Zero); return Task.CompletedTask; }
    public Task SendAsync(string deviceId, OutputFrame frame, CancellationToken cancellationToken = default)
    {
        if (deviceId == FailId) throw new IOException("Test connection lost");
        Latest[deviceId] = frame;
        Events.Add($"send:{deviceId}:{frame.ChannelA}:{frame.ChannelB}");
        return Task.CompletedTask;
    }
    public Task DisconnectAsync(string deviceId, CancellationToken cancellationToken = default)
    { Events.Add($"disconnect:{deviceId}"); Latest.Remove(deviceId); return Task.CompletedTask; }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class TestFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory,
        "../../../../../artifacts/test-data", Guid.NewGuid().ToString("N")));
    public TestFolder() => Directory.CreateDirectory(Path);
    public void Dispose() => Directory.Delete(Path, true);
}
