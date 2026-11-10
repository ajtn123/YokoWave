using YokoWave.Core.Localization;
using YokoWave.Pipelines;
using YokoWave.Remote;
using YokoWave.Remote.DgLab.V4;
using YokoWave.Tests.Support.Remote;

namespace YokoWave.Tests.Remote;

public sealed class DgLabV4ControllerTests
{
    [Fact]
    public async Task TypedControllerExecutesAllOperationsAndBothPulseFormats()
    {
        await using var host = await DgLabV4TestHost.CreateAsync();
        var client = host.Controller;
        Assert.Single((await client.GetDevicesAsync(host.AppId, Ct)).GetProperty("devices").EnumerateArray());
        Assert.True((await client.PingAsync(host.AppId, Ct)).GetInt64() > 0);
        await client.AddIntensityAsync(host.AppId, host.SlotId, 0, 20, cancellationToken: Ct);
        var temporary = client.SetTemporaryIntensityAsync(host.AppId, host.SlotId, 0, 20, new(Duration: 200), Ct);
        var v2 = client.SendPulseAsync(host.AppId, host.SlotId, 0, RemoteProtocol.Element(new[] { new[] { 33, 1, 10 } }), version: 2, sequence: 7, cancellationToken: Ct);
        Assert.Equal("completed", (await v2).GetProperty("reason").GetString());
        await temporary;
        var v3 = client.SendPulseAsync(host.AppId, host.SlotId, 0, RemoteProtocol.Element(new[] { "3232323264646464" }), cancellationToken: Ct);
        Assert.Equal("completed", (await v3).GetProperty("reason").GetString());
        await client.AddIntensityAsync(host.AppId, host.SlotId, 0, -10, cancellationToken: Ct);
        await AsyncAssert.EventuallyAsync(() => host.Devices.Connections[host.Device.Id].Output.ChannelA == 5);
        await client.ResetIntensityAsync(host.AppId, host.SlotId, 0, cancellationToken: Ct);
        await AsyncAssert.EventuallyAsync(() => host.Devices.Connections[host.Device.Id].Output.ChannelA == 0);
    }

    [Fact]
    public async Task PauseCancelsQueuedTasksAndClearRemainsUsableWhilePaused()
    {
        await using var host = await DgLabV4TestHost.CreateAsync();
        var pending = Enumerable.Range(0, 20).Select(_ => host.Controller.SetTemporaryIntensityAsync(host.AppId, host.SlotId, 0, 40, cancellationToken: Ct)).ToArray();
        await host.Controller.PingAsync(host.AppId, Ct);
        await AsyncAssert.EventuallyAsync(() => host.Devices.Connections[host.Device.Id].Output.ChannelA == 20);
        await host.Session.PauseAsync();
        Assert.All(await Task.WhenAll(pending), result => Assert.Equal("cancelled", result.GetProperty("reason").GetString()));
        await host.Controller.ClearAsync(host.AppId, cancellationToken: Ct).WaitAsync(TimeSpan.FromSeconds(2), Ct);
        await host.Controller.ResetIntensityAsync(host.AppId, host.SlotId, 0, cancellationToken: Ct);
        var pausedState = await host.Controller.GetDevicesAsync(host.AppId, Ct);
        Assert.True(pausedState.GetProperty("devices")[0].GetProperty("slotState").GetProperty("channelA").GetProperty("isMuted").GetBoolean());
        await host.Session.PauseAsync();
        await host.Session.FlushAsync();
        Assert.Equal(PipelineState.Running, host.Session.State);
        Assert.Equal(0, host.Devices.Connections[host.Device.Id].Output.ChannelA);
        await host.Session.StopAsync();
        await host.Session.StartAsync(host.Plan);
        Assert.Equal(0, host.Devices.Connections[host.Device.Id].Output.ChannelA);
    }

