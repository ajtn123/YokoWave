using System.Text.Json;
using YokoWave.Core;
using YokoWave.Core.Bluetooth;
using YokoWave.Pipelines;
using YokoWave.Tests.Support.Bluetooth;
using static YokoWave.Tests.Support.Pipelines.PipelineTestGraphs;

namespace YokoWave.Tests.Waveforms;

public sealed class DeviceCompatibilityTests
{
    [Theory]
    [InlineData(BluetoothProtocol.YokonexV1)]
    [InlineData(BluetoothProtocol.YokonexV2)]
    [InlineData(BluetoothProtocol.YokonexMotor)]
    [InlineData(BluetoothProtocol.CoyoteV2)]
    [InlineData(BluetoothProtocol.CoyoteV3)]
    [InlineData(BluetoothProtocol.Opossum)]
    public async Task EveryDefaultWaveformFlowsThroughEveryElectricalAndMotorDriver(BluetoothProtocol protocol)
    {
        var adapter = new HardwareGattAdapter(protocol);
        await using var controller = new DeviceController(new BluetoothDeviceTransport(adapter));
        var device = Assert.Single(await controller.ScanAsync(TestContext.Current.CancellationToken));
        await controller.ConnectAsync(device, TestContext.Current.CancellationToken);
        foreach (var waveform in WaveformAsset.BuiltIn)
        {
            string saved = JsonSerializer.Serialize(waveform);
            var graph = PipelineTemplates.Create("waveform", new("wave", device.Id, Waveform: waveform, Channels: device.ChannelCount));
            var runtime = new PipelineRuntime(new PipelineCompiler(BuiltInNodes.CreateRegistry()).Compile(graph, WaveformAsset.BuiltIn, new Dictionary<string, DeviceDescriptor> { [device.Id] = device }), new HashSet<string>());
            foreach (var node in graph.Nodes.Where(n => n.Type == BuiltInNodes.Constant))
                runtime.SetConstant(node.Id, .4);
            var frame = runtime.Evaluate(TimeSpan.FromSeconds(waveform.PeriodSeconds / 2), EvaluationTime).Outputs[device.Id];
            Assert.Equal(4, frame.WindowA!.Count);
            await controller.SendAsync(device.Id, frame, TestContext.Current.CancellationToken);
            Assert.Equal(saved, JsonSerializer.Serialize(waveform));
            Assert.InRange(controller.Connections[device.Id].Output.ChannelA, 0, 40);
        }

        await controller.StopAllAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, controller.Connections[device.Id].Output.ChannelA);
    }
}
