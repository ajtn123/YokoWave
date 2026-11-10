using YokoWave.Core;
using YokoWave.Pipelines;

namespace YokoWave.Remote.DgLab.V4;

public sealed record DgLabV4AppNode(string DeviceId, string Url = "ws://127.0.0.1:9998/v4", string ControllerId = "");
public sealed record DgLabV4RelayNode(DgLabV4RelaySettings Settings);

public static class DgLabV4Nodes
{
    public const string App = "network.dglab.v4.app";
    public const string Relay = "network.dglab.v4.relay";
    public static PipelineTemplate AppTemplate { get; } = new("dglab.v4.app", "DgLab.Controlled", parameters =>
    {
        var builder = new PipelineBuilder(parameters.Name, parameters.Limit);
        var input = builder.Add(App, new DgLabV4AppNode(parameters.DeviceId));
        var output = builder.Add(BuiltInNodes.RouterOutput, new RouterOutputNode(parameters.DeviceId, parameters.Limit));
        foreach (var port in BuiltInNodes.FramePorts())
            builder.Connect(input, port.Id, output, port.Id);
        return builder.Build();
    });
    public static PipelineTemplate SensorTemplate { get; } = new("dglab.v4.sensor", "DgLab.SensorSharing", parameters =>
    {
        var builder = new PipelineBuilder(parameters.Name);
        builder.Add(App, new DgLabV4AppNode(parameters.DeviceId));
        return builder.Build();
    });
    public static PipelineTemplate RelayTemplate { get; } = new("dglab.v4.relay", "DgLab.Relay", parameters =>
    {
        var builder = new PipelineBuilder(parameters.Name);
        builder.Add(Relay, new DgLabV4RelayNode(new()));
        return builder.Build();
    });
    public static NodeTypeRegistry Register(NodeTypeRegistry registry) => registry
        .Register(new NodeRegistration<DgLabV4AppNode>(App, "DgLab.Controlled",
            [.. BuiltInNodes.FramePorts(), NodePort.Input("action", "DgLab.Action", 0, 9, integer: true), NodePort.Input("trigger", "Pipeline.Port.trigger", 0, 1)],
            Valid, (_, _) => new DgLabV4InputProcessor(), configuration => new(configuration.DeviceId, OutputBindingKind.ControlEndpoint),
            participant: (context, configuration) => new DgLabV4AppParticipant(context, configuration),
            acceptsDevice: (_, device) => device.Origin == DeviceOrigin.Local && device.Simulated != true && DgLabV4Models.Supports(device)))
        .Register(new NodeRegistration<DgLabV4RelayNode>(Relay, "DgLab.Relay", [], Valid,
            (_, _) => new RelayProcessor(), participant: (context, configuration) => new DgLabV4RelayParticipant(context, configuration.Settings), service: true));

    private static bool Valid(DgLabV4AppNode configuration)
    {
        try
        {
            _ = DgLabV4Protocol.AppUri(configuration.Url, configuration.ControllerId);
            return !string.IsNullOrWhiteSpace(configuration.DeviceId);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
    private static bool Valid(DgLabV4RelayNode configuration)
    {
        try
        {
            configuration.Settings.Validate();
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private sealed class RelayProcessor : ISignalProcessor
    {
        public void Evaluate(NodeEvaluationContext context) => context.Status(new("Tracking"));
    }
}