    [Fact]
    public async Task FiniteTaskLongerThanDefaultRpcTimeoutReturnsItsCompletion()
    {
        await using var host = await DgLabV4TestHost.CreateAsync();
        var result = await host.Controller.SetTemporaryIntensityAsync(host.AppId, host.SlotId, 0, 0, new(Duration: 8300), Ct);
        Assert.Equal("completed", result.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task InvalidClearAndOperateParametersReturnProtocolErrorsWithoutClosingTheApp()
    {
        await using var host = await DgLabV4TestHost.CreateAsync();
        foreach (object data in new object[] { RemoteProtocol.Element<object?>(null), new { s = (string?)null }, new { c = 0 }, new { s = host.SlotId, c = 1.5 } })
        {
            var error = await Assert.ThrowsAsync<DgLabV4Exception>(() => host.Peer.RequestAsync(host.AppId, "device.op.clear", data, cancellationToken: Ct));
            Assert.Equal("invalid_params", error.Code);
        }
        var missing = await Assert.ThrowsAsync<DgLabV4Exception>(() => host.Peer.RequestAsync(host.AppId, "device.op", new { t = 3, c = 0, v = 0 }, cancellationToken: Ct));
        Assert.Equal("slot_not_found", missing.Code);
        await host.Controller.PingAsync(host.AppId, Ct);
    }

    [Fact]
    public async Task FailedAppRegistrationReleasesItsPoolReferenceAndAllowsRestart()
    {
        await using var host = await DgLabV4TestHost.CreateAsync();
        await host.Session.StopAsync();
        var builder = new PipelineBuilder("Invalid invitation");
        builder.Add(DgLabV4Nodes.App, new DgLabV4AppNode(host.Device.Id, host.Url.ToString(), "missing"));
        var plan = new PipelineCompiler(RemotePipelineNodes.Register(BuiltInNodes.CreateRegistry())).Compile(builder.Build(), [],
            new Dictionary<string, Core.DeviceDescriptor> { [host.Device.Id] = host.Device });
        var error = await Assert.ThrowsAsync<DgLabV4Exception>(() => host.Session.StartAsync(plan));
        Assert.Equal("controller_not_found", error.Code);
        Assert.Equal(PipelineState.Idle, host.Session.State);
        Assert.Null(host.Devices.DeviceOwner(host.Device.Id));
        await host.Session.StartAsync(host.Plan);
        Assert.Equal(PipelineState.Running, host.Session.State);
        Assert.Equal(host.Session.OwnerId, host.Devices.DeviceOwner(host.Device.Id));
    }

    [Fact]
    public async Task CancelledOrInvalidWaitSettingsDoNotSendAnOperation()
    {
        await using var host = await DgLabV4TestHost.CreateAsync();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.Controller.SetTemporaryIntensityAsync(host.AppId, host.SlotId, 0, 80, cancellationToken: cancelled.Token));
        await Assert.ThrowsAsync<LocalizedArgumentException>(() => host.Controller.SetTemporaryIntensityAsync(host.AppId, host.SlotId, 0, 80, new(Timeout: TimeSpan.FromMilliseconds(-2)), Ct));
        await host.Controller.PingAsync(host.AppId, Ct);
        await host.Session.FlushAsync();
        Assert.Equal(0, host.Devices.Connections[host.Device.Id].Output.ChannelA);
    }

    [Fact]
    public async Task ImmediateOperationCanReplaceAFullAppQueue()
    {
        await using var host = await DgLabV4TestHost.CreateAsync();
        var pending = Enumerable.Range(0, 256).Select(_ => host.Controller.SetTemporaryIntensityAsync(host.AppId, host.SlotId, 0, 0, cancellationToken: Ct)).ToArray();
        var nodeId = Guid.ParseExact(host.SlotId, "N");
        await AsyncAssert.EventuallyAsync(() => host.Session.NodeStatus.GetValueOrDefault(nodeId)?.Count == 256);
        var replacement = host.Controller.SetTemporaryIntensityAsync(host.AppId, host.SlotId, 0, 0, new(Duration: 100, Immediate: true), Ct);
        Assert.All(await Task.WhenAll(pending), result => Assert.Equal("replaced", result.GetProperty("reason").GetString()));
        Assert.Equal("completed", (await replacement).GetProperty("reason").GetString());
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
