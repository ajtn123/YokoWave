namespace YokoWave.App.ViewModels.Pipelines;

public sealed record PipelineEndpointViewModel(string Url, string Kind, string Token)
{
    public bool HasToken => Token.Length > 0;
}
