using System.Text.Json;
using Xunit;
using YokoWave.Core;

namespace YokoWave.Tests;

public sealed class CoreTests
{

    [Theory]
    [InlineData(WaveShape.Sine)]
    [InlineData(WaveShape.Triangle)]
    [InlineData(WaveShape.Pulse)]
    [InlineData(WaveShape.Ramp)]
    [InlineData(WaveShape.Custom)]
    public void WaveformsStayNormalizedAndRepeat(WaveShape shape)
    {
        var waveform = new WaveformAsset(Guid.NewGuid(), "test", shape, 4, 20, [0, 0.3, 1, 0]);
        for (int i = -100; i < 100; i++)
        {
            double time = i / 10d;
            double value = WaveformGenerator.Sample(waveform, time);
            Assert.InRange(value, 0, 1);
            Assert.Equal(value, WaveformGenerator.Sample(waveform, time + 4), 8);
        }
    }

    [Fact]
    public async Task SingleChannelDeviceNeverReceivesChannelB()
    {
        var transport = new RecordingTransport();
        await using var controller = new DeviceController(transport);
        await controller.ConnectAsync(Device(kind: DeviceKind.Pump), cancellationToken: TestContext.Current.CancellationToken);
        await controller.SendAsync("one", new(20, 40, 30), TestContext.Current.CancellationToken);
        Assert.Equal(0, transport.Latest["one"].ChannelB);
    }

    [Fact]
    public async Task StopAttemptsEveryDeviceEvenWhenOneWriteFails()
    {
        var transport = new RecordingTransport();
        var controller = new DeviceController(transport);
        await controller.ConnectAsync(Device("one"), cancellationToken: TestContext.Current.CancellationToken);
        await controller.ConnectAsync(Device("two"), cancellationToken: TestContext.Current.CancellationToken);
        await controller.SendAsync("two", new(25, 15, 20), TestContext.Current.CancellationToken);
        transport.FailId = "one";
        await Assert.ThrowsAnyAsync<AggregateException>(() => controller.StopAllAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, transport.Latest["two"].ChannelA);
        transport.FailId = null;
        await controller.DisposeAsync();
    }

    [Fact]
    public async Task WorkspaceRoundTripsAndPreservesCorruptOriginal()
    {
        using var folder = new TestFolder();
        var store = new WorkspaceStore(folder.Path);
        var custom = WaveformAsset.BuiltIn[0] with
        {
            Id = Guid.NewGuid(),
            Name = "自定义"
        };
        await store.SaveAsync(new(WorkspaceData.CurrentSchema, [custom], [], new(35)));
        var loaded = await store.LoadAsync();
        Assert.Equivalent(custom, Assert.Single(loaded.Waveforms));
        Assert.Equal(35, loaded.Settings.DefaultLimit);
        await File.WriteAllTextAsync(store.FilePath, "broken", TestContext.Current.CancellationToken);
        Assert.Empty((await store.LoadAsync()).Waveforms);
        Assert.NotNull(store.LoadWarning);
        await Assert.ThrowsAnyAsync<IOException>(() => store.SaveAsync(WorkspaceData.Empty));
        Assert.Equal("broken", await File.ReadAllTextAsync(store.FilePath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void WaveImportRejectsInvalidRangesAndDuplicateIdentifiers()
    {
        var waveform = WaveformAsset.BuiltIn[0];
        Assert.ThrowsAny<ArgumentException>(() => WaveformExchange.Import(WaveformExchange.Export([waveform, waveform])));
        Assert.ThrowsAny<ArgumentException>(() => WaveformExchange.Import(WaveformExchange.Export([waveform with { PeriodSeconds = 0 }])));
        Assert.ThrowsAny<ArgumentException>(() => WaveformExchange.Import(WaveformExchange.Export([waveform with { Shape = WaveShape.Custom, Points = [0, 2] }])));
        Assert.Throws<JsonException>(() => WaveformExchange.Import("not json"));
        Assert.Single(WaveformExchange.Import(WaveformExchange.Export([waveform])));
    }
    private static DeviceDescriptor Device(string id = "one", DeviceKind kind = DeviceKind.DualChannel) => new(id, id, kind, 90, -40);
    private static WaveformAsset Sine() => new(Guid.NewGuid(), "test", WaveShape.Sine, 4, 20);
}
