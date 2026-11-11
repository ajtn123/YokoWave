using YokoWave.Core.Localization;
using YokoWave.Pipelines;

namespace YokoWave.Remote;

/// <summary>Trigger edges are queued; numeric updates use the latest value. All outputs retain source freshness and provenance.</summary>
internal sealed class RemoteEventProcessor(EventInputNode configuration) : ISignalProcessor, IInteractiveProcessor, ISuspendableProcessor
{
    private readonly Queue<EventValue> _pending = new();
    private EventInputNode _selection = configuration;
    private EventValue? _latest;
    private Guid _registration;
    private bool _triggered;
    private bool _numberChanged;
    private bool _simulated;
    private int _count;

    public void Update(NodeControl control)
    {
        if (control.Action != "selectEvent")
            return;
        _selection = control.Read<EventInputNode>();
        Suspend();
    }

    public void Evaluate(NodeEvaluationContext context)
    {
        var batch = context.EventInput("events");
        var registration = batch.Sources.FirstOrDefault(source => source.ClientId == _selection.ClientId)?.Registration ?? Guid.Empty;
        if (_registration != registration)
        {
            Suspend();
            _registration = registration;
        }
        bool Current(EventValue message) => registration != Guid.Empty && message.Registration == registration && message.ValidUntil >= context.Now;
        foreach (var message in batch.Messages.Where(message => message.ClientId == _selection.ClientId && message.EventId == _selection.EventId && Current(message)))
        {
            _simulated |= message.Simulated;
            if (!message.IsTrigger)
            {
                _latest = message;
                _numberChanged = true;
                _count++;
            }
            else
            {
                if (_pending.Count >= RemoteEventProtocol.Capacity)
                    throw new LocalizedOperationException("Error.RemoteEventQueue");
                _pending.Enqueue(message);
            }
        }
        while (_pending.TryPeek(out var pending) && !Current(pending))
            _pending.Dequeue();
        if (_latest is not null && !Current(_latest))
        {
            _latest = null;
            _numberChanged = false;
        }
        bool trigger = false;
        if (_triggered)
        {
            _triggered = false;
        }
        else if (_pending.TryDequeue(out var next))
        {
            _latest = next;
            _count++;
            trigger = _triggered = true;
        }
        else if (_numberChanged)
        {
            trigger = _triggered = true;
            _numberChanged = false;
        }
        long sequence = _latest?.Sequence ?? 0;
        var triggerUntil = context.Now.AddMilliseconds(100);
        if (_latest is not null && _latest.ValidUntil < triggerUntil)
            triggerUntil = _latest.ValidUntil;
        context.Output("trigger", _latest is null ? NumericSignal.Missing : new([trigger ? 1 : 0], triggerUntil, _simulated, sequence));
        context.Output("value", _latest is { } latest && (!latest.IsTrigger || trigger)
            ? new([latest.Value], latest.ValidUntil, _simulated, latest.Sequence) : NumericSignal.Missing);
        context.Output("count", _latest is null ? NumericSignal.Missing : new([_count], _latest.ValidUntil, _simulated, sequence));
        context.Status(new(_latest is null ? "Waiting" : "Tracking", _latest?.Value ?? 0, _count));
    }

    public void Suspend()
    {
        _pending.Clear();
        _latest = null;
        _registration = Guid.Empty;
        _triggered = false;
        _numberChanged = false;
        _simulated = false;
        _count = 0;
    }
}
