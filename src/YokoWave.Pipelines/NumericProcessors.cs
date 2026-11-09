namespace YokoWave.Pipelines;

public sealed record MixNode(double Amount = .5);
public sealed record ThresholdNode(double Threshold = .5);
public sealed record SmoothNode(double Seconds = .2);
public sealed record SelectNode;
public sealed record SimulatedInputNode(double Value = 0);

public static partial class BuiltInNodes
{
    public const string Mix = "numeric.mix";

    public const string Threshold = "numeric.threshold";

    public const string Smooth = "numeric.smooth";

    public const string Select = "numeric.select";

    public const string SimulatedInput = "input.simulated";
    private static NodeTypeRegistry RegisterNumeric(NodeTypeRegistry registry) => registry
        .Register(new NodeRegistration<MixNode>(Mix, "Pipeline.Node.Mix", [In("a"), In("b"), In("amount", 0, 1, .5), Out("value")],
            c => double.IsFinite(c.Amount) && c.Amount is >= 0 and <= 1, (c, _) => new MixProcessor(c)))
        .Register(new NodeRegistration<ThresholdNode>(Threshold, "Pipeline.Node.Threshold", [In("value"), Out("value")],
            c => double.IsFinite(c.Threshold), (c, _) => new ThresholdProcessor(c)))
        .Register(new NodeRegistration<SmoothNode>(Smooth, "Pipeline.Node.Smooth", [In("value"), Out("value")],
            c => double.IsFinite(c.Seconds) && c.Seconds is >= 0 and <= 60, (c, _) => new SmoothProcessor(c)))
        .Register(new NodeRegistration<SelectNode>(Select, "Pipeline.Node.Select", [In("a"), In("b"), In("select", 0, 1), Out("value")],
            _ => true, (_, _) => new SelectProcessor()))
        .Register(new NodeRegistration<SimulatedInputNode>(SimulatedInput, "Pipeline.Node.Simulated", [Out("value")],
            c => double.IsFinite(c.Value), (c, _) => new SimulatedInputProcessor(c.Value)));
}

internal sealed class MixProcessor(MixNode settings) : ISignalProcessor
{
    public void Evaluate(NodeEvaluationContext context)
    {
        var a = context.Input("a");
        var b = context.Input("b");
        var amount = context.Input("amount", settings.Amount);
        var left = NumericSignal.Combine(a, amount, (v, m) => v * (1 - m));
        var right = NumericSignal.Combine(b, amount, (v, m) => v * m);
        context.Output("value", NumericSignal.Combine(left, right, (x, y) => x + y));
    }
}
internal sealed class ThresholdProcessor(ThresholdNode settings) : ISignalProcessor
{
    public void Evaluate(NodeEvaluationContext context) => context.Output("value", Signals.Map(context.Input("value"), v => v >= settings.Threshold ? 1 : 0));
}
internal sealed class SelectProcessor : ISignalProcessor
{
    public void Evaluate(NodeEvaluationContext context)
    {
        var select = context.Input("select");
        if (!select.IsValid(context.Now))
        {
            context.Output("value", NumericSignal.Missing);
            return;
        }
        var chosen = context.Input(select.At(0) > 0 ? "b" : "a");
        context.Output("value", NumericSignal.Combine(chosen, select, (value, _) => value));
    }
}
internal sealed class SmoothProcessor(SmoothNode settings) : ISignalProcessor, ISuspendableProcessor
{
    private double _value;
    private TimeSpan? _at;
    public void Suspend()
    {
        _at = null;
        _value = 0;
    }
    public void Evaluate(NodeEvaluationContext context)
    {
        var input = context.Input("value");
        if (!input.IsValid(context.Now))
        {
            Suspend();
            context.Output("value", input);
            return;
        }
        double factor = settings.Seconds == 0 || _at is null ? 1 : 1 - Math.Exp(-(context.Elapsed - _at.Value).TotalSeconds / settings.Seconds);
        _value += (input.At(0) - _value) * factor;
        _at = context.Elapsed;
        context.Output("value", new([_value], input.ValidUntil, input.Simulated, input.Sequence));
    }
}
internal sealed class SimulatedInputProcessor(double value) : ISignalProcessor, IInteractiveProcessor
{
    public void Update(NodeControl control)
    {
        if (control.Action == "value" && control.Read<double>() is var updated && double.IsFinite(updated))
            value = updated;
    }
    public void Evaluate(NodeEvaluationContext context) => context.Output("value", new([value], context.Now.AddSeconds(2), true, context.Now.UtcTicks));
}
