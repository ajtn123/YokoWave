using YokoWave.Core;

namespace YokoWave.Pipelines;

public sealed class NodeEvaluationContext
{
    private readonly PipelineRuntime _runtime;
    private readonly CompiledNode _node;
    public TimeSpan Elapsed => _runtime.Elapsed;
    public DateTimeOffset Now => _runtime.Now;
    public Guid NodeId => _node.Id;
    public PipelineInputs Inputs => _runtime.Inputs;
    internal NodeEvaluationContext(PipelineRuntime runtime, CompiledNode node)
    {
        _runtime = runtime;
        _node = node;
    }
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
    public void EventOutput(string port, EventSignal signal) => _runtime.Events[new(_node.Id, port)] = signal;
    public EventSignal EventInput(string port) => _runtime.EventInput(new(_node.Id, port));
    public void Status(PipelineNodeStatus status) => _runtime.NodeStatus[_node.Id] = status;
    public void Completion(bool completed, string reason) => _runtime.Completions[_node.Id] = (completed, reason);
    public void Stop(string reason) => _runtime.StopReason = reason;
    public void Command(string deviceId, Core.Bluetooth.DeviceCommand command) => _runtime.Commands.Add(new(deviceId, command));
    internal void Native(NativeOutputNode binding, double a, double b, double c, NativeProgramSettings program) =>
        _runtime.Outputs.Native(binding with { Limit = _runtime.Outputs.Limit(NodeId, binding.Limit) }, a, b, c, program, Elapsed);
    internal void RouteNative(NativeOutputNode binding, double a, double b, double c, NativeProgramOutput program, OutputFrame? options = null) =>
        _runtime.Outputs.Native(binding with { Limit = _runtime.Outputs.Limit(NodeId, binding.Limit) }, a, b, c, program.Settings, TimeSpan.FromSeconds(program.ElapsedSeconds), program.RemainingSeconds, options);
    internal void Channel(ChannelOutputNode binding, NumericSignal strength, NumericSignal frequency, NumericSignal width, NumericSignal limit, OutputFrame? options = null) =>
        _runtime.Outputs.Channel(binding with { Limit = _runtime.Outputs.Limit(NodeId, binding.Limit) }, strength, frequency, width, limit, Now, options);
    internal void Pump(PumpOutputNode binding, NumericSignal direction, NumericSignal water, NumericSignal peristalticSeconds, NumericSignal waterSeconds) =>
        _runtime.Outputs.Pump(binding with { Limit = _runtime.Outputs.Limit(NodeId, binding.Limit) }, direction, water, peristalticSeconds, waterSeconds, Now);
}
