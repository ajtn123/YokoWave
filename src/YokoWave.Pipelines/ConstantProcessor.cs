namespace YokoWave.Pipelines;

internal sealed class ConstantProcessor(double value) : ISignalProcessor
{
    public void Evaluate(NodeEvaluationContext context) => context.Output("value", NumericSignal.Constant(context.Constant(value)));
}
