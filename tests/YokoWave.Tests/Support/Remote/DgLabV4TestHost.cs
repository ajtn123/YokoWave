using YokoWave.Core;
using YokoWave.Core.Bluetooth;
using YokoWave.Pipelines;
using YokoWave.Remote;
using YokoWave.Remote.DgLab.V4;
using YokoWave.Tests.Support.Bluetooth;

namespace YokoWave.Tests.Support.Remote;

internal sealed class DgLabV4TestHost : IAsyncDisposable
{
    private readonly DgLabV4Relay _relay = new(new(Port: 0));
    public DeviceController Devices { get; } = new(new BluetoothDeviceTransport(new HardwareGattAdapter(BluetoothProtocol.CoyoteV3)));
    public DgLabV4Peer Peer { get; } = new();
    public PipelineSession Session { get; }
    public DgLabV4Controller Controller { get; }
    public string AppId { get; private set; } = "";
    public string SlotId { get; private set; } = "";
    public DeviceDescriptor Device { get; private set; } = null!;
    public PipelinePlan Plan { get; private set; } = null!;
    public Uri Url => _relay.Url!;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private DgLabV4TestHost()
    {
        Session = new(Devices);
        Controller = new(Peer);
    }

    public static async Task<DgLabV4TestHost> CreateAsync()
    {
        var host = new DgLabV4TestHost();
        try
        {
            host.Device = Assert.Single(await host.Devices.ScanAsync(Ct));
            await host.Devices.ConnectAsync(host.Device, Ct);
            await host._relay.StartAsync(cancellationToken: Ct);
            await host.Peer.ConnectAsync(host._relay.Url!, Ct);
            var attached = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            host.Peer.FrameReceived += (type, frame) =>
            {
                if (type == "client_attached")
                    attached.TrySetResult(frame.GetProperty("clientId").GetString()!);
            };
            var builder = new PipelineBuilder("V4 test");
            var input = builder.Add(DgLabV4Nodes.App, new DgLabV4AppNode(host.Device.Id, host._relay.Url!.ToString(), host.Peer.ClientId));
            var output = builder.Add(BuiltInNodes.RouterOutput, new RouterOutputNode(host.Device.Id, .4));
            foreach (var port in BuiltInNodes.FramePorts())
                builder.Connect(input, port.Id, output, port.Id);
            host.SlotId = input.Id.ToString("N");
            host.Plan = new PipelineCompiler(RemotePipelineNodes.Register(BuiltInNodes.CreateRegistry())).Compile(builder.Build(), [],
                new Dictionary<string, DeviceDescriptor> { [host.Device.Id] = host.Device });
            await host.Session.StartAsync(host.Plan);
            host.AppId = await attached.Task.WaitAsync(TimeSpan.FromSeconds(3), Ct);
            return host;
        }
        catch (Exception error)
        {
            try
            {
                await host.DisposeAsync();
            }
            catch (Exception cleanup)
            {
                throw new AggregateException(error, cleanup);
            }
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        List<Exception> errors = [];
        foreach (var resource in new IAsyncDisposable[] { Session, Peer, _relay, Devices })
        {
            try
            {
                await resource.DisposeAsync();
            }
            catch (Exception error)
            {
                errors.Add(error);
            }
        }
        if (errors.Count > 0)
            throw new AggregateException(errors);
    }
}
