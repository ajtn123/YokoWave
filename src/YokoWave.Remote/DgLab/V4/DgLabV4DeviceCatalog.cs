using System.Text.Json;
using System.Text.Json.Nodes;

namespace YokoWave.Remote.DgLab.V4;

/// <summary>Connection-scoped identities, atomic device events and revision-checked query responses.</summary>
public sealed class DgLabV4DeviceCatalog(Guid endpoint, TimeProvider? clock = null)
{
    private readonly object _gate = new();
    private readonly Guid _generation = Guid.NewGuid();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Dictionary<(string App, string Slot), DgLabV4Slot> _slots = [];
    private readonly Dictionary<string, long> _revisions = [];
    public event Action<IReadOnlyList<DgLabV4Slot>, IReadOnlyList<string>>? Changed;

    public IReadOnlyList<DgLabV4Slot> Snapshot()
    {
        lock (_gate)
            return _slots.Values.Select(Clone).ToArray();
    }

    public DgLabV4Slot? Find(string id)
    {
        lock (_gate)
            return _slots.Values.FirstOrDefault(slot => slot.Id == id) is { } slot ? Clone(slot) : null;
    }

    public long Revision(string appId)
    {
        lock (_gate)
            return _revisions.GetValueOrDefault(appId);
    }

    public void RemoveApp(string appId)
    {
        string[] removed;
        lock (_gate)
        {
            removed = _slots.Where(pair => pair.Key.App == appId).Select(pair => pair.Value.Id).ToArray();
            foreach (var key in _slots.Keys.Where(key => key.App == appId).ToArray())
                _slots.Remove(key);
            Advance(appId);
        }
        Changed?.Invoke([], removed);
    }

    public void Clear()
    {
        string[] removed;
        lock (_gate)
        {
            removed = _slots.Values.Select(slot => slot.Id).ToArray();
            foreach (string appId in _slots.Keys.Select(key => key.App).Distinct().ToArray())
                Advance(appId);
            _slots.Clear();
        }
        Changed?.Invoke([], removed);
    }

    public bool ApplyDescriptions(string appId, JsonElement data, long expectedRevision)
    {
        var entries = Devices(data, "devices", required: true);
        CatalogChange change;
        lock (_gate)
        {
            if (_revisions.GetValueOrDefault(appId) != expectedRevision)
                return false;
            change = Replace(appId, entries, descriptions: true);
            Advance(appId);
        }
        Changed?.Invoke(change.Changed, change.Removed);
        return true;
    }

    public void ApplyEvent(string appId, JsonElement data)
    {
        string kind = DgLabV4Protocol.String(data, "ev");
        CatalogChange change;
        lock (_gate)
        {
            change = kind switch
            {
                "devices.snapshot" => Replace(appId, Devices(data, "devices", required: true), descriptions: false),
                "devices.patch" => PatchDevices(appId, data),
                "slots.patch" => PatchSlots(appId, data),
                _ => throw new DgLabV4Exception("invalid_params")
            };
            Advance(appId);
        }
        Changed?.Invoke(change.Changed, change.Removed);
    }

    private CatalogChange Replace(string appId, DgLabV4Device[] devices, bool descriptions)
    {
        var removed = _slots.Keys.Where(key => key.App == appId && devices.All(device => device.SlotId != key.Slot)).ToArray();
        string[] ids = removed.Select(key => _slots[key].Id).ToArray();
        foreach (var key in removed)
            _slots.Remove(key);
        return new(devices.Select(device => Upsert(appId, device, descriptions)).ToArray(), ids);
    }

    private CatalogChange PatchDevices(string appId, JsonElement data)
    {
        var added = Devices(data, "added", required: false);
        string[] deleted = Array(data, "removed", required: false).Select(item => item.ValueKind == JsonValueKind.String
            ? item.GetString()! : throw new DgLabV4Exception("invalid_params")).ToArray();
        if (_slots.Keys.Where(key => key.App == appId).Select(key => key.Slot).Except(deleted).Union(added.Select(device => device.SlotId)).Count() > 128)
            throw new DgLabV4Exception("invalid_params");
        List<string> removed = [];
        foreach (string slotId in deleted)
        {
            if (_slots.Remove((appId, slotId), out var slot))
                removed.Add(slot.Id);
        }
        return new(added.Select(device => Upsert(appId, device, descriptions: false)).ToArray(), removed);
    }

