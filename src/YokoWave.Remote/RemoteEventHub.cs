using YokoWave.Pipelines;

namespace YokoWave.Remote;

/// <summary>Per-source bounded fan-out and cached declarations. Catalogs survive listener restarts; input data does not.</summary>
public sealed class RemoteEventHub
{
    private readonly object _gate = new();
    private readonly Dictionary<string, RemoteEventDefinition[]> _catalog = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Guid> _active = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, Queue<EventValue>> _subscriptions = [];
    private long _sequence;
    public IReadOnlyList<RemoteEventDescriptor> Snapshot()
    {
        lock (_gate)
            return _catalog.SelectMany(client => client.Value.Select(item => new RemoteEventDescriptor(client.Key, item))).ToArray();
    }

    internal void Register(Guid registration, RemoteEventRegistration declarations)
    {
        lock (_gate)
        {
            if (_active.ContainsKey(declarations.ClientId))
                throw new RemoteRpcException(-32002, "Error.RemoteEventClientInUse");
            _active.Add(declarations.ClientId, registration);
            _catalog[declarations.ClientId] = declarations.Events.ToArray();
        }
    }

    internal RemoteEventReceipt Emit(Guid registration, RemoteEventRegistration source, RemoteEventDefinition definition, double value, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_active.GetValueOrDefault(source.ClientId) != registration)
                throw new RemoteRpcException(-32003, "Error.RemoteLease");
            if (_subscriptions.Values.Any(queue => queue.Count >= RemoteEventProtocol.Capacity))
                throw new RemoteRpcException(-32005, "Error.RemoteEventQueue");
            var message = new EventValue(source.ClientId, definition.Id, registration, definition.Kind == RemoteEventKind.Trigger, value, ++_sequence,
                now, now.AddMilliseconds(definition.ValidityMilliseconds), source.Simulated);
            foreach (var queue in _subscriptions.Values)
                queue.Enqueue(message);
            return new(message.Sequence);
        }
    }

    internal void Remove(Guid registration)
    {
        lock (_gate)
        {
            foreach (string client in _active.Where(pair => pair.Value == registration).Select(pair => pair.Key).ToArray())
                _active.Remove(client);
            foreach (var queue in _subscriptions.Values)
            {
                var retained = queue.Where(message => message.Registration != registration).ToArray();
                queue.Clear();
                foreach (var message in retained)
                    queue.Enqueue(message);
            }
        }
    }

    internal void Reset(bool disconnect)
    {
        lock (_gate)
        {
            foreach (var queue in _subscriptions.Values)
                queue.Clear();
            if (disconnect)
                _active.Clear();
        }
    }

    internal RemoteEventSubscription Subscribe()
    {
        lock (_gate)
        {
            var id = Guid.NewGuid();
            _subscriptions.Add(id, new());
            return new(this, id);
        }
    }

    internal EventSignal Capture(Guid id)
    {
        lock (_gate)
        {
            if (!_subscriptions.TryGetValue(id, out var queue))
                return new([], []);
            var messages = queue.ToArray();
            queue.Clear();
            return new(_active.Select(pair => new EventSource(pair.Key, pair.Value)).ToArray(), messages);
        }
    }

    internal void Unsubscribe(Guid id)
    {
        lock (_gate)
            _subscriptions.Remove(id);
    }
}

internal sealed class RemoteEventSubscription(RemoteEventHub hub, Guid id) : IDisposable
{
    public EventSignal Capture() => hub.Capture(id);
    public void Dispose() => hub.Unsubscribe(id);
}
