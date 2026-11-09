namespace YokoWave.Pipelines;

internal sealed class LimitProcessor(double limit) : ISignalProcessor
{
    public void Evaluate(NodeEvaluationContext context) => context.Output("value",
        NumericSignal.Combine(context.Input("value"), context.Input("limit", limit), (value, cap) => Math.Clamp(value, 0, cap)));
}
