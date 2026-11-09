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
    public Guid NodeId => _node.Id;
    public PipelineInputs Inputs => _runtime.Inputs;
    public bool IsSimulatedTarget(string deviceId) => _runtime.SimulatedDevices.Contains(deviceId);
    public void Zero(string deviceId) => _runtime.Outputs.Zero(deviceId);
    public bool IsConnected(string port) => _runtime.IsConnected(new(_node.Id, port));
    public NumericSignal Input(string port, double? defaultValue = null)
    {
        var spec = _node.Registration.PortsFor(_node.Configuration).Single(p => p.Id == port && p.Direction == PortDirection.Input).Parameter!;
        var signal = _runtime.Read(new(_node.Id, port), defaultValue ?? spec.DefaultValue);
        if (!signal.IsValid(Now) || signal.Samples.Any(value => !spec.Accepts(value)))
        {
            _runtime.Diagnostics.Add(new("Pipeline.Error.Input", _node.Id, port));
            return NumericSignal.Missing;
        }
        return signal;
    }
    public NumericSignal InputSignal(string port, NumericSignal fallback) => IsConnected(port) ? Input(port) : fallback;
    public double Constant(double fallback) => _runtime.Constants.GetValueOrDefault(_node.Id, fallback);
    public void Output(string port, NumericSignal signal) => _runtime.Values[new(_node.Id, port)] = signal;
    public void IntentOutput(string port, OutputFrame frame) => _runtime.Intents[new(_node.Id, port)] = frame;
    public OutputFrame? IntentInput(string port) => _runtime.IntentInput(new(_node.Id, port));
    public void Status(PipelineNodeStatus status) => _runtime.NodeStatus[_node.Id] = status;
    public void Completion(bool completed, string reason) => _runtime.Completions[_node.Id] = (completed, reason);
    public void Stop(string reason) => _runtime.StopReason = reason;
    public void Command(string deviceId, Core.Bluetooth.DeviceCommand command) => _runtime.Commands.Add(new(deviceId, command));
    internal void Native(NativeOutputNode binding, double a, double b, double c, NativeProgramSettings program) =>
        _runtime.Outputs.Native(binding with { Limit = _runtime.Outputs.Limit(NodeId, binding.Limit) }, a, b, c, program, Elapsed);
    internal void RouteNative(NativeOutputNode binding, double a, double b, double c, NativeProgramOutput program) =>
        _runtime.Outputs.Native(binding with { Limit = _runtime.Outputs.Limit(NodeId, binding.Limit) }, a, b, c, program.Settings, TimeSpan.FromSeconds(program.ElapsedSeconds), program.RemainingSeconds);
    internal void Channel(ChannelOutputNode binding, NumericSignal strength, NumericSignal frequency, NumericSignal width, NumericSignal limit) =>
        _runtime.Outputs.Channel(binding with { Limit = _runtime.Outputs.Limit(NodeId, binding.Limit) }, strength, frequency, width, limit, Now);
    internal void Pump(PumpOutputNode binding, NumericSignal direction, NumericSignal water, NumericSignal peristalticSeconds, NumericSignal waterSeconds) =>
        _runtime.Outputs.Pump(binding with { Limit = _runtime.Outputs.Limit(NodeId, binding.Limit) }, direction, water, peristalticSeconds, waterSeconds, Now);
}

internal sealed class ConstantProcessor(double value) : ISignalProcessor
{
    public void Evaluate(NodeEvaluationContext context) => context.Output("value", NumericSignal.Constant(context.Constant(value)));
}

