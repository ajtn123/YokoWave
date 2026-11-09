namespace YokoWave.Pipelines;

internal sealed class GainProcessor(double gain) : ISignalProcessor
{
    public void Evaluate(NodeEvaluationContext context) => context.Output("value",
        NumericSignal.Combine(context.Input("value"), context.Input("gain", gain), (value, factor) => value * factor));
}
