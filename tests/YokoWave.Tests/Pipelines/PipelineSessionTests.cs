using YokoWave.Core;
using YokoWave.Core.Localization;
using YokoWave.Pipelines;
using YokoWave.Tests.Support.Pipelines;
using static YokoWave.Tests.Support.Pipelines.PipelineTestGraphs;

namespace YokoWave.Tests.Pipelines;

public sealed class PipelineSessionTests
{
    [Fact]
    public async Task SessionRunsWithoutUiPausesWithOwnershipAndStopsBeforeRelease()
    {
        var transport = new PipelineTransport();
        await using var controller = new DeviceController(transport);
        await controller.ConnectAsync(Device, TestContext.Current.CancellationToken);
        await using var session = new PipelineSession(controller);
        await session.StartAsync(CreatePlan());
        await AsyncAssert.EventuallyAsync(() => transport.Last.ChannelA == 50);
        Assert.Equal(session.OwnerId, controller.DeviceOwner("one"));
        await session.PauseAsync();
        Assert.Equal(PipelineState.Paused, session.State);
        Assert.Equal(0, transport.Last.ChannelA);
        Assert.Equal(session.OwnerId, controller.DeviceOwner("one"));
        await using var other = new PipelineSession(controller);
        await Assert.ThrowsAsync<LocalizedOperationException>(() => other.StartAsync(CreatePlan()));
        await session.PauseAsync();
        await AsyncAssert.EventuallyAsync(() => transport.Last.ChannelA == 50);
        SessionRecord? record = null;
        session.Completed += (_, item) => record = item;
        await session.StopAsync();
        Assert.Equal(0, transport.Last.ChannelA);
        Assert.Null(controller.DeviceOwner("one"));
        Assert.Equal(PipelineState.Idle, session.State);
        Assert.NotNull(record);
        Assert.Equal(50, record.PeakA);
    }

    [Fact]
    public async Task ZeroStrengthStartsAndTimerStopsWithoutUiTicks()
    {
        var transport = new PipelineTransport();
        await using var controller = new DeviceController(transport);
        await controller.ConnectAsync(Device, TestContext.Current.CancellationToken);
        await using var session = new PipelineSession(controller);
        await session.StartAsync(CreatePlan(0, 1));
        Assert.Equal(PipelineState.Running, session.State);
        await AsyncAssert.EventuallyAsync(() => session.State == PipelineState.Idle);
        Assert.Null(controller.DeviceOwner("one"));
        Assert.All(transport.Frames, f => Assert.Equal(0, f.ChannelA));
    }

    [Fact]
    public async Task ObserverFailureCannotInterruptOutputOrMarkConfirmedStopAsFaulted()
    {
        var transport = new PipelineTransport();
        await using var controller = new DeviceController(transport);
        await controller.ConnectAsync(Device, TestContext.Current.CancellationToken);
        await using var session = new PipelineSession(controller);
        session.Changed += (_, _) => throw new InvalidOperationException("observer");
        session.Completed += (_, _) => throw new InvalidOperationException("observer");
        await session.StartAsync(CreatePlan());
        await AsyncAssert.EventuallyAsync(() => transport.Last.ChannelA == 50);
        await session.StopAsync();
        Assert.Equal(PipelineState.Idle, session.State);
        Assert.Null(controller.DeviceOwner("one"));
    }

    [Fact]
    public async Task IndependentDevicesDispatchTogetherAndStopWaitsForInFlightWrite()
    {
        var transport = new PipelineTransport();
        await using var controller = new DeviceController(transport);
        await controller.ConnectAsync(Device, TestContext.Current.CancellationToken);
        await controller.ConnectAsync(Device with { Id = "fast" }, TestContext.Current.CancellationToken);
        await using var session = new PipelineSession(controller);
        var number = CreateNumber(.5);
        var first = CreateOutput();
        var second = NodeDefinition.Create(BuiltInNodes.ChannelOutput, new ChannelOutputNode("fast"));
        var plan = CreateCompiler().Compile(CreateGraph([number, first, second], CreateEdge(number, first), CreateEdge(number, second)), []);
        transport.BlockNonzero = true;
        var start = session.StartAsync(plan);
        Task? stop = null;
        try
        {
            await transport.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            await transport.FastWrite.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            stop = session.StopAsync();
            Assert.False(stop.IsCompleted);
        }
        finally
        {
            transport.ReleaseWrite.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
            await (stop ?? session.StopAsync());
        }

        Assert.Null(controller.DeviceOwner("one"));
        Assert.Null(controller.DeviceOwner("fast"));
        Assert.Equal(0, transport.Last.ChannelA);
    }

