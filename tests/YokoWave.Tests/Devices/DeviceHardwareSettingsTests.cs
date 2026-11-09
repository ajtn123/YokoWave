using YokoWave.Core;
using YokoWave.Core.Bluetooth;
using YokoWave.Pipelines;

namespace YokoWave.Tests.Devices;

public sealed class DeviceHardwareSettingsTests
{
    [Fact]
    public async Task DeviceHardwareSettingsApplyToEveryFrameAndDoNotAffectOtherDevices()
    {
        var transport = new RecordingTransport();
        await using var controller = new DeviceController(transport);
        var one = new DeviceDescriptor("one", "one", DeviceKind.DualChannel, 80, -40, ProtocolId: nameof(BluetoothProtocol.CoyoteV3));
        var two = one with
        {
            Id = "two",
            Name = "two"
        };
        var settings = new DeviceOutputSettings(one.Id, true, new(35, 75, RelativeStrength: true), new(200, 100, 90, 80));
        controller.LoadOutputSettings([settings]);
        await controller.ConnectAsync(one, TestContext.Current.CancellationToken);
        await controller.ConnectAsync(two, TestContext.Current.CancellationToken);
        Assert.Equal(OutputFrame.Zero, transport.Latest[one.Id]);
        await controller.SendAsync(one.Id, new(90, 90, 20), TestContext.Current.CancellationToken);
        await controller.SendAsync(two.Id, new(90, 90, 20), TestContext.Current.CancellationToken);
        Assert.Equal(35, transport.Latest[one.Id].ChannelA);
        Assert.Equal(75, transport.Latest[one.Id].ChannelB);
        Assert.True(transport.Latest[one.Id].Gentle);
        Assert.Equal(settings.Balances, transport.Latest[one.Id].DgBalances);
        Assert.Equal(90, transport.Latest[two.Id].ChannelA);
        Assert.False(transport.Latest[two.Id].Gentle);
        var graph = new PipelineBuilder("limited");
        var level = graph.Add(BuiltInNodes.Constant, new ConstantNode(.9));
        var sink = graph.Add(BuiltInNodes.ChannelOutput, new ChannelOutputNode(one.Id, Limit: 1));
        graph.Connect(level, "value", sink, "strength");
        var plan = new PipelineCompiler(BuiltInNodes.CreateRegistry()).Compile(graph.Build(), [], new Dictionary<string, DeviceDescriptor> { [one.Id] = one });
        await using var session = new PipelineSession(controller);
        SessionRecord? record = null;
        session.Completed += (_, completed) => record = completed;
        await session.StartAsync(plan);
        await session.StopAsync();
        Assert.Equal(35, record!.PeakA);
    }

    [Fact]
    public async Task RememberedAuxiliaryMotorStaysOffDuringConnectionConfigurationAndStops()
    {
        var transport = new RecordingTransport();
        await using var controller = new DeviceController(transport);
        var device = new DeviceDescriptor("aux", "aux", DeviceKind.DualChannel, 80, -40, ProtocolId: nameof(BluetoothProtocol.YokonexV2));
        var auxiliary = Enum.GetValues<AuxiliaryMotorMode>().First(m => m != AuxiliaryMotorMode.Off);
        var settings = new DeviceOutputSettings(device.Id, Hardware: new(AuxiliaryMotor: auxiliary));
        controller.LoadOutputSettings([settings]);
        await controller.ConnectAsync(device, TestContext.Current.CancellationToken);
        Assert.Equal(OutputFrame.Zero, transport.Latest[device.Id]);
        await controller.SetOutputSettingsAsync(device, settings, TestContext.Current.CancellationToken);
        Assert.Equal(OutputFrame.Zero, transport.Latest[device.Id]);
        await controller.SendAsync(device.Id, new(0, 0, 20), TestContext.Current.CancellationToken);
        Assert.Equal(auxiliary, transport.Latest[device.Id].Hardware!.AuxiliaryMotor);
        await controller.StopAllAsync(TestContext.Current.CancellationToken);
        Assert.Equal(OutputFrame.Zero, transport.Latest[device.Id]);
    }
}
