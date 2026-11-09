namespace YokoWave.App.ViewModels.Pipelines;

public sealed record PipelineDeviceOption(string Id, string Name)
{
    public override string ToString() => Name;
}
