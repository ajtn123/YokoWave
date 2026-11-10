using System.Text.Json;
using System.Text.Json.Nodes;
using YokoWave.Core;
using YokoWave.Core.Bluetooth;

namespace YokoWave.Remote.DgLab.V4;

public sealed record DgLabV4Slot(string Id, string AppId, DgLabV4Device Device, DateTimeOffset CapturedAt)
{
    public bool Online
    {
        get
        {
            if (Device.SlotState?["hasDevice"] is JsonValue flag && flag.TryGetValue<bool>(out bool connected))
                return connected;
            return Device.Props?["connectState"] is JsonValue state && state.TryGetValue<string>(out string? text) && text == "connected";
        }
    }
    public DeviceDescriptor Descriptor => Device.Type switch
    {
        "COYOTE_020" => Describe(DeviceKind.DualChannel, BluetoothProtocol.CoyoteV2, 3),
        "COYOTE_030" => Describe(DeviceKind.DualChannel, BluetoothProtocol.CoyoteV3, 3),
        "OVC_1" => Describe(DeviceKind.Vibration, BluetoothProtocol.Opossum, 3),
        "BMTR_1" => Describe(DeviceKind.Sensor, BluetoothProtocol.Civet, 0),
        _ => new(Id, Device.Name, DeviceKind.Linear, null, null, ChannelMask: 0, Origin: DeviceOrigin.Remote, Simulated: false, AccessCapabilities: DeviceCapability.None)
    };
    public double Maximum => Device.Type == "COYOTE_020" ? 2047 : 200;
    public double Limit(int channel)
    {
        if (channel is < 0 or > 1)
            throw new DgLabV4Exception("invalid_params");
        var state = Device.SlotState?[channel == 0 ? "channelA" : "channelB"] as JsonObject;
        if (state?["isMuted"] is JsonValue flag && flag.TryGetValue<bool>(out bool muted) && muted)
            return 0;
        return Math.Clamp(Number(state?["intensityMax"], Maximum), 0, Maximum);
    }
    public static double Number(JsonNode? value, double fallback = 0) => value is JsonValue item && item.TryGetValue<double>(out double number) && double.IsFinite(number) ? number : fallback;
    private DeviceDescriptor Describe(DeviceKind kind, BluetoothProtocol protocol, int channels) =>
        new(Id, Device.Name, kind, Device.Props?["power"] is null ? null : (int)Math.Clamp(Number(Device.Props["power"]), 0, 100), null, protocol.ToString(), channels,
            DeviceOrigin.Remote, false, DeviceCapability.StatusQuery);
}

/// <summary>Connection-scoped identities and deep state patches, isolated by app and slot.</summary>
public sealed class DgLabV4DeviceCatalog(Guid endpoint, TimeProvider? clock = null)
{
    private readonly object _gate = new();
    private readonly Guid _generation = Guid.NewGuid();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Dictionary<(string App, string Slot), DgLabV4Slot> _slots = [];
    public event Action<IReadOnlyList<DgLabV4Slot>, IReadOnlyList<string>>? Changed;
    public IReadOnlyList<DgLabV4Slot> Slots
    {
        get { lock (_gate) return _slots.Values.Select(Clone).ToArray(); }
    }

    public DgLabV4Slot? Find(string id) => Slots.FirstOrDefault(slot => slot.Id == id);

    public void RemoveApp(string appId)
    {
        string[] removed;
        lock (_gate)
        {
            removed = _slots.Where(pair => pair.Key.App == appId).Select(pair => pair.Value.Id).ToArray();
            foreach (var key in _slots.Keys.Where(key => key.App == appId).ToArray())
                _slots.Remove(key);
        }
        Changed?.Invoke([], removed);
    }

