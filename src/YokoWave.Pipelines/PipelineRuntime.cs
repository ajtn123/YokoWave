using System.Collections.Concurrent;
using YokoWave.Core;

namespace YokoWave.Pipelines;

/// <summary>One run's processors and parameter queue. Evaluation performs no I/O.</summary>
public sealed class PipelineRuntime
{
    private readonly PipelinePlan _plan;
    private readonly (CompiledNode Node, ISignalProcessor Processor)[] _processors;
    private readonly ConcurrentQueue<(Guid Id, double Value)> _pendingConstants = new();
    private readonly ConcurrentQueue<NodeControl> _pendingControls = new();
    private readonly ConcurrentQueue<(Guid Node, double Value)> _pendingLimits = new();
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
    internal Dictionary<PortAddress, EventSignal> Events { get; } = [];
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

    public void SetLimit(Guid node, double value)
    {
        if (!double.IsFinite(value) || value is < 0 or > 1)
            throw new Core.Localization.LocalizedArgumentException("Error.InvalidSettings");
        if (_plan.Nodes.Any(n => n.Id == node && BuiltInNodes.IsOutput(n.Registration.Type)))
            _pendingLimits.Enqueue((node, value));
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
    internal EventSignal EventInput(PortAddress address) => _plan.Inputs.TryGetValue(address, out var source) ? Events.GetValueOrDefault(source, EventSignal.Empty) : EventSignal.Empty;
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
        Events.Clear();
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
