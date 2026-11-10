using System.Text.Json;
using YokoWave.Core;
using YokoWave.Pipelines;

namespace YokoWave.Remote.DgLab.V4;

/// <summary>Serializes request acceptance, slot removal and cancellation; processors alone execute tasks.</summary>
internal sealed class DgLabV4AppRouter(DeviceController devices, Action<DgLabV4Request, object?, string?> respond, Action<object> publish)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, DgLabV4AppSlot> _slots = [];
    private readonly Dictionary<string, PendingRequest> _pending = [];
    private bool _paused;
    private bool _closed;

    public void Add(Guid nodeId, DeviceDescriptor device)
    {
        lock (_gate)
        {
            if (_closed || _slots.Count >= 128)
                throw new DgLabV4Exception("invalid_operate");
            var slot = new DgLabV4AppSlot(nodeId, device);
            if (!_slots.TryAdd(slot.SlotId, slot))
                throw new DgLabV4Exception("invalid_operate");
            slot.Configure();
        }
    }

    public void PublishSnapshot()
    {
        lock (_gate)
            publish(new { t = "ev", ev = "devices.snapshot", devices = _slots.Values.Select(slot => slot.Publish(devices)).ToArray() });
    }

    public void PublishAdded(Guid nodeId)
    {
        lock (_gate)
            publish(new { t = "ev", ev = "devices.patch", added = new[] { _slots[nodeId.ToString("N")].Publish(devices) } });
    }

    public void Handle(DgLabV4Request request)
    {
        lock (_gate)
        {
            try
            {
                if (_closed)
                    throw new DgLabV4Exception("invalid_operate");
                if (request.Data.ValueKind != JsonValueKind.Object)
                    throw new DgLabV4Exception("invalid_params");
                if (_pending.ContainsKey(request.Id))
                    throw new DgLabV4Exception("duplicate_request_id");
                switch (request.Method)
                {
                    case "ping":
                        respond(request, devices.Clock.GetUtcNow().ToUnixTimeMilliseconds(), null);
                        break;
                    case "devices.get":
                        respond(request, new { devices = _slots.Values.Select(slot => slot.Snapshot(devices)).ToArray() }, null);
                        break;
                    case "device.op":
                        Operate(request);
                        break;
                    case "device.op.clear":
                        Clear(request);
                        break;
                    default:
                        throw new DgLabV4Exception("method_not_found");
                }
            }
            catch (Exception error) when (error is DgLabV4Exception or JsonException or ArgumentException)
            {
                respond(request, null, error is DgLabV4Exception protocol ? protocol.Code : "invalid_params");
            }
            catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
            {
                System.Diagnostics.Trace.TraceError("V4 App request failed: {0}", error);
                respond(request, null, "internal_error");
            }
        }
    }

    public void Capture(Guid nodeId, PipelineInputs inputs)
    {
        lock (_gate)
        {
            if (_closed || !_slots.TryGetValue(nodeId.ToString("N"), out var slot))
                return;
            slot.Capture(inputs, result => Complete(slot, result));
            if (slot.CapturePatch(devices) is { } patch)
                publish(new { t = "ev", ev = "slots.patch", slots = new[] { patch } });
        }
    }

    public void Pause(bool paused)
    {
        lock (_gate)
        {
            if (_paused == paused)
                return;
            _paused = paused;
            foreach (var slot in _slots.Values)
            {
                slot.DrainResults(result => Complete(slot, result));
                slot.SetPaused(paused);
                if (slot.CapturePatch(devices) is { } patch)
                    publish(new { t = "ev", ev = "slots.patch", slots = new[] { patch } });
            }
            if (paused)
                CancelPending(null, send: true);
        }
    }

    public void Remove(Guid nodeId)
    {
        lock (_gate)
        {
            if (!_slots.Remove(nodeId.ToString("N"), out var slot))
                return;
            slot.DrainResults(result => Complete(slot, result));
            slot.Cancel();
            CancelPending(nodeId, send: true);
            publish(new { t = "ev", ev = "devices.patch", removed = new[] { slot.SlotId } });
        }
    }

    public void Close(bool send)
    {
        lock (_gate)
        {
            _closed = true;
            foreach (var slot in _slots.Values)
                slot.Cancel();
            CancelPending(null, send);
        }
    }

    private void Operate(DgLabV4Request request)
    {
        if (!request.Data.TryGetProperty("s", out var target) || target.ValueKind != JsonValueKind.String ||
            !_slots.TryGetValue(target.GetString() ?? "", out var slot))
            throw new DgLabV4Exception("slot_not_found");
        DgLabV4Operation operation;
        try
        {
            operation = DgLabV4Protocol.Read<DgLabV4Operation>(request.Data);
            operation.Validate();
        }
        catch (DgLabV4Exception)
        {
            throw new DgLabV4Exception("invalid_operate");
        }
        if (!devices.Connections.TryGetValue(slot.DeviceId, out var connection) || connection.Device.IsInput || !connection.Device.HasChannel(operation.Channel))
            throw new DgLabV4Exception("invalid_operate");
        if (_paused)
        {
            if (operation.Type != 7)
                throw new DgLabV4Exception("invalid_operate");
            // Runtime suspension has already reset base intensity; zero reset remains available while paused.
            respond(request, new { type = operation.Type, reason = "completed", slotId = slot.SlotId, channel = operation.Channel }, null);
            return;
        }
        AddPending(request, [slot.NodeId], operation.Immediate);
        slot.Enqueue("operate", new DgLabV4NodeCommand(request.Id, operation));
    }

    private void Clear(DgLabV4Request request)
    {
        string? slotId = null;
        if (request.Data.TryGetProperty("s", out var target))
        {
            if (target.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(target.GetString()))
                throw new DgLabV4Exception("invalid_params");
            slotId = target.GetString();
        }
        int? channel = null;
        if (request.Data.TryGetProperty("c", out var value))
        {
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int number))
                throw new DgLabV4Exception("invalid_params");
            channel = number;
        }
        if (channel is < 0 or > 1 || channel.HasValue && slotId is null)
            throw new DgLabV4Exception("invalid_params");
        var slots = _slots.Values.Where(slot => slotId is null || slot.SlotId == slotId).ToArray();
        if (slotId is not null && slots.Length == 0)
            throw new DgLabV4Exception("slot_not_found");
        if (slots.Length == 0 || _paused)
        {
            respond(request, new { }, null);
            return;
        }
        AddPending(request, slots.Select(slot => slot.NodeId).ToArray());
        foreach (var slot in slots)
            slot.Enqueue("clear", new DgLabV4NodeCommand(request.Id, Channel: channel));
    }

    private void AddPending(DgLabV4Request request, Guid[] nodes, bool replacement = false)
    {
        int capacity = request.Method == "device.op.clear" ? 261 : replacement ? 257 : 256;
        if (_pending.Count >= capacity)
            throw new DgLabV4Exception("invalid_operate");
        _pending.Add(request.Id, new(request, nodes));
    }

    private void Complete(DgLabV4AppSlot slot, NodeControl result)
    {
        if (result.Action == "completed")
        {
            var completion = result.Read<DgLabV4TaskResult>();
            if (_pending.Remove(completion.Id, out var pending))
                respond(pending.Request, new { type = completion.Type, reason = completion.Reason, slotId = slot.SlotId, channel = completion.Channel }, null);
        }
        else if (result.Action == "cleared")
        {
            string id = result.Read<string>();
            if (_pending.TryGetValue(id, out var pending) && pending.Complete(slot.NodeId) && _pending.Remove(id))
                respond(pending.Request, new { }, null);
        }
        else if (result.Action == "error")
        {
            string id = result.Value.GetProperty("Id").GetString()!;
            if (_pending.Remove(id, out var pending))
                respond(pending.Request, null, result.Value.GetProperty("Code").GetString());
        }
        else if (result.Action == "action")
        {
            publish(new { t = "ev", ev = "custom.action", action = result.Read<int>() });
        }
    }

    private void CancelPending(Guid? nodeId, bool send)
    {
        foreach (var entry in _pending.Where(pair => nodeId is null || pair.Value.Contains(nodeId.Value)).ToArray())
        {
            _pending.Remove(entry.Key);
            if (!send)
                continue;
            var pending = entry.Value;
            var operation = pending.Request.Method == "device.op" ? DgLabV4Protocol.Read<DgLabV4Operation>(pending.Request.Data) : null;
            respond(pending.Request, operation is null ? new { } : new { type = operation.Type, reason = "cancelled", slotId = operation.SlotId, channel = operation.Channel }, null);
        }
    }

    private sealed class PendingRequest(DgLabV4Request request, Guid[] nodes)
    {
        private readonly HashSet<Guid> _remaining = [.. nodes];
        public DgLabV4Request Request { get; } = request;
        public bool Contains(Guid nodeId) => _remaining.Contains(nodeId);
        public bool Complete(Guid nodeId) => _remaining.Remove(nodeId) && _remaining.Count == 0;
    }
}