    [Fact]
    public async Task StopFailureKeepsDeviceReservedUntilRetrySucceeds()
    {
        var transport = new PipelineTransport();
        await using var controller = new DeviceController(transport);
        await controller.ConnectAsync(Device, TestContext.Current.CancellationToken);
        await using var session = new PipelineSession(controller);
        await session.StartAsync(CreatePlan());
        await AsyncAssert.EventuallyAsync(() => transport.Last.ChannelA > 0);
        transport.FailZero = true;
        await Assert.ThrowsAnyAsync<Exception>(() => session.StopAsync());
        Assert.Equal(PipelineState.Faulted, session.State);
        Assert.Equal(session.OwnerId, controller.DeviceOwner("one"));
        transport.FailZero = false;
        await session.StopAsync();
        Assert.Null(controller.DeviceOwner("one"));
        Assert.Equal(0, transport.Last.ChannelA);
    }

    [Fact]
    public async Task OutputFailureAndFailedCleanupPreserveBothErrors()
    {
        var transport = new PipelineTransport();
        await using var controller = new DeviceController(transport);
        await controller.ConnectAsync(Device, TestContext.Current.CancellationToken);
        await using var session = new PipelineSession(controller);
        await session.StartAsync(CreatePlan());
        transport.FailNonzero = true;
        transport.FailZero = true;
        await AsyncAssert.EventuallyAsync(() => session.State == PipelineState.Faulted);
        var errors = Assert.IsAssignableFrom<AggregateException>(session.Error).Flatten().InnerExceptions;
        Assert.Contains(errors, e => e.Message == "write failed");
        Assert.Contains(errors, e => e.Message == "zero failed");
        Assert.Equal(session.OwnerId, controller.DeviceOwner("one"));
        transport.FailZero = false;
        await session.StopAsync();
    }

    [Fact]
    public async Task TimedPumpCommandsAreDeduplicatedAndResumeAndRestartSendAgain()
    {
        var transport = new PipelineTransport();
        await using var controller = new DeviceController(transport);
        await controller.ConnectAsync(new("pump", "pump", DeviceKind.Pump, 80, -40), TestContext.Current.CancellationToken);
        var direction = CreateNumber(1);
        var seconds = CreateNumber(17);
        var output = NodeDefinition.Create(BuiltInNodes.PumpOutput, new PumpOutputNode("pump"));
        var plan = CreateCompiler().Compile(CreateGraph([direction, seconds, output], CreateEdge(direction, output, "direction"), CreateEdge(seconds, output, "peristalticSeconds")), []);
        var timed = new PumpOutput(PumpDirection.Forward, PeristalticSeconds: 17);
        int Count(PumpOutput pump) => transport.Frames.Count(frame => frame.Pump == pump);
        await using var session = new PipelineSession(controller);
        await session.StartAsync(plan);
        await session.FlushAsync();
        Assert.Equal(1, Count(timed));
        await session.PauseAsync();
        await session.PauseAsync();
        await session.FlushAsync();
        Assert.Equal(2, Count(timed));
        session.SetConstant(seconds.Id, 0);
        await session.FlushAsync();
        var continuous = new PumpOutput(PumpDirection.Forward);
        int before = Count(continuous);
        Assert.True(before > 0);
        await session.FlushAsync();
        Assert.True(Count(continuous) > before);
        await session.StopAsync();
        await session.StartAsync(plan);
        await session.FlushAsync();
        Assert.Equal(3, Count(timed));
        await session.StopAsync();
        Assert.Null(controller.DeviceOwner("pump"));
        Assert.Null(transport.Last.Pump);
    }
}
