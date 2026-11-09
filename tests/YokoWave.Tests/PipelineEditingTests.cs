using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia;
using Avalonia.Headless.XUnit;
using YokoWave.App.ViewModels;
using YokoWave.App.ViewModels.Pipelines;
using YokoWave.Core;
using YokoWave.Pipelines;
using Xunit;

namespace YokoWave.Tests;

public sealed class PipelineEditingTests
{
    [AvaloniaFact]
    public async Task ParameterEditsKeepEditorsAndFieldsAndClipboardPreservesExposedPorts()
    {
        await using var controller = new DeviceController(new SimulationTransport());
        var b = new PipelineBuilder("editing");
        var source = b.Add(BuiltInNodes.Constant, new ConstantNode(.3));
        var smooth = b.Add(BuiltInNodes.Smooth, new SmoothNode());
        var excluded = b.Add(BuiltInNodes.Constant, new ConstantNode(.9));
        b.Connect(source, "value", smooth, "value");
        var graph = b.Build() with { Layout = [new(source.Id, 10, 20), new(smooth.Id, 300, 20), new(excluded.Id, 600, 20)] };
        await using var editor = new PipelineEditorViewModel(graph, controller, new ObservableCollection<PresetItem>(), error => throw error, _ => { });
        var node = editor.Nodes.Single(n => n.Id == smooth.Id);
        var field = Assert.Single(node.Parameters);
        field.Number = .4;
        Assert.Same(node, editor.Nodes.Single(n => n.Id == smooth.Id));
        Assert.Same(field, Assert.Single(node.Parameters));
        Assert.Equal(.4, node.ToDefinition().Configuration.GetProperty("Seconds").GetDouble());
        editor.UndoCommand.Execute(null);
        Assert.Equal(.2, Assert.Single(editor.Nodes.Single(n => n.Id == smooth.Id).Parameters).Number);
        editor.RedoCommand.Execute(null);
        Assert.Equal(.4, Assert.Single(editor.Nodes.Single(n => n.Id == smooth.Id).Parameters).Number);

        editor.Nodes.Single(n => n.Id == source.Id).Outputs.Single().IsExposed = true;
        editor.Nodes.Single(n => n.Id == smooth.Id).Inputs.First(p => p.Port.Id == "value").IsExposed = true;
        foreach (var original in editor.Nodes.Where(n => n.Id != excluded.Id))
            editor.SelectedNodes.Add(original);
        editor.CopyCommand.Execute(null);
        var before = editor.ToDocument();
        editor.PasteCommand.Execute(null);
        var pasted = editor.ToDocument();
        var copiedSource = Assert.Single(editor.Nodes, n => n.Type == BuiltInNodes.Constant && n.Id != source.Id && n.Id != excluded.Id);
        var copiedSmooth = Assert.Single(editor.Nodes, n => n.Type == BuiltInNodes.Smooth && n.Id != smooth.Id);
        Assert.Equal(5, pasted.Nodes.Length);
        Assert.Equal(new Point(50, 60), copiedSource.Location);
        Assert.Equal(new Point(340, 60), copiedSmooth.Location);
        Assert.Contains(pasted.Connections, c => c.Source.Node == copiedSource.Id && c.Target.Node == copiedSmooth.Id);
        Assert.Equal(2, pasted.Inputs!.Length);
        Assert.Equal(2, pasted.Outputs!.Length);
        Assert.True(copiedSource.Outputs.Single().IsExposed);
        Assert.True(copiedSmooth.Inputs.First(p => p.Port.Id == "value").IsExposed);
        Assert.Equal(4, pasted.Inputs.Concat(pasted.Outputs).Select(p => p.Id).Distinct().Count());
        Assert.Empty(editor.Diagnostics);

        editor.UndoCommand.Execute(null);
        Assert.Equal(JsonSerializer.Serialize(before, PipelineJson.Options), JsonSerializer.Serialize(editor.ToDocument(), PipelineJson.Options));
        editor.RedoCommand.Execute(null);
        Assert.Equal(JsonSerializer.Serialize(pasted, PipelineJson.Options), JsonSerializer.Serialize(editor.ToDocument(), PipelineJson.Options));
        Assert.Empty(editor.SelectedNodes);
        Assert.Equal(.4, Assert.Single(editor.Nodes.Single(n => n.Id == copiedSmooth.Id).Parameters).Number);
    }
}
