namespace YokoWave.Pipelines;

internal sealed class ChannelOutputProcessor(ChannelOutputNode binding) : ISignalProcessor
{
    public void Evaluate(NodeEvaluationContext context) => context.Channel(binding,
        context.Input("strength"), context.Input("frequency"), context.Input("pulseWidth"), context.Input("limit"));
}
