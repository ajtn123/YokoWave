using YokoWave.Core;
using YokoWave.Core.Bluetooth;
using YokoWave.Tests.Support.Bluetooth;

namespace YokoWave.Tests.Devices;

public sealed class DeviceControllerTests
{
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
        await using var controller = new DeviceController(transport);
        await controller.ConnectAsync(Device("one"), cancellationToken: TestContext.Current.CancellationToken);
        await controller.ConnectAsync(Device("two"), cancellationToken: TestContext.Current.CancellationToken);
        await controller.SendAsync("two", new(25, 15, 20), TestContext.Current.CancellationToken);
        transport.FailId = "one";
        try
        {
            await Assert.ThrowsAnyAsync<AggregateException>(() => controller.StopAllAsync(TestContext.Current.CancellationToken));
            Assert.Equal(0, transport.Latest["two"].ChannelA);
        }
        finally
        {
            transport.FailId = null;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidWindowIsRejectedBeforeAnyDeviceWrite(bool missingSample)
    {
        var adapter = new HardwareGattAdapter(BluetoothProtocol.CoyoteV3);
        await using var transport = new BluetoothDeviceTransport(adapter);
        var device = Assert.Single(await transport.ScanAsync(TestContext.Current.CancellationToken));
        await transport.ConnectAsync(device, TestContext.Current.CancellationToken);
        int before = adapter.Links[device.Id].Writes.Count;
        WaveformSample[] window = missingSample ? [new(1, 20), null!, new(1, 20), new(1, 20)] : [new(1, 20)];
        var frame = new OutputFrame(20, 20, 20, WindowA: window);
        await Assert.ThrowsAnyAsync<ArgumentException>(() => transport.SendAsync(device.Id, frame, TestContext.Current.CancellationToken));
        Assert.ThrowsAny<ArgumentException>(() => DgLabProtocol.V3(frame));
        Assert.Equal(before, adapter.Links[device.Id].Writes.Count);
    }

    private static DeviceDescriptor Device(string id = "one", DeviceKind kind = DeviceKind.DualChannel) => new(id, id, kind, 90, -40);
}
