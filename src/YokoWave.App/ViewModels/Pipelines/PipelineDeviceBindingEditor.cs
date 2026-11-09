using YokoWave.Core;
using YokoWave.Core.Localization;
using YokoWave.Pipelines;
using YokoWave.Remote;

namespace YokoWave.App.ViewModels.Pipelines;

/// <summary>Maps device cards to graph resources, without storing output settings on devices.</summary>
internal static class PipelineDeviceBindingEditor
{
    private static bool IsOutput(DeviceOutputBinding binding) => binding.Kind is OutputBindingKind.Channel or OutputBindingKind.Pump or OutputBindingKind.Native or OutputBindingKind.Router;
    public static bool CanAssign(PipelineEditorViewModel editor, DeviceDescriptor device)
    {
        if (!editor.CanEdit || editor.CanBindDevice?.Invoke(device.Id) == false || editor.BoundDeviceIds.Contains(device.Id))
            return false;
        var candidates = Candidates(editor, device).ToArray();
        if (candidates.Any(c => c.Binding.DeviceId.StartsWith('$')))
            return true;
        var document = editor.ToDocument();
        if (document.Nodes.Any(n => n.Type == RemotePipelineNodes.GattServer))
            return true;
        return !document.Nodes.Any(n => n.Type == SubgraphExpansion.Type) && candidates.Any(c => IsOutput(c.Binding));
    }

    private static IEnumerable<(NodeDefinition Node, DeviceOutputBinding Binding)> Candidates(PipelineEditorViewModel editor, DeviceDescriptor device) =>
        SubgraphExpansion.Expand(editor.ToDocument()).Nodes.SelectMany(n => editor.Registry.Get(n.Type).Resources(editor.Registry.Get(n.Type).Read(n.Configuration))
            .Where(b => PipelineCompiler.Accepts(b, device)).Select(b => (n, b)));

    public static void Assign(PipelineEditorViewModel editor, DeviceDescriptor device)
    {
        if (!CanAssign(editor, device))
            throw new LocalizedOperationException("Pipeline.Error.Device");
        var candidates = Candidates(editor, device).ToArray();
        var slot = candidates.Where(c => c.Binding.DeviceId.StartsWith('$'))
            .OrderByDescending(c => device.IsInput ? c.Binding.Kind == OutputBindingKind.Sensor : IsOutput(c.Binding)).FirstOrDefault();
        if (slot.Node is not null)
        {
            editor.RebindDevice(slot.Binding.DeviceId, device.Id);
            return;
        }
        var document = editor.ToDocument();
        var export = document.Nodes.FirstOrDefault(n => n.Type == RemotePipelineNodes.GattServer);
        if (export is not null)
        {
            var config = (GattServerNode)editor.Registry.Get(export.Type).Read(export.Configuration);
            editor.ReplaceNode(NodeDefinition.Create(export.Type, config with { DeviceIds = config.DeviceIds.Append(device.Id).ToArray() }, export.Id));
            return;
        }
        var group = candidates.Where(c => IsOutput(c.Binding)).GroupBy(c => c.Binding.DeviceId).FirstOrDefault();
        if (group is null || document.Nodes.Any(n => n.Type == SubgraphExpansion.Type))
            throw new LocalizedOperationException("Pipeline.Error.Device");
        var copies = group.Select(c => c.Node).DistinctBy(n => n.Id).ToDictionary(n => n.Id,
            n => SubgraphExpansion.MapDevices(n with { Id = Guid.NewGuid() }, new Dictionary<string, string> { [group.Key] = device.Id }));
        var edges = document.Connections.Where(c => copies.ContainsKey(c.Target.Node)).Select(c => new PipelineConnection(
            copies.TryGetValue(c.Source.Node, out var from) ? c.Source with { Node = from.Id } : c.Source,
            c.Target with { Node = copies[c.Target.Node].Id }));
        var layout = document.Layout.Where(l => copies.ContainsKey(l.Node)).Select(l => l with { Node = copies[l.Node].Id, Y = l.Y + 160 });
        editor.EditDocument(document with
        {
            Nodes = document.Nodes.Concat(copies.Values).ToArray(),
            Connections = document.Connections.Concat(edges).ToArray(),
            Layout = document.Layout.Concat(layout).ToArray()
        });
    }

    public static void Detach(PipelineEditorViewModel editor, string deviceId)
    {
        if (!editor.CanEdit)
            return;
        var graph = editor.ToDocument();
        var bound = graph.Nodes.Where(n => editor.Registry.Get(n.Type).Resources(editor.Registry.Get(n.Type).Read(n.Configuration)).Any(b => b.DeviceId == deviceId)).ToArray();
        if (bound.Length == 1 && bound[0].Type == RemotePipelineNodes.GattServer)
        {
            var config = (GattServerNode)editor.Registry.Get(bound[0].Type).Read(bound[0].Configuration);
            if (config.DeviceIds.Length > 1)
            {
                editor.ReplaceNode(NodeDefinition.Create(bound[0].Type, config with { DeviceIds = config.DeviceIds.Where(id => id != deviceId).ToArray() }, bound[0].Id));
                return;
            }
        }
        var outputs = graph.Nodes.Where(n => BuiltInNodes.IsOutput(n.Type)).ToArray();
        if (bound.Length > 0 && bound.All(n => BuiltInNodes.IsOutput(n.Type)) && outputs.Except(bound).Any())
        {
            var removed = bound.Select(n => n.Id).ToHashSet();
            editor.EditDocument(graph with
            {
                Nodes = graph.Nodes.Where(n => !removed.Contains(n.Id)).ToArray(),
                Connections = graph.Connections.Where(c => !removed.Contains(c.Source.Node) && !removed.Contains(c.Target.Node)).ToArray(),
                Layout = graph.Layout.Where(l => !removed.Contains(l.Node)).ToArray()
            });
        }
        else
            editor.RebindDevice(deviceId, "$device-" + Guid.NewGuid().ToString("N")[..6]);
    }
}
