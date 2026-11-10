using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using YokoWave.Core;
using YokoWave.Core.Localization;
using YokoWave.Pipelines;

namespace YokoWave.Remote.DgLab.V4;

/// <summary>Network half of an app endpoint. Per-node processors, not this object, execute operations.</summary>
internal sealed class DgLabV4AppSession(DeviceController devices, string url, string controllerId) : IAsyncDisposable
{
    private readonly SemaphoreSlim _start = new(1, 1);
    private readonly DgLabV4Peer _peer = new();
    private readonly ConcurrentDictionary<string, Slot> _slots = new();
    private readonly ConcurrentDictionary<string, Pending> _pending = new();
    private bool _subscribed;
    private volatile bool _paused;
    private volatile bool _closing;
    public Exception? Error { get; private set; }
    public int SlotCount => _slots.Count;

    public async Task RegisterAsync(Guid node, DeviceDescriptor device, CancellationToken cancellationToken)
    {
        _ = DgLabV4Models.Type(device);
        string id = node.ToString("N");
        var slot = new Slot(node, device.Id);
        slot.Incoming.Enqueue(NodeControl.Create(node, "configure", DgLabV4Models.Maximum(device)));
        if (!_slots.TryAdd(id, slot))
            throw new LocalizedOperationException("Error.DeviceBusy");
        await _start.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            bool first = !_subscribed;
            if (!_subscribed)
            {
                _peer.Requested += Handle;
                _peer.FrameReceived += (type, _) =>
                {
                    if (type == "controller_attached")
                        Snapshot();
                };
                _peer.Disconnected += error =>
                {
                    if (!_closing)
                        Error = error ?? new LocalizedIOException("Error.RemoteDisconnected");
                    CancelPending(null, "cancelled", send: false);
                };
                _subscribed = true;
                await _peer.ConnectAsync(DgLabV4Protocol.AppUri(url, controllerId), cancellationToken).ConfigureAwait(false);
            }
            if (_peer.ControllerId is not null && !first)
                SendEvent(new { t = "ev", ev = "devices.patch", added = new[] { Snapshot(slot) } });
        }
        finally
        {
            _start.Release();
        }
    }

    public void Capture(Guid node, PipelineInputs inputs)
    {
        if (Error is { } error)
            throw error;
        if (!_slots.TryGetValue(node.ToString("N"), out var slot))
            return;
        var controls = inputs.Controls.GetOrAdd(node, _ => new ConcurrentQueue<NodeControl>());
        while (slot.Incoming.TryDequeue(out var command))
            controls.Enqueue(command);
        DrainResults(slot, inputs);
        var snapshot = Snapshot(slot);
        if (!JsonNode.DeepEquals(slot.LastProps, snapshot.Props) || !JsonNode.DeepEquals(slot.LastState, snapshot.SlotState))
        {
            slot.LastProps = snapshot.Props?.DeepClone();
            slot.LastState = snapshot.SlotState?.DeepClone();
            SendEvent(new { t = "ev", ev = "slots.patch", slots = new[] { snapshot } });
        }
    }

    public void Pause(bool paused)
    {
        _paused = paused;
        if (paused)
        {
            CancelPending(null, "cancelled", send: true);
            foreach (var slot in _slots.Values)
                slot.Incoming.Enqueue(NodeControl.Create(slot.Node, "cancel", new DgLabV4NodeCommand("")));
        }
    }

    public void Unregister(Guid node, PipelineInputs? inputs)
    {
        string id = node.ToString("N");
        if (inputs is not null && _slots.TryGetValue(id, out var slot))
            DrainResults(slot, inputs);
        CancelPending(node, "cancelled", send: true);
        if (_slots.TryRemove(id, out _))
            SendEvent(new { t = "ev", ev = "devices.patch", removed = new[] { id } });
    }

    public async ValueTask DisposeAsync()
    {
        _closing = true;
        CancelPending(null, "cancelled", send: true);
        await _peer.DisposeAsync().ConfigureAwait(false);
        _start.Dispose();
    }

    private void Handle(DgLabV4Request request)
    {
        try
        {
            if (_pending.ContainsKey(request.Id))
                throw new DgLabV4Exception("duplicate_request_id");
            switch (request.Method)
            {
                case "ping":
                    Respond(request, devices.Clock.GetUtcNow().ToUnixTimeMilliseconds());
                    return;
                case "devices.get":
                    Respond(request, new { devices = _slots.Values.Select(Snapshot).ToArray() });
                    return;
                case "device.op":
                    var operation = DgLabV4Protocol.Read<DgLabV4Operation>(request.Data);
                    operation.Validate();
                    if (!_slots.TryGetValue(operation.SlotId, out var slot))
                        throw new DgLabV4Exception("slot_not_found");
                    if (_paused || !devices.Connections.TryGetValue(slot.DeviceId, out var connection) || connection.Device.IsInput || !connection.Device.HasChannel(operation.Channel))
                        throw new DgLabV4Exception("invalid_operate");
                    AddPending(request, [slot.Node]);
                    slot.Incoming.Enqueue(NodeControl.Create(slot.Node, "operate", new DgLabV4NodeCommand(request.Id, operation)));
                    return;
                case "device.op.clear":
                    Clear(request);
                    return;
                default:
                    throw new DgLabV4Exception("method_not_found");
            }
        }
        catch (Exception error) when (error is DgLabV4Exception or JsonException or InvalidOperationException or ArgumentException)
        {
            Respond(request, error: error is DgLabV4Exception protocol ? protocol.Code : "invalid_params");
        }
    }

    private void Clear(DgLabV4Request request)
    {
        string? id = request.Data.TryGetProperty("s", out var target) ? target.GetString() : null;
        int? channel = request.Data.TryGetProperty("c", out var value) ? value.GetInt32() : null;
        if (channel is < 0 or > 1 || channel.HasValue && id is null)
            throw new DgLabV4Exception("invalid_params");
        var slots = _slots.Where(pair => id is null || pair.Key == id).Select(pair => pair.Value).ToArray();
        if (id is not null && slots.Length == 0)
            throw new DgLabV4Exception("slot_not_found");
        if (slots.Length == 0)
        {
            Respond(request, new { });
            return;
        }
        AddPending(request, slots.Select(slot => slot.Node).ToArray());
        foreach (var slot in slots)
            slot.Incoming.Enqueue(NodeControl.Create(slot.Node, "clear", new DgLabV4NodeCommand(request.Id, Channel: channel)));
    }

    private void AddPending(DgLabV4Request request, Guid[] nodes)
    {
        if (_pending.Count >= 256 || !_pending.TryAdd(request.Id, new(request, nodes)))
            throw new DgLabV4Exception("duplicate_request_id");
    }

    private void Result(Slot slot, NodeControl result)
    {
        if (result.Action == "completed")
        {
            var completion = result.Read<DgLabV4TaskResult>();
            if (_pending.TryRemove(completion.Id, out var pending))
                Respond(pending.Request, new { type = completion.Type, reason = completion.Reason, slotId = slot.Node.ToString("N"), channel = completion.Channel });
        }
        else if (result.Action == "cleared")
        {
            string id = result.Read<string>();
            if (_pending.TryGetValue(id, out var pending) && Interlocked.Decrement(ref pending.Remaining) == 0 && _pending.TryRemove(id, out _))
                Respond(pending.Request, new { });
        }
        else if (result.Action == "error")
        {
            string id = result.Value.GetProperty("Id").GetString()!;
            if (_pending.TryRemove(id, out var pending))
                Respond(pending.Request, error: result.Value.GetProperty("Code").GetString());
        }
        else if (result.Action == "action")
        {
            SendEvent(new { t = "ev", ev = "custom.action", action = result.Read<int>() });
        }
    }

    private void DrainResults(Slot slot, PipelineInputs inputs)
    {
        if (!inputs.Results.TryGetValue(slot.Node, out var results))
            return;
        while (results.TryDequeue(out var result))
            Result(slot, result);
    }

    private void CancelPending(Guid? node, string reason, bool send)
    {
        foreach (var entry in _pending.Where(pair => node is null || pair.Value.Nodes.Contains(node.Value)))
        {
            if (!_pending.TryRemove(entry.Key, out var pending) || !send || !_peer.IsConnected)
                continue;
            if (pending.Request.Method == "device.op")
            {
                var operation = DgLabV4Protocol.Read<DgLabV4Operation>(pending.Request.Data);
                Respond(pending.Request, new { type = operation.Type, reason, slotId = operation.SlotId, channel = operation.Channel });
            }
            else
            {
                Respond(pending.Request, new { });
            }
        }
    }

    private DgLabV4Device Snapshot(Slot slot) => devices.Connections.TryGetValue(slot.DeviceId, out var connection)
        ? DgLabV4Models.Snapshot(slot.Node.ToString("N"), connection) : throw new DgLabV4Exception("slot_not_found");
    private void Snapshot() => SendEvent(new { t = "ev", ev = "devices.snapshot", devices = _slots.Values.Select(Snapshot).ToArray() });
    private void SendEvent(object value)
    {
        if (_peer.IsConnected && _peer.ControllerId is not null)
            Send(() => _peer.SendData(null, value));
    }

    private void Respond(DgLabV4Request request, object? result = null, string? error = null) =>
        Send(() => _peer.Respond(request, result, error));

    private void Send(Action send)
    {
        try
        {
            send();
        }
        catch (Exception error) when (error is IOException or InvalidOperationException)
        {
            // The next capture faults the graph; cleanup must still release the socket and other slots.
            Error ??= error;
        }
    }

    private sealed class Slot(Guid node, string deviceId)
    {
        public Guid Node { get; } = node;
        public string DeviceId { get; } = deviceId;
        public ConcurrentQueue<NodeControl> Incoming { get; } = new();
        public JsonNode? LastProps { get; set; }
        public JsonNode? LastState { get; set; }
    }
    private sealed class Pending(DgLabV4Request request, Guid[] nodes)
    {
        public DgLabV4Request Request { get; } = request;
        public Guid[] Nodes { get; } = nodes;
        public int Remaining = nodes.Length;
    }
}