    public void Apply(string appId, JsonElement data, bool descriptions = false)
    {
        List<DgLabV4Slot> changed = [];
        List<string> removed = [];
        lock (_gate)
        {
            string kind = descriptions ? "descriptions" : DgLabV4Protocol.String(data, "ev");
            if (kind is "devices.snapshot" or "devices.patch" or "descriptions")
            {
                string property = kind == "devices.patch" ? "added" : "devices";
                var entries = data.TryGetProperty(property, out var list) && list.ValueKind == JsonValueKind.Array
                    ? list.EnumerateArray().Select(DgLabV4Protocol.Read<DgLabV4Device>).ToArray() : [];
                if (entries.Length > 128 || entries.Any(device => string.IsNullOrWhiteSpace(device.SlotId) || device.SlotId.Length > 256 || device.Name?.Length is null or > 120))
                    throw new DgLabV4Exception("invalid_params");
                if (kind is "devices.snapshot" or "descriptions")
                {
                    foreach (var key in _slots.Keys.Where(key => key.App == appId && entries.All(device => device.SlotId != key.Slot)).ToArray())
                    {
                        removed.Add(_slots[key].Id);
                        _slots.Remove(key);
                    }
                }
                foreach (var device in entries)
                {
                    var key = (appId, device.SlotId);
                    _slots.TryGetValue(key, out var previous);
                    var info = descriptions && previous is not null ? device with
                    {
                        Props = device.Props ?? previous.Device.Props,
                        SlotState = device.SlotState ?? previous.Device.SlotState
                    } : device;
                    string id = previous?.Id ?? $"dglab:{endpoint:N}:{_generation:N}:{Uri.EscapeDataString(appId)}:{Uri.EscapeDataString(device.SlotId)}";
                    var slot = new DgLabV4Slot(id, appId, Clone(info), descriptions ? previous?.CapturedAt ?? DateTimeOffset.MinValue : _clock.GetUtcNow());
                    _slots[key] = slot;
                    changed.Add(Clone(slot));
                }
                if (data.TryGetProperty("removed", out var deleted) && deleted.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in deleted.EnumerateArray())
                    {
                        if (_slots.Remove((appId, item.GetString() ?? ""), out var slot))
                            removed.Add(slot.Id);
                    }
                }
            }
            else if (kind == "slots.patch" && data.TryGetProperty("slots", out var patches) && patches.ValueKind == JsonValueKind.Array)
            {
                foreach (var patch in patches.EnumerateArray())
                {
                    var key = (appId, DgLabV4Protocol.String(patch, "slotId"));
                    if (!_slots.TryGetValue(key, out var previous))
                        continue;
                    var props = previous.Device.Props?.DeepClone().AsObject() ?? new JsonObject();
                    var state = previous.Device.SlotState?.DeepClone().AsObject() ?? new JsonObject();
                    JsonObject? incoming = null;
                    if (patch.TryGetProperty("props", out var property) && property.ValueKind == JsonValueKind.Object)
                    {
                        incoming = JsonNode.Parse(property.GetRawText())!.AsObject();
                        DgLabV4Protocol.Merge(props, incoming);
                    }
                    if (patch.TryGetProperty("slotState", out var status) && status.ValueKind == JsonValueKind.Object)
                        DgLabV4Protocol.Merge(state, JsonNode.Parse(status.GetRawText())!.AsObject());
                    bool measurement = previous.Device.Type == "BMTR_1" ? incoming?.ContainsKey("pressure") == true ||
                        incoming?["updateTime"] is JsonValue time && time.TryGetValue<string>(out string? text) && !string.IsNullOrEmpty(text) &&
                        !JsonNode.DeepEquals(previous.Device.Props?["updateTime"], time)
                        : incoming?.ContainsKey("intensityA") == true || incoming?.ContainsKey("intensityB") == true;
                    var next = previous with { Device = previous.Device with { Props = props, SlotState = state }, CapturedAt = measurement ? _clock.GetUtcNow() : previous.CapturedAt };
                    _slots[key] = next;
                    changed.Add(Clone(next));
                }
            }
        }
        Changed?.Invoke(changed, removed);
    }

    private static DgLabV4Device Clone(DgLabV4Device value) => value with { Props = value.Props?.DeepClone().AsObject(), SlotState = value.SlotState?.DeepClone().AsObject() };
    private static DgLabV4Slot Clone(DgLabV4Slot value) => value with { Device = Clone(value.Device) };
}
