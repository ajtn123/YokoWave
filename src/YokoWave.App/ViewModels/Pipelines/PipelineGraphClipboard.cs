using YokoWave.Pipelines;

namespace YokoWave.App.ViewModels.Pipelines;

/// <summary>Copies a graph fragment and remaps its node, route, layout and exposed-port identities on paste.</summary>
internal sealed class PipelineGraphClipboard
{
    private PipelineDocument? _selection;

    public void Copy(PipelineDocument document, IEnumerable<Guid> selectedNodes)
    {
        var selected = selectedNodes.ToHashSet();
        _selection = document with
        {
            Nodes = document.Nodes.Where(n => selected.Contains(n.Id)).ToArray(),
            Connections = document.Connections.Where(c => selected.Contains(c.Source.Node) && selected.Contains(c.Target.Node)).ToArray(),
            Layout = document.Layout.Where(l => selected.Contains(l.Node)).ToArray(),
            Inputs = document.Inputs?.Where(p => selected.Contains(p.Address.Node)).ToArray(),
            Outputs = document.Outputs?.Where(p => selected.Contains(p.Address.Node)).ToArray()
        };
    }

    public PipelineDocument? Paste(PipelineDocument current)
    {
        if (_selection is not { Nodes.Length: > 0 } copied)
            return null;
        var ids = copied.Nodes.ToDictionary(n => n.Id, _ => Guid.NewGuid());
        PortAddress Remap(PortAddress address) => new(ids[address.Node], address.Port);
        ExposedPort RemapPort(ExposedPort port) => port with
        {
            Id = ids[port.Address.Node].ToString("N") + "." + port.Address.Port,
            Address = Remap(port.Address)
        };
        return current with
        {
            Nodes = [.. current.Nodes, .. copied.Nodes.Select(n => n with { Id = ids[n.Id] })],
            Connections = [.. current.Connections, .. copied.Connections.Select(c => new PipelineConnection(Remap(c.Source), Remap(c.Target)))],
            Layout = [.. current.Layout, .. copied.Layout.Select(l => new NodeLayout(ids[l.Node], l.X + 40, l.Y + 40))],
            Inputs = [.. current.Inputs ?? [], .. (copied.Inputs ?? []).Select(RemapPort)],
            Outputs = [.. current.Outputs ?? [], .. (copied.Outputs ?? []).Select(RemapPort)]
        };
    }
}
