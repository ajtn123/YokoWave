using YokoWave.Core;
using YokoWave.Core.Bluetooth;
using YokoWave.Core.Localization;
using YokoWave.Tests.Support.Bluetooth;

namespace YokoWave.Tests.Devices;

public sealed class DeviceConcurrencyTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(3, 2)]
    [InlineData(4, 1)]
    [InlineData(5, 2)]
    [InlineData(6, 2)]
    [InlineData(7, 3)]
    public void SparseMasksReportCountsAndRejectInvalidChannelIndices(int mask, int count)
    {
        var device = new DeviceDescriptor("one", "motor", DeviceKind.Vibration, 80, -40, ChannelMask: mask);
        Assert.Equal(count, device.ChannelCount);
        for (int index = 0; index < 3; index++)
            Assert.Equal((mask & (1 << index)) != 0, device.HasChannel(index));
        Assert.False(device.HasChannel(-1));
        Assert.False(device.HasChannel(3));
        Assert.False(device.HasChannel(32));
    }

    [Fact]
    public async Task SparseMotorOutputCannotEnableAnAbsentChannel()
    {
        var transport = new ConcurrentTransport();
        await using var controller = new DeviceController(transport);
        await controller.ConnectAsync(new("one", "motor", DeviceKind.Vibration, 80, -40, ChannelMask: 4), TestContext.Current.CancellationToken);
        await controller.SendAsync("one", new(50, 50, 20, ChannelC: 30), TestContext.Current.CancellationToken);
        Assert.Equal(0, transport.Outputs["one"].ChannelA);
        Assert.Equal(0, transport.Outputs["one"].ChannelB);
        Assert.Equal(30, transport.Outputs["one"].ChannelC);
    }

    [Fact]
    public async Task BlockedDeviceDoesNotDelayOtherDevicesOutputStopOrStatus()
    {
        var transport = new ConcurrentTransport();
        await using var controller = new DeviceController(transport);
        await ConnectAsync(controller, "slow");
        await ConnectAsync(controller, "fast");
        var slowOwner = Guid.NewGuid();
        var fastOwner = Guid.NewGuid();
        await controller.AcquireTargetsAsync(slowOwner, ["slow"], cancellationToken: TestContext.Current.CancellationToken);
        await controller.AcquireTargetsAsync(fastOwner, ["fast"], cancellationToken: TestContext.Current.CancellationToken);
        transport.BlockedId = "slow";
        var blocked = controller.SendOwnedAsync(slowOwner, "slow", new(20, 0, 20), cancellationToken: TestContext.Current.CancellationToken);
        try
        {
            await transport.Blocked.Task.WaitAsync(_deadline, TestContext.Current.CancellationToken);
            await controller.SendOwnedAsync(fastOwner, "fast", new(30, 0, 20), cancellationToken: TestContext.Current.CancellationToken).WaitAsync(_deadline, TestContext.Current.CancellationToken);
            transport.Report(new("fast", battery: 42));
            Assert.Equal(42, controller.Connections["fast"].Device.Battery);
            await controller.StopOwnedAsync(fastOwner, cancellationToken: TestContext.Current.CancellationToken).WaitAsync(_deadline, TestContext.Current.CancellationToken);
            Assert.Equal(OutputFrame.Zero, transport.Outputs["fast"]);
            Assert.Null(controller.DeviceOwner("fast"));
            Assert.False(blocked.IsCompleted);
        }
        finally
        {
            transport.Release.TrySetResult();
            await blocked;
        }

        await controller.StopOwnedAsync(slowOwner, cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task OverlappingClaimsInOppositeOrdersCannotDeadlockOrClaimOnlyPartOfTheTargets()
    {
        var transport = new ConcurrentTransport();
        await using var controller = new DeviceController(transport);
        await ConnectAsync(controller, "slow");
        await ConnectAsync(controller, "fast");
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        transport.BlockAllWrites = true;
        var acquireFirst = controller.AcquireTargetsAsync(first, ["slow", "fast"], cancellationToken: TestContext.Current.CancellationToken);
        await transport.Blocked.Task.WaitAsync(_deadline, TestContext.Current.CancellationToken);
        var acquireSecond = controller.AcquireTargetsAsync(second, ["fast", "slow"], cancellationToken: TestContext.Current.CancellationToken);
        transport.Release.TrySetResult();
        await acquireFirst.WaitAsync(_deadline, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<LocalizedOperationException>(() => acquireSecond.WaitAsync(_deadline, TestContext.Current.CancellationToken));
        Assert.Equal(first, controller.DeviceOwner("slow"));
        Assert.Equal(first, controller.DeviceOwner("fast"));
        await controller.StopOwnedAsync(first, cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CancelledMultiDeviceLockReleasesTheDevicesItAlreadyAcquired()
    {
        var transport = new ConcurrentTransport();
        await using var controller = new DeviceController(transport);
        await ConnectAsync(controller, "fast");
        await ConnectAsync(controller, "slow");
        transport.BlockedId = "slow";
        var write = controller.SendAsync("slow", new(20, 0, 20), TestContext.Current.CancellationToken);
        await transport.Blocked.Task.WaitAsync(_deadline, TestContext.Current.CancellationToken);
        try
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            var acquire = controller.AcquireTargetsAsync(Guid.NewGuid(), ["fast", "slow"], cancellation.Token);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => acquire);
            await controller.SendAsync("fast", new(25, 0, 20), TestContext.Current.CancellationToken).WaitAsync(_deadline, TestContext.Current.CancellationToken);
            Assert.Null(controller.DeviceOwner("fast"));
            Assert.Equal(25, transport.Outputs["fast"].ChannelA);
        }
        finally
        {
            transport.Release.TrySetResult();
            await write;
        }
    }

    [Fact]
    public async Task DisposalDrainsAnInflightWriteAndQueuedNotificationsWithoutReenablingOutput()
    {
        var transport = new ConcurrentTransport();
        var clock = new ManualInputClock();
        var controller = new DeviceController(transport, clock);
        await ConnectAsync(controller, "slow");
        transport.BlockedId = "slow";
        var write = controller.SendAsync("slow", new(20, 0, 20), cancellationToken: TestContext.Current.CancellationToken);
        await transport.Blocked.Task.WaitAsync(_deadline, TestContext.Current.CancellationToken);
        transport.Report(new("slow", motionAngle: 45));
        var disposal = controller.DisposeAsync().AsTask();
        Assert.False(disposal.IsCompleted);
        transport.Release.TrySetResult();
        await write;
        await disposal.WaitAsync(_deadline, TestContext.Current.CancellationToken);
        await controller.DisposeAsync();
        Assert.Empty(controller.Connections);
        Assert.Equal(OutputFrame.Zero, transport.Outputs["slow"]);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => controller.SendAsync("slow", new(20, 0, 20), cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ThrowingEventSubscribersDoNotHideStatusOrFaultsFromOtherSubscribers()
    {
        var transport = new ConcurrentTransport();
        await using var controller = new DeviceController(transport);
        await ConnectAsync(controller, "fast");
        int changes = 0;
        int faults = 0;
        controller.Changed += (_, _) => throw new InvalidOperationException("change subscriber");
        controller.Changed += (_, _) => changes++;
        controller.Faulted += (_, _) => throw new InvalidOperationException("fault subscriber");
        controller.Faulted += (_, _) => faults++;
        transport.Report(new("fast", battery: 25));
        transport.ReportFault(new("fast", false, new IOException("test fault")));
        Assert.Equal(25, controller.Connections["fast"].Device.Battery);
        Assert.Equal(2, changes);
        Assert.Equal(1, faults);
    }

    [Fact]
    public async Task TargetCapabilitiesRejectUnrelatedFeaturesEvenWhenTheTransportImplementsThem()
    {
        var transport = new ConcurrentTransport();
        await using var controller = new DeviceController(transport);
        await controller.ConnectAsync(new("fast", "coyote", DeviceKind.DualChannel, 80, -40, BluetoothProtocol.CoyoteV3.ToString()), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<LocalizedOperationException>(() => controller.RefreshFeaturesAsync("fast", cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<LocalizedOperationException>(() => controller.SetLightAsync("fast", true, cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<LocalizedOperationException>(() => controller.ResetInputAsync("fast", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(0, transport.FeatureCalls);
    }

    private static readonly TimeSpan _deadline = TimeSpan.FromSeconds(3);

    private static Task ConnectAsync(DeviceController controller, string id) => controller.ConnectAsync(new(id, id, DeviceKind.DualChannel, 80, -40), TestContext.Current.CancellationToken);
}
