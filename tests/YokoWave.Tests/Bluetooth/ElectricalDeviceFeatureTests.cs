using System.Buffers.Binary;
using YokoWave.Core;
using YokoWave.Core.Bluetooth;
using YokoWave.Tests.Support.Bluetooth;

namespace YokoWave.Tests.Bluetooth;

public sealed class ElectricalDeviceFeatureTests
{
    [Fact]
    public async Task DeviceLightRequiresVerifiedFirmwareAndWaitsForConfirmedState()
    {
        var adapter = new FakeBluetoothAdapter();
        await using var controller = new DeviceController(new BluetoothDeviceTransport(adapter));
        var device = Assert.Single(await controller.ScanAsync(TestContext.Current.CancellationToken));
        await controller.ConnectAsync(device, cancellationToken: TestContext.Current.CancellationToken);
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
        await using var controller = new DeviceController(new BluetoothDeviceTransport(adapter));
        var device = Assert.Single(await controller.ScanAsync(TestContext.Current.CancellationToken));
        await controller.ConnectAsync(device, cancellationToken: TestContext.Current.CancellationToken);
        var link = adapter.Links[device.Id];
        link.ReplyToFeatures = false;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => controller.RefreshFeaturesAsync(device.Id, cancellation.Token));
        Assert.Null(controller.Connections[device.Id].HardwareVersion);
        Assert.Null(controller.Connections[device.Id].LightEnabled);
        Assert.False(link.Closed);
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => controller.SetLightAsync(device.Id, true, TestContext.Current.CancellationToken));
        Assert.DoesNotContain(link.Writes, packet => packet[1] == 0x15);
    }

    [Fact]
    public void GentleModeUsesLowerWireStrengthForBothGenerationsAndPreservesZero()
    {
        var gentle = new OutputFrame(100, 50, 20, Gentle: true);
        var v1 = V1Protocol.Encode(gentle);
        byte[] v2 = V2Protocol.Encode(gentle);
        Assert.Equal(180, BinaryPrimitives.ReadUInt16BigEndian(v1[0].AsSpan(4, 2)));
        Assert.Equal(90, BinaryPrimitives.ReadUInt16BigEndian(v1[1].AsSpan(4, 2)));
        Assert.Equal(180, BinaryPrimitives.ReadUInt16BigEndian(v2.AsSpan(3, 2)));
        Assert.Equal(90, BinaryPrimitives.ReadUInt16BigEndian(v2.AsSpan(7, 2)));
        Assert.Equal(V2Protocol.Stop(), V2Protocol.Encode(OutputFrame.Zero with { Gentle = true }));
    }
}