    private CatalogChange PatchSlots(string appId, JsonElement data)
    {
        var patches = Array(data, "slots", required: false).Select(patch => new
        {
            Slot = DgLabV4Protocol.String(patch, "slotId"),
            Props = Object(patch, "props"),
            State = Object(patch, "slotState")
        }).ToArray();
        List<DgLabV4Slot> changed = [];
        foreach (var patch in patches)
        {
            var key = (appId, patch.Slot);
            if (!_slots.TryGetValue(key, out var previous))
                continue;
            var props = previous.Device.Props?.DeepClone().AsObject() ?? new JsonObject();
            var state = previous.Device.SlotState?.DeepClone().AsObject() ?? new JsonObject();
            DgLabV4Protocol.Merge(props, patch.Props);
            DgLabV4Protocol.Merge(state, patch.State);
            var device = previous.Device with { Props = props, SlotState = state };
            var captured = CapturedAt(device, patch.Props, previous);
            var next = previous with { Device = device, CapturedAt = captured };
            _slots[key] = next;
            changed.Add(Clone(next));
        }
        return new(changed, []);
    }

    private DgLabV4Slot Upsert(string appId, DgLabV4Device device, bool descriptions)
    {
        var key = (appId, device.SlotId);
        _slots.TryGetValue(key, out var previous);
        var info = descriptions && previous is not null ? device with
        {
            Props = device.Props ?? previous.Device.Props,
            SlotState = device.SlotState ?? previous.Device.SlotState
        } : device;
        string id = previous?.Id ?? $"dglab:{endpoint:N}:{_generation:N}:{Uri.EscapeDataString(appId)}:{Uri.EscapeDataString(device.SlotId)}";
        var captured = descriptions ? previous?.CapturedAt ?? DateTimeOffset.MinValue : CapturedAt(info, info.Props, null);
        var slot = new DgLabV4Slot(id, appId, Clone(info), captured);
        _slots[key] = slot;
        return Clone(slot);
    }

    private DateTimeOffset CapturedAt(DgLabV4Device device, JsonObject? incoming, DgLabV4Slot? previous)
    {
        if (device.Type != "BMTR_1")
            return incoming?.ContainsKey("intensityA") == true || incoming?.ContainsKey("intensityB") == true ? _clock.GetUtcNow() : previous?.CapturedAt ?? DateTimeOffset.MinValue;
        bool measurement = incoming?.ContainsKey("pressure") == true ||
            DgLabV4Slot.Text(incoming?["updateTime"]) is { Length: > 0 } && !JsonNode.DeepEquals(previous?.Device.Props?["updateTime"], incoming?["updateTime"]);
        if (!measurement)
            return previous?.CapturedAt ?? DateTimeOffset.MinValue;
        return DgLabV4Slot.Numeric(device.Props?["pressure"]) is not null ? _clock.GetUtcNow() : DateTimeOffset.MinValue;
    }

    private void Advance(string appId) => _revisions[appId] = _revisions.GetValueOrDefault(appId) + 1;

    private static DgLabV4Device[] Devices(JsonElement data, string property, bool required)
    {
        var devices = Array(data, property, required).Select(DgLabV4Protocol.Read<DgLabV4Device>).ToArray();
        if (devices.Any(device => string.IsNullOrWhiteSpace(device.SlotId) || device.SlotId.Length > 256 || device.Name.Length > 120 || device.Type.Length > 64) ||
            devices.Select(device => device.SlotId).Distinct(StringComparer.Ordinal).Count() != devices.Length)
            throw new DgLabV4Exception("invalid_params");
        return devices;
    }

    private static JsonElement[] Array(JsonElement data, string property, bool required)
    {
        if (data.ValueKind != JsonValueKind.Object)
            throw new DgLabV4Exception("invalid_params");
        if (!data.TryGetProperty(property, out var value) && !required)
            return [];
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 128)
            throw new DgLabV4Exception("invalid_params");
        return value.EnumerateArray().ToArray();
    }

    private static JsonObject Object(JsonElement data, string property)
    {
        if (!data.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null)
            return new();
        if (value.ValueKind != JsonValueKind.Object)
            throw new DgLabV4Exception("invalid_params");
        return JsonNode.Parse(value.GetRawText())!.AsObject();
    }

    private static DgLabV4Device Clone(DgLabV4Device value) => value with { Props = value.Props?.DeepClone().AsObject(), SlotState = value.SlotState?.DeepClone().AsObject() };
    private static DgLabV4Slot Clone(DgLabV4Slot value) => value with { Device = Clone(value.Device) };
    private sealed record CatalogChange(IReadOnlyList<DgLabV4Slot> Changed, IReadOnlyList<string> Removed);
}
