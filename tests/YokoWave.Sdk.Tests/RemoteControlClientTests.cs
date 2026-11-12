using YokoWave.Core;
using YokoWave.Core.Bluetooth;
using YokoWave.Core.Localization;
using YokoWave.Tests.Support.Remote;

namespace YokoWave.Sdk.Tests;

public sealed class RemoteControlClientTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task DirectClientDiscoversControlsSubscribesAndReacquiresWithoutALocalPipeline()
    {
        await using var host = await RemoteTestHost.CreateAsync(BluetoothProtocol.YokonexV1);
        await using var client = new RemoteControlClient(host.Server.Url!.ToString(), RemoteTestHost.Token);
        await client.ConnectAsync(Ct);
        Assert.True(client.IsConnected);
        var device = Assert.Single(await client.GetDevicesAsync(Ct));
        await client.AcquireAsync([device.Id], Ct);
        await client.SubscribeAsync(device.Id, Ct);
        await client.OutputAsync(new(80, 0, 20), Ct);
        await AsyncAssert.EventuallyAsync(() => host.Controller.Connections[host.Devices[0].Id].Output.ChannelA == 40);
        Assert.Equal(device.Id, (await client.GetStatusAsync(device.Id, Ct)).DeviceId);
        await client.SetStrengthAsync(10, 0, cancellationToken: Ct);
        await client.PauseAsync(Ct);
        await AsyncAssert.EventuallyAsync(() => host.Controller.Connections[host.Devices[0].Id].Output.ChannelA == 0);
        Assert.True(client.HasLease);
        await client.ResumeAsync(Ct);
        await client.UnsubscribeAsync(device.Id, Ct);
        await client.ReleaseAsync(Ct);
        Assert.False(client.HasLease);
        Assert.True(client.IsConnected);
        await client.AcquireAsync([device.Id], Ct);
        await client.StopAsync(Ct);
    }

    [Fact]
    public async Task DisposalStopsActiveOutputAndOnlyControlMethodsAreAllowed()
    {
        await using var host = await RemoteTestHost.CreateAsync(BluetoothProtocol.YokonexV1);
        await using (var client = new RemoteControlClient(host.Server.Url!.ToString(), RemoteTestHost.Token))
        {
            await client.ConnectAsync(Ct);
            await Assert.ThrowsAsync<LocalizedOperationException>(() => client.OutputAsync(OutputFrame.Zero, Ct));
            var device = Assert.Single(await client.GetDevicesAsync(Ct));
            await client.AcquireAsync([device.Id], Ct);
            await Assert.ThrowsAsync<LocalizedArgumentException>(() => client.CallAsync<bool>("gatt.write", cancellationToken: Ct));
            await client.OutputAsync(new(20, 0, 20), Ct);
            await AsyncAssert.EventuallyAsync(() => host.Controller.Connections[host.Devices[0].Id].Output.ChannelA > 0);
        }
        await AsyncAssert.EventuallyAsync(() => host.Controller.Connections[host.Devices[0].Id].Output.ChannelA == 0);
    }

    [Fact]
    public async Task FailedAuthenticationDoesNotExposeAConnectedClientOrLease()
    {
        await using var host = await RemoteTestHost.CreateAsync(BluetoothProtocol.YokonexV1);
        await using var client = new RemoteControlClient(host.Server.Url!.ToString(), "incorrect-token-with-enough-length");
        var error = await Assert.ThrowsAsync<RemoteRpcException>(() => client.ConnectAsync(Ct));
        Assert.Equal(-32001, error.Code);
        Assert.False(client.IsConnected);
        Assert.False(client.HasLease);
    }

    [Fact]
    public void ClientAssemblyHasNoServerPipelineOrUiDependency()
    {
        string?[] references = typeof(RemoteControlClient).Assembly.GetReferencedAssemblies().Select(reference => reference.Name).ToArray();
        Assert.DoesNotContain("YokoWave.Remote", references);
        Assert.DoesNotContain("YokoWave.Pipelines", references);
        Assert.DoesNotContain(references, name => name!.StartsWith("Microsoft.AspNetCore.", StringComparison.Ordinal) || name.StartsWith("Avalonia", StringComparison.Ordinal));
    }
}