internal sealed class WaveformProcessor(WaveformPreset preset, int channel) : ISignalProcessor, IInteractiveProcessor
{
    private WaveformPreset _preset = preset;
    private bool _restart;
    private double _epoch;
    public void Update(NodeControl control)
    {
        if (control.Action != "waveform")
            return;
        var waveform = control.Read<WaveformPreset>();
        waveform.Validate();
        _preset = waveform;
        _restart = true;
    }
    public void Evaluate(NodeEvaluationContext context)
    {
        if (_restart)
        {
            _epoch = context.Elapsed.TotalSeconds;
            _restart = false;
        }
        var samples = WaveformGenerator.Window(_preset, context.Elapsed.TotalSeconds - _epoch);
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

public sealed record PipelineFrame(IReadOnlyDictionary<string, OutputFrame> Outputs, IReadOnlyList<PipelineDiagnostic> Diagnostics,
    IReadOnlyList<PipelineCommandIntent>? Commands = null);

/// <summary>One run's processors and parameter queue. Evaluation performs no I/O.</summary>
public sealed class PipelineRuntime
{
    private readonly PipelinePlan _plan;
    private readonly (CompiledNode Node, ISignalProcessor Processor)[] _processors;
    private readonly ConcurrentQueue<(Guid Id, double Value)> _pendingConstants = new();
    private readonly ConcurrentQueue<NodeControl> _pendingControls = new();
    private readonly ConcurrentQueue<(Guid Node, double Value)> _pendingLimits = new();
    public void SetLimit(Guid node, double value)
    {
        if (!double.IsFinite(value) || value is < 0 or > 1)
            throw new Core.Localization.LocalizedArgumentException("Error.InvalidSettings");
        if (_plan.Nodes.Any(n => n.Id == node && BuiltInNodes.IsOutput(n.Registration.Type)))
            _pendingLimits.Enqueue((node, value));
    }
    public PipelineInputs Inputs { get; } = new();
    internal IReadOnlySet<string> SimulatedDevices { get; }
    public ConcurrentDictionary<Guid, PipelineNodeStatus> NodeStatus { get; } = new();
    internal Dictionary<Guid, (bool Completed, string Reason)> Completions { get; } = [];
    internal List<PipelineCommandIntent> Commands { get; } = [];
    internal string? StopReason { get; set; }
    public string? CompletionReason => StopReason ?? (Completions.Count > 0 && Completions.Values.All(c => c.Completed) ? Completions.Values.First().Reason : null);
    internal Dictionary<Guid, double> Constants { get; } = [];
    internal Dictionary<PortAddress, NumericSignal> Values { get; } = [];
    internal Dictionary<PortAddress, OutputFrame> Intents { get; } = [];
    internal List<PipelineDiagnostic> Diagnostics { get; } = [];
    internal DeviceOutputCoordinator Outputs { get; }
    internal TimeSpan Elapsed { get; private set; }
    internal DateTimeOffset Now { get; private set; }

    public PipelineRuntime(PipelinePlan plan, IReadOnlySet<string> simulatedDevices)
    {
        _plan = plan;
        SimulatedDevices = simulatedDevices;
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
    internal bool IsConnected(PortAddress address) => _plan.Inputs.ContainsKey(address);
    internal OutputFrame? IntentInput(PortAddress address) => _plan.Inputs.TryGetValue(address, out var source) ? Intents.GetValueOrDefault(source) : null;
    public bool Update(NodeControl control)
    {
        if (!_processors.Any(p => p.Node.Id == control.Node && p.Processor is IInteractiveProcessor))
            return false;
        _pendingControls.Enqueue(control);
        return true;
    }
    public void Suspend()
    {
        foreach (var processor in _processors.Select(p => p.Processor).OfType<ISuspendableProcessor>())
            processor.Suspend();
    }

    public PipelineFrame Evaluate(TimeSpan elapsed, DateTimeOffset now)
    {
        Elapsed = elapsed;
        Now = now;
        while (_pendingLimits.TryDequeue(out var limit))
            Outputs.SetLimit(limit.Node, limit.Value);
        while (_pendingConstants.TryDequeue(out var change))
            Constants[change.Id] = change.Value;
        while (_pendingControls.TryDequeue(out var control))
            ((IInteractiveProcessor)_processors.Single(p => p.Node.Id == control.Node).Processor).Update(control);
        Values.Clear();
        Intents.Clear();
        Diagnostics.Clear();
        Commands.Clear();
        Completions.Clear();
        StopReason = null;
        Outputs.Clear(elapsed);
        foreach (var (node, processor) in _processors)
            processor.Evaluate(new(this, node));
        return new(Outputs.Build(), Diagnostics.ToArray(), Commands.ToArray());
    }
}
