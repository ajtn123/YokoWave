using YokoWave.Pipelines;

namespace YokoWave.App.ViewModels.Pipelines;

public sealed record ConnectionViewModel(NodePortViewModel Source, NodePortViewModel Target)
{
    public PipelineConnection Definition => new(new(Source.Node.Id, Source.Port.Id), new(Target.Node.Id, Target.Port.Id));
}
