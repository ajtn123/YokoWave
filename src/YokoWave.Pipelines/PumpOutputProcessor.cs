namespace YokoWave.Pipelines;

internal sealed class PumpOutputProcessor(PumpOutputNode binding) : ISignalProcessor
{
    public void Evaluate(NodeEvaluationContext context) => context.Pump(binding,
        context.Input("direction"), context.Input("water"), context.Input("peristalticSeconds"), context.Input("waterSeconds"));
}
