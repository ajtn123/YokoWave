using YokoWave.Core.Localization;
using YokoWave.Pipelines;

namespace YokoWave.App.ViewModels.Pipelines;

public sealed class PipelineTemplateChoice(PipelineTemplate? builtIn = null, SavedPipelineTemplate? savedTemplate = null) : ObservableObject
{
    public string Name => builtIn is not null ? L10n.Get(builtIn.NameKey) : savedTemplate!.Name;
    public bool IsBuiltIn => builtIn is not null;
    public Guid? TemplateId => savedTemplate?.Id;
    public PipelineDocument Create(string name, double limit)
    {
        if (builtIn is not null)
            return builtIn.Create(new(name, Limit: limit));
        var graph = savedTemplate!.Graph;
        var wrapper = NodeDefinition.Create(SubgraphExpansion.Type, new SubgraphNode(graph, savedTemplate.Inputs, savedTemplate.Outputs));
        return new(Guid.NewGuid(), name, [wrapper], [], [new(wrapper.Id, 40, 40)], graph.DurationSeconds, graph.TimerEnabled);
    }
    public void RefreshLocalization() => Raise(nameof(Name));
}
