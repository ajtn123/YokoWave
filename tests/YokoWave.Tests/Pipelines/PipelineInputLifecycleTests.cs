using YokoWave.Core;
using YokoWave.Pipelines;
using YokoWave.Tests.Support.Audio;
using YokoWave.Tests.Support.Bluetooth;

namespace YokoWave.Tests.Pipelines;

public sealed class PipelineInputLifecycleTests
{
    [Theory]
    [InlineData(AudioSourceKind.Microphone)]
    [InlineData(AudioSourceKind.SystemAudio)]
    [InlineData(AudioSourceKind.MediaFile)]
    public async Task AudioResourcesListenAcrossGapsMuteAndEofAndReleaseAfterStop(AudioSourceKind source)
    {
        var audio = new FakeAudioInputFactory();
        var transport = new RecordingTransport();
        await using var controller = new DeviceController(transport);
        await controller.ConnectAsync(new("out", "out", DeviceKind.DualChannel, 80, -40), TestContext.Current.CancellationToken);
        var graph = PipelineTemplates.Create("audio", new("audio", "out"));
        var input = graph.Nodes.Single(n => n.Type == BuiltInNodes.Audio);
        graph = graph with
        {
            Nodes = graph.Nodes.Select(n => n.Id == input.Id ? NodeDefinition.Create(n.Type, new AudioNode(new(Source: source, MediaPath: "test.wav", PlaybackVolume: 0)), n.Id) : n).ToArray()
        };
        await using var session = new PipelineSession(controller, new(audio));
        await session.StartAsync(new PipelineCompiler(BuiltInNodes.CreateRegistry()).Compile(graph, []));
        var capture = Assert.Single(audio.Sessions);
        Assert.Equal(0, Assert.Single(audio.Requests).PlaybackVolume);
        capture.NoFrames = true;
        capture.Ended = true;
        await session.FlushAsync();
        Assert.Equal(PipelineState.Running, session.State);
        Assert.False(capture.Disposed);
        capture.NoFrames = false;
        capture.Ended = false;
        await session.FlushAsync();
        Assert.Equal("Tracking", session.NodeStatus[input.Id].Phase);
        await session.PauseAsync();
        Assert.True(capture.Paused);
        Assert.Equal(session.OwnerId, controller.DeviceOwner("out"));
        await session.PauseAsync();
        await session.StopAsync();
        Assert.True(capture.Disposed);
        Assert.Null(controller.DeviceOwner("out"));
    }

    [Fact]
    public async Task EmergencyStopCancelsPendingInputStartupAndReleasesReservation()
    {
        var audio = new FakeAudioInputFactory
        {
            BlockStart = true
        };
        await using var controller = new DeviceController(new RecordingTransport());
        await controller.ConnectAsync(new("out", "out", DeviceKind.DualChannel, 80, -40), TestContext.Current.CancellationToken);
        await using var session = new PipelineSession(controller, new(audio));
        var graph = PipelineTemplates.Create("audio", new("audio", "out"));
        var start = session.StartAsync(new PipelineCompiler(BuiltInNodes.CreateRegistry()).Compile(graph, []));
        await audio.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
        Assert.Equal(PipelineState.Idle, session.State);
        Assert.Null(controller.DeviceOwner("out"));
    }

    [Fact]
    public async Task FailedInputStartupClearsRunStateAndAllowsAFreshRun()
    {
        var failure = new IOException("input startup failed");
        var audio = new FakeAudioInputFactory();
        await using var controller = new DeviceController(new RecordingTransport());
        await controller.ConnectAsync(new("out", "out", DeviceKind.DualChannel, 80, -40), TestContext.Current.CancellationToken);
        await using var session = new PipelineSession(controller, new(audio));
        var graph = PipelineTemplates.Create("audio", new("audio", "out")) with
        {
            TimerEnabled = true
        };
        var plan = new PipelineCompiler(BuiltInNodes.CreateRegistry()).Compile(graph, []);
        List<SessionRecord> records = [];
        session.Completed += (_, record) => records.Add(record);
        await session.StartAsync(plan);
        var previousInputs = session.Inputs;
        Assert.NotNull(session.LastFrame);
        await session.StopAsync();
        audio.StartError = failure;
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => session.StartAsync(plan)));
        Assert.Equal(PipelineState.Idle, session.State);
        Assert.Null(session.Inputs);
        Assert.Null(session.Remaining);
        Assert.Null(session.LastFrame);
        Assert.Empty(session.NodeStatus);
        Assert.Same(failure, session.Error);
        Assert.Null(controller.DeviceOwner("out"));
        Assert.Single(records);
        Assert.All(audio.Sessions, capture => Assert.True(capture.Disposed));
        audio.StartError = null;
        await session.StartAsync(plan);
        Assert.NotNull(session.Inputs);
        Assert.NotSame(previousInputs, session.Inputs);
        Assert.Null(session.Error);
        await session.StopAsync();
        Assert.Equal(2, records.Count);
    }

    [Fact]
    public async Task PausedSessionStopsAllItsTargetsOnTransportFaultWithoutAnyUi()
    {
        var transport = new ConcurrentTransport();
        await using var controller = new DeviceController(transport);
        foreach (string? id in new[] { "one", "two" })
        {
            await controller.ConnectAsync(new(id, id, DeviceKind.DualChannel, 80, -40), TestContext.Current.CancellationToken);
        }

        var b = new PipelineBuilder("fault");
        var input = b.Add(BuiltInNodes.Constant, new ConstantNode(.3));
        foreach (string? id in new[] { "one", "two" })
        {
            var output = b.Add(BuiltInNodes.ChannelOutput, new ChannelOutputNode(id));
            b.Connect(input, "value", output, "strength");
        }

        await using var session = new PipelineSession(controller);
        await session.StartAsync(new PipelineCompiler(BuiltInNodes.CreateRegistry()).Compile(b.Build(), []));
        await session.PauseAsync();
        var original = new IOException("transport fault");
        transport.ReportFault(new("one", true, original));
        await AsyncAssert.EventuallyAsync(() => session.State == PipelineState.Idle);
        Assert.Equal(PipelineState.Idle, session.State);
        Assert.Same(original, session.Error);
        Assert.Null(controller.DeviceOwner("two"));
        Assert.Equal(0, transport.Outputs["two"].ChannelA);
    }
}
