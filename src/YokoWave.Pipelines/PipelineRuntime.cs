using System.Collections.Concurrent;
using YokoWave.Core;

namespace YokoWave.Pipelines;

public sealed class NodeEvaluationContext
{
    private readonly PipelineRuntime _runtime;
    private readonly CompiledNode _node;
    internal NodeEvaluationContext(PipelineRuntime runtime, CompiledNode node)
    {
        _runtime = runtime;
        _node = node;
    }
    public TimeSpan Elapsed => _runtime.Elapsed;
    public DateTimeOffset Now => _runtime.Now;
    public NumericSignal Input(string port, double? defaultValue = null)
    {
        var spec = _node.Registration.Ports.Single(p => p.Id == port && p.Direction == PortDirection.Input).Parameter!;
        var signal = _runtime.Read(new(_node.Id, port), defaultValue ?? spec.DefaultValue);
        if (!signal.IsValid(Now) || signal.Samples.Any(value => !spec.Accepts(value)))
        {
            _runtime.Diagnostics.Add(new("Pipeline.Error.Input", _node.Id, port));
            return NumericSignal.Missing;
        }
        return signal;
    }
    public double Constant(double fallback) => _runtime.Constants.GetValueOrDefault(_node.Id, fallback);
    public void Output(string port, NumericSignal signal) => _runtime.Values[new(_node.Id, port)] = signal;
    internal void Channel(ChannelOutputNode binding, NumericSignal strength, NumericSignal frequency, NumericSignal width, NumericSignal limit) =>
        _runtime.Outputs.Channel(binding, strength, frequency, width, limit, Now);
    internal void Pump(PumpOutputNode binding, NumericSignal direction, NumericSignal water, NumericSignal peristalticSeconds, NumericSignal waterSeconds) =>
        _runtime.Outputs.Pump(binding, direction, water, peristalticSeconds, waterSeconds, Now);
}

internal sealed class ConstantProcessor(double value) : ISignalProcessor
{
    public void Evaluate(NodeEvaluationContext context) => context.Output("value", NumericSignal.Constant(context.Constant(value)));
}

internal sealed class WaveformProcessor(WaveformPreset preset, int channel) : ISignalProcessor
{
    public void Evaluate(NodeEvaluationContext context)
    {
        var samples = WaveformGenerator.Window(preset, context.Elapsed.TotalSeconds);
        context.Output("level", new(samples.Select(s => s.Level * s.ChannelMagnitude(channel)).ToArray(), DateTimeOffset.MaxValue));
        context.Output("frequency", new(samples.Select(s => s.FrequencyHz).ToArray(), DateTimeOffset.MaxValue));
        context.Output("pulseWidth", new(samples.Select(s => s.PulseWidth).ToArray(), DateTimeOffset.MaxValue));
    }
}

internal sealed class GainProcessor(double gain) : ISignalProcessor
{
    public void Evaluate(NodeEvaluationContext context) => context.Output("value",
        NumericSignal.Combine(context.Input("value"), context.Input("gain", gain), (value, factor) => value * factor));
}

internal sealed class LimitProcessor(double limit) : ISignalProcessor
{
    public void Evaluate(NodeEvaluationContext context) => context.Output("value",
        NumericSignal.Combine(context.Input("value"), context.Input("limit", limit), (value, cap) => Math.Clamp(value, 0, cap)));
}

internal sealed class ChannelOutputProcessor(ChannelOutputNode binding) : ISignalProcessor
{
    public void Evaluate(NodeEvaluationContext context) => context.Channel(binding,
        context.Input("strength"), context.Input("frequency"), context.Input("pulseWidth"), context.Input("limit"));
}

internal sealed class PumpOutputProcessor(PumpOutputNode binding) : ISignalProcessor
{
    public void Evaluate(NodeEvaluationContext context) => context.Pump(binding,
        context.Input("direction"), context.Input("water"), context.Input("peristalticSeconds"), context.Input("waterSeconds"));
}

public sealed record PipelineFrame(IReadOnlyDictionary<string, OutputFrame> Outputs, IReadOnlyList<PipelineDiagnostic> Diagnostics);

/// <summary>One run's processors and parameter queue. Evaluation performs no I/O.</summary>
public sealed class PipelineRuntime
{
    private readonly PipelinePlan _plan;
    private readonly (CompiledNode Node, ISignalProcessor Processor)[] _processors;
    private readonly ConcurrentQueue<(Guid Id, double Value)> _pendingConstants = new();
    internal Dictionary<Guid, double> Constants { get; } = [];
    internal Dictionary<PortAddress, NumericSignal> Values { get; } = [];
    internal List<PipelineDiagnostic> Diagnostics { get; } = [];
    internal DeviceOutputCoordinator Outputs { get; }
    internal TimeSpan Elapsed { get; private set; }
    internal DateTimeOffset Now { get; private set; }

    public PipelineRuntime(PipelinePlan plan, IReadOnlySet<string> simulatedDevices)
    {
        _plan = plan;
        Outputs = new(plan, simulatedDevices);
        _processors = plan.Nodes.Select(node => (node, node.Registration.Create(node.Configuration, plan.Waveforms))).ToArray();
    }

    public bool SetConstant(Guid node, double value)
    {
        if (!double.IsFinite(value) || !_plan.Nodes.Any(n => n.Id == node && n.Configuration is ConstantNode))
            return false;
        _pendingConstants.Enqueue((node, value));
        return true;
    }

    internal NumericSignal Read(PortAddress address, double fallback) => _plan.Inputs.TryGetValue(address, out var source)
        ? Values.GetValueOrDefault(source, NumericSignal.Missing) : NumericSignal.Constant(fallback);

    public PipelineFrame Evaluate(TimeSpan elapsed, DateTimeOffset now)
    {
        Elapsed = elapsed;
        Now = now;
        while (_pendingConstants.TryDequeue(out var change))
            Constants[change.Id] = change.Value;
        Values.Clear();
        Diagnostics.Clear();
        Outputs.Clear(elapsed);
        foreach (var (node, processor) in _processors)
            processor.Evaluate(new(this, node));
        return new(Outputs.Build(), Diagnostics.ToArray());
    }
}
