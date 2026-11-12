using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using YokoWave.Core.Bluetooth;
using YokoWave.Tests.Support.Remote;

namespace YokoWave.Tests.Remote;

public sealed class RpcProtocolTests
{
    [Fact]
    public async Task FragmentedJsonIsAcceptedButDuplicateRequestIdsNeverExecuteAgain()
    {
        await using var host = await RemoteTestHost.CreateAsync(BluetoothProtocol.CoyoteV3);
        using var socket = new ClientWebSocket();
        socket.Options.AddSubProtocol(RemoteProtocol.SubProtocol);
        await socket.ConnectAsync(host.Server.Url!, Ct);
        byte[] packet = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = "1", method = "system.authenticate", @params = new { token = RemoteTestHost.Token } }));
        await socket.SendAsync(packet.AsMemory(0, 10), WebSocketMessageType.Text, false, Ct);
        await socket.SendAsync(packet.AsMemory(10), WebSocketMessageType.Text, true, Ct);
        Assert.Contains("\"result\":true", await Receive(socket));
        await socket.SendAsync(packet, WebSocketMessageType.Text, true, Ct);
        Assert.Contains("Error.RemoteSequence", await Receive(socket));
    }

    [Fact]
    public void AllTypedCommandsAndPawTriggersRoundTripThroughTheWireFormat()
    {
        DeviceCommand[] commands = [new QueryDevice(), new ConfigureMotionStream(true), new ControlStepCounter(StepCounterAction.Reset), new ConfigureSensorStream(true), new ConfigureIndicator(2), new ConfigureShoulderLight(2, 3), new CalibratePawAngles(), new ResetPawParameters(), new ConfigurePressureSensor(true), new SynchronizeDisplay(), new ConfigureLight(false)];
        PawTrigger[] triggers = [new PawNoTrigger(), new PawLiveData(), new PawRandomReaction(), new PawProbability([new(1, 100)]), new PawVoltage(), new PawButtonMotion()];
        foreach (var command in commands.Concat(triggers.Select(trigger => (DeviceCommand)new ConfigurePaw(trigger))))
        {
            var json = RemoteProtocol.Element(command);
            var restored = RemoteProtocol.Read<DeviceCommand>(json);
            Assert.Equal(command.GetType(), restored.GetType());
            restored.Validate();
            Assert.Equal(json.GetRawText(), RemoteProtocol.Element(restored).GetRawText());
        }
    }

    private static async Task<string> Receive(ClientWebSocket socket)
    {
        byte[] bytes = new byte[4096];
        var result = await socket.ReceiveAsync(bytes.AsMemory(), Ct);
        return Encoding.UTF8.GetString(bytes, 0, result.Count);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
