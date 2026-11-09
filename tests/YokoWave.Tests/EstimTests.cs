using System.Buffers.Binary;
using YokoWave.Core;
using YokoWave.Core.Bluetooth;
using Xunit;

namespace YokoWave.Tests;

public sealed class EstimTests
{
    private static WaveformPreset Wave(string name, double period = 1, double frequency = 20, double pulse = 30) =>
        new(Guid.NewGuid(), name, WaveShape.Sampled, period, frequency,
            Samples: [new(1, frequency, pulse / 100), new(1, frequency + 1, (pulse + 1) / 100)]);
    private static async Task<DeviceController> Controller(RecordingTransport transport)
    {
        var controller = new DeviceController(transport);
        await controller.ConnectAsync(new("one", "one", DeviceKind.DualChannel, 80, -40), TestContext.Current.CancellationToken);
        return controller;
    }

    [Fact]
    public async Task DeviceLightRequiresVerifiedFirmwareAndWaitsForConfirmedState()
    {
        var adapter = new FakeBluetoothAdapter();
        await using var controller = new DeviceController(new BluetoothDeviceTransport(adapter));
        var device = Assert.Single(await controller.ScanAsync(TestContext.Current.CancellationToken));
        await controller.ConnectAsync(device, ct: TestContext.Current.CancellationToken);
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
        await controller.ConnectAsync(device, ct: TestContext.Current.CancellationToken);
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
        var v2 = V2Protocol.Encode(gentle);
        Assert.Equal(180, BinaryPrimitives.ReadUInt16BigEndian(v1[0].AsSpan(4, 2)));
        Assert.Equal(90, BinaryPrimitives.ReadUInt16BigEndian(v1[1].AsSpan(4, 2)));
        Assert.Equal(180, BinaryPrimitives.ReadUInt16BigEndian(v2.AsSpan(3, 2)));
        Assert.Equal(90, BinaryPrimitives.ReadUInt16BigEndian(v2.AsSpan(7, 2)));
        Assert.Equal(V2Protocol.Stop(), V2Protocol.Encode(OutputFrame.Zero with
        {
            Gentle = true
        }));
    }
}
