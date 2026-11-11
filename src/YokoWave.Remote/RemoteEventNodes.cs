using YokoWave.Core;
using YokoWave.Pipelines;

namespace YokoWave.Remote;

public sealed record EventServerNode(RemoteServerSettings Settings);
public sealed record EventInputNode(string ClientId = "", string EventId = "");

public static class RemoteEventNodes
{
    public const string Server = "network.event.server";
    public const string Input = "network.event.input";
    public static PipelineTemplate Template { get; } = new("event.server", "Pipeline.Node.EventServer", parameters =>
    {
        var graph = new PipelineBuilder(parameters.Name);
        var server = graph.Add(Server, new EventServerNode(new()));
        var input = graph.Add(Input, new EventInputNode());
        graph.Connect(server, "events", input, "events");
        return graph.Build();
    });

    public static NodeTypeRegistry Register(NodeTypeRegistry registry) => registry
        .Register(new NodeRegistration<EventServerNode>(Server, "Pipeline.Node.EventServer", [new("events", "Pipeline.Port.Events", PortDirection.Output, Kind: PortValueKind.Event)],
            configuration => Valid(configuration.Settings), (_, _) => new EventEndpointProcessor(), service: true,
            participant: (context, configuration) => new EventServerParticipant(context, configuration.Settings)))
        .Register(new NodeRegistration<EventInputNode>(Input, "Pipeline.Node.EventInput",
            [new("events", "Pipeline.Port.Events", PortDirection.Input, Kind: PortValueKind.Event),
             NodePort.Output("trigger", "Pipeline.Port.trigger"), NodePort.Output("value", "Pipeline.Port.value"), NodePort.Output("count", "Pipeline.Port.EventCount")],
            configuration => (configuration.ClientId is { Length: 0 } || RemoteEventProtocol.ValidKey(configuration.ClientId)) &&
                (configuration.EventId is { Length: 0 } || RemoteEventProtocol.ValidKey(configuration.EventId)),
            (configuration, _) => new RemoteEventProcessor(configuration)));

    private static bool Valid(RemoteServerSettings? settings)
    {
        if (settings is null)
            return false;
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

    private sealed class EventEndpointProcessor : ISignalProcessor
    {
        public void Evaluate(NodeEvaluationContext context)
        {
            context.EventOutput("events", context.Inputs.Events.GetValueOrDefault(context.NodeId, EventSignal.Empty));
            context.Status(new("Tracking"));
        }
    }
}
