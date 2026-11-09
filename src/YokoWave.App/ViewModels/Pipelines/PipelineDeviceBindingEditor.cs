using YokoWave.Core;
using YokoWave.Core.Localization;
using YokoWave.Pipelines;
using YokoWave.Remote;

namespace YokoWave.App.ViewModels.Pipelines;

/// <summary>Associates devices with a graph and edits compatible resource slots without changing its topology.</summary>
internal static class PipelineDeviceBindingEditor
{
    public static bool CanAssign(PipelineEditorViewModel editor, DeviceDescriptor device) => editor.CanEdit && device.IsSupported &&
        editor.CanBindDevice?.Invoke(device.Id) != false && !editor.AssignedDeviceIds.Contains(device.Id);

    public static void Assign(PipelineEditorViewModel editor, DeviceDescriptor device)
    {
        if (!CanAssign(editor, device))
            throw new LocalizedOperationException("Pipeline.Error.Device");
        var document = editor.ToDocument();
        List<DeviceOutputBinding> claimed = [];
        NodeDefinition Bind(NodeDefinition node)
        {
            var registration = editor.Registry.Get(node.Type);
            var configuration = registration.Read(node.Configuration);
            if (configuration is GattServerNode server)
            {
                var target = new DeviceOutputBinding(device.Id, OutputBindingKind.Export);
                if (claimed.Any(binding => binding.ConflictsWith(target)))
                    return node;
                claimed.Add(target);
                var ids = server.DeviceIds.Select(id => IsEmpty(id) ? device.Id : id).Append(device.Id).Distinct(StringComparer.Ordinal).ToArray();
                return NodeDefinition.Create(node.Type, server with { DeviceIds = ids }, node.Id);
            }
            var compatible = configuration switch
            {
                NativeOutputNode native => (native.Settings ?? new()).Accepts(device),
                CommandNode command => device.Profile?.Supports(command.Command.Capability) == true,
                _ => true
            };
            var slots = registration.Resources(configuration).Where(b => compatible && IsEmpty(b.DeviceId) && PipelineCompiler.Accepts(b, device)).ToArray();
            var targets = slots.Select(binding => binding with { DeviceId = device.Id }).ToArray();
            if (targets.Any(target => claimed.Any(binding => binding.ConflictsWith(target))))
                return node;
            claimed.AddRange(targets);
            var mappings = slots.Select(b => b.DeviceId).Distinct(StringComparer.Ordinal).ToDictionary(id => id, _ => device.Id);
            return mappings.Count == 0 ? node : SubgraphExpansion.MapDevices(node, mappings);
        }
        editor.EditDocument(document with
        {
            AssignedDevices = editor.AssignedDeviceIds.Append(device.Id).ToArray(),
            Nodes = document.Nodes.Select(n => TransformResources(n, editor.Registry, Bind)).ToArray()
        });
    }

    private static bool IsEmpty(string id) => string.IsNullOrWhiteSpace(id) || id.StartsWith('$');

    public static void Detach(PipelineEditorViewModel editor, string deviceId)
    {
        if (!editor.CanEdit)
            return;
        var graph = editor.ToDocument();
        var placeholder = "$device-" + Guid.NewGuid().ToString("N")[..6];
        NodeDefinition Unbind(NodeDefinition node)
        {
            if (node.Type == RemotePipelineNodes.GattServer)
            {
                var server = (GattServerNode)editor.Registry.Get(node.Type).Read(node.Configuration);
                var ids = server.DeviceIds.Where(id => id != deviceId).ToArray();
                return NodeDefinition.Create(node.Type, server with { DeviceIds = ids.Length == 0 ? [placeholder] : ids }, node.Id);
            }
            return SubgraphExpansion.MapDevices(node, new Dictionary<string, string> { [deviceId] = placeholder });
        }
        editor.EditDocument(graph with
        {
            AssignedDevices = editor.AssignedDeviceIds.Where(id => id != deviceId).ToArray(),
            Nodes = graph.Nodes.Select(n => TransformResources(n, editor.Registry, Unbind)).ToArray()
        });
    }

    private static NodeDefinition TransformResources(NodeDefinition node, NodeTypeRegistry registry, Func<NodeDefinition, NodeDefinition> transform)
    {
        if (node.Type != SubgraphExpansion.Type)
            return transform(node);
        var subgraph = (SubgraphNode)registry.Get(node.Type).Read(node.Configuration);
        var graph = subgraph.Graph with
        {
            Nodes = subgraph.Graph.Nodes.Select(n => TransformResources(SubgraphExpansion.MapDevices(n, subgraph.Devices ?? []), registry, transform)).ToArray()
        };
        return NodeDefinition.Create(node.Type, subgraph with { Graph = graph, Devices = null }, node.Id);
    }
}
