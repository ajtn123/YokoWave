using System.Security.Cryptography;
using YokoWave.Core;
using YokoWave.Core.Bluetooth;
using YokoWave.Pipelines;

namespace YokoWave.Remote;

public sealed record GattServerNode(string[] DeviceIds, RemoteServerSettings Settings);
public sealed record ControlServerNode(string DeviceId, RemoteServerSettings Settings);

public static class RemotePipelineNodes
{
    public const string GattServer = "network.gatt.server";
    public const string ControlServer = "network.control.server";
    public static PipelineTemplate GattTemplate { get; } = new("gatt.server", "Pipeline.Node.GattServer", p =>
    {
        var builder = new PipelineBuilder(p.Name, p.Limit);
        builder.Add(GattServer, new GattServerNode([p.DeviceId], new()));
        return builder.Build();
    });
    public static PipelineTemplate ControlTemplate { get; } = new("control.server", "Pipeline.Node.ControlServer", p =>
    {
        var builder = new PipelineBuilder(p.Name, p.Limit);
        var source = builder.Add(ControlServer, new ControlServerNode(p.DeviceId, new(ControlLimit: p.Limit * 100)));
        var sink = builder.Add(BuiltInNodes.RouterOutput, new RouterOutputNode(p.DeviceId, p.Limit));
        foreach (var port in BuiltInNodes.FramePorts())
            builder.Connect(source, port.Id, sink, port.Id);
        return builder.Build();
    });
    public static NodeTypeRegistry Register(NodeTypeRegistry registry) => RemoteEventNodes.Register(DgLab.V4.DgLabV4Nodes.Register(RegisterYokoWave(registry)));
    private static NodeTypeRegistry RegisterYokoWave(NodeTypeRegistry registry) => registry
        .Register(new NodeRegistration<GattServerNode>(GattServer, "Pipeline.Node.GattServer", [],
            c => c.DeviceIds is { Length: > 0 and <= 32 } && c.DeviceIds.All(id => !string.IsNullOrWhiteSpace(id)) && Valid(c.Settings),
            (_, _) => new EndpointProcessor(), resources: c => c.DeviceIds.Select(id => new DeviceOutputBinding(id, OutputBindingKind.Export)).ToArray(),
            participant: (context, c) => new EndpointParticipant(context, c.DeviceIds, c.Settings, RemoteLeaseType.Gatt)))
        .Register(new NodeRegistration<ControlServerNode>(ControlServer, "Pipeline.Node.ControlServer", BuiltInNodes.FramePorts(),
            c => !string.IsNullOrWhiteSpace(c.DeviceId) && Valid(c.Settings),
            (_, _) => new FrameSignalProcessor(), c => new(c.DeviceId, OutputBindingKind.ControlEndpoint),
            participant: (context, c) => new EndpointParticipant(context, [c.DeviceId], c.Settings, RemoteLeaseType.Control)));
    private static bool Valid(RemoteServerSettings settings)
    {
        try
        {
            settings.Validate();
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
    private sealed class EndpointProcessor : ISignalProcessor
    {
        public void Evaluate(NodeEvaluationContext context) => context.Status(new("Tracking"));
    }
    private sealed class EndpointParticipant(NodeRunContext context, string[] deviceIds, RemoteServerSettings settings,
        RemoteLeaseType type) : IRunParticipant, IOutputResource
    {
        private RemoteServer? _server;
        private DeviceController? _proxy;
        private BufferedControlTransport? _buffer;
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            if (type == RemoteLeaseType.Control)
            {
                _buffer = new(context.Devices, context.OwnerId, deviceIds);
                _proxy = new(_buffer, context.Devices.Clock);
                foreach (string id in deviceIds)
                    await _proxy.ConnectAsync(context.Devices.Connections[id].Device, cancellationToken).ConfigureAwait(false);
                await _proxy.AcquireTargetsAsync(context.OwnerId, deviceIds, cancellationToken, allowInputs: true).ConfigureAwait(false);
            }
            var credential = context.Services.Credentials.GetOrAdd(context.NodeId, _ => new(Convert.ToHexString(RandomNumberGenerator.GetBytes(32))));
            var session = new RemoteExportSession(context.NodeId, context.Devices, deviceIds, settings,
                reservationOwner: context.OwnerId, controlDevices: _proxy, allowedLease: type);
            _server = new(session, credential.Token);
            await _server.StartAsync(credential.CertificatePassword, cancellationToken).ConfigureAwait(false);
            context.Services.Endpoints[context.NodeId] = new(_server.Url!.ToString(), type.ToString().ToLowerInvariant());
        }
        public ValueTask CaptureAsync(PipelineInputs inputs, CancellationToken cancellationToken)
        {
            if (_server?.Session.IsRunning != true)
                throw new Core.Localization.LocalizedOperationException("Error.RemoteStopped");
            if (_buffer is not null)
                inputs.Frames[context.NodeId] = _buffer.Latest;
            return ValueTask.CompletedTask;
        }
        public Task SetPausedAsync(bool paused) => _server?.Session.SetPausedAsync(paused) ?? Task.CompletedTask;
        public async Task StopOutputsAsync()
        {
            if (_server is not null)
            {
                await _server.Session.StopAsync().ConfigureAwait(false);
                await _server.DisposeAsync().ConfigureAwait(false);
                _server = null;
            }
            context.Services.Endpoints.TryRemove(context.NodeId, out _);
        }
        public async ValueTask DisposeAsync()
        {
            await StopOutputsAsync().ConfigureAwait(false);
            if (_proxy is not null)
                await _proxy.DisposeAsync().ConfigureAwait(false);
        }
    }
}

internal sealed class BufferedControlTransport(DeviceController physical, Guid owner, string[] ids) : IDeviceTransport, IDeviceCommandTransport, IElectricalMotionSource
{
    private OutputFrame _latest = OutputFrame.Zero;
    public OutputFrame Latest => Volatile.Read(ref _latest);
    public bool IsSimulation => false;
    public string DisplayName => Core.Localization.L10n.Get("Pipeline.Title");
    public Task<IReadOnlyList<DeviceDescriptor>> ScanAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<DeviceDescriptor>>(ids.Select(id => physical.Connections[id].Device).ToArray());
    public Task ConnectAsync(DeviceDescriptor device, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task SendAsync(string deviceId, OutputFrame frame, CancellationToken cancellationToken = default)
    {
        Volatile.Write(ref _latest, frame);
        return Task.CompletedTask;
    }
    public Task DisconnectAsync(string deviceId, CancellationToken cancellationToken = default) => SendAsync(deviceId, OutputFrame.Zero, cancellationToken);
    public Task ExecuteCommandAsync(string deviceId, DeviceCommand command, CancellationToken cancellationToken = default) => physical.ExecuteCommandAsync(deviceId, command, owner, cancellationToken);
    public Task SetMotionStreamingAsync(string id, bool enabled, CancellationToken cancellationToken = default) => physical.ExecuteCommandAsync(id, new ConfigureMotionStream(enabled), owner, cancellationToken);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
