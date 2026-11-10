using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using YokoWave.Core;
using YokoWave.Pipelines;

namespace YokoWave.Remote.DgLab.V4;

/// <summary>One app slot's mailbox and published state. Access is serialized by its app router.</summary>
internal sealed class DgLabV4AppSlot(Guid nodeId, DeviceDescriptor device)
{
    private readonly Queue<NodeControl> _incoming = new();
    private PipelineInputs? _inputs;
    private DgLabV4Device? _published;
    private bool _paused;
    public Guid NodeId { get; } = nodeId;
    public string SlotId => NodeId.ToString("N");
    public string DeviceId => device.Id;

    public void Configure() => Enqueue("configure", Math.Max(1, DgLabV4Models.Maximum(device)));
    public void Enqueue<T>(string action, T command) => _incoming.Enqueue(NodeControl.Create(NodeId, action, command));

    public void Capture(PipelineInputs inputs, Action<NodeControl> complete)
    {
        _inputs = inputs;
        var controls = inputs.Controls.GetOrAdd(NodeId, _ => new ConcurrentQueue<NodeControl>());
        while (_incoming.TryDequeue(out var command))
            controls.Enqueue(command);
        DrainResults(complete);
    }

    public void DrainResults(Action<NodeControl> complete)
    {
        if (_inputs is not { } inputs || !inputs.Results.TryGetValue(NodeId, out var results))
            return;
        while (results.TryDequeue(out var result))
            complete(result);
    }

    public void Cancel()
    {
        _incoming.Clear();
        if (_inputs?.Controls.TryGetValue(NodeId, out var controls) == true)
            controls.Clear();
        Enqueue("cancel", new DgLabV4NodeCommand(""));
    }

    public void SetPaused(bool paused)
    {
        _paused = paused;
        if (paused)
            Cancel();
    }

    public DgLabV4Device Snapshot(DeviceController devices)
    {
        if (!devices.Connections.TryGetValue(DeviceId, out var connection))
            throw new DgLabV4Exception("slot_not_found");
        var snapshot = DgLabV4Models.Snapshot(SlotId, connection);
        if (_paused && snapshot.SlotState is { } state)
        {
            foreach (string channel in new[] { "channelA", "channelB" })
            {
                if (state[channel] is JsonObject channelState)
                    channelState["isMuted"] = true;
            }
        }
        return snapshot;
    }

    public DgLabV4Device Publish(DeviceController devices) => _published = Snapshot(devices);

    public DgLabV4SlotPatch? CapturePatch(DeviceController devices)
    {
        var next = Snapshot(devices);
        var props = Difference(_published?.Props, next.Props);
        var state = Difference(_published?.SlotState, next.SlotState);
        _published = next;
        return props.Count == 0 && state.Count == 0 ? null : new(SlotId, props, state);
    }

    private static JsonObject Difference(JsonObject? previous, JsonObject? next)
    {
        var patch = new JsonObject();
        foreach (string key in (previous?.Select(pair => pair.Key) ?? []).Union(next?.Select(pair => pair.Key) ?? []))
        {
            var before = previous?[key];
            var after = next?[key];
            if (!JsonNode.DeepEquals(before, after))
                patch[key] = before is JsonObject oldObject && after is JsonObject newObject ? Difference(oldObject, newObject) : after?.DeepClone();
        }
        return patch;
    }
}

internal sealed record DgLabV4SlotPatch(string SlotId, JsonObject Props, JsonObject SlotState);
