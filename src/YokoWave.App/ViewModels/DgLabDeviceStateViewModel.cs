using System.Text.Json.Nodes;
using YokoWave.Core.Localization;

namespace YokoWave.App.ViewModels;

/// <summary>Read-only public App state; fields absent from the App are omitted.</summary>
public sealed class DgLabDeviceStateViewModel(Func<DgLabV4Slot?> selected) : ObservableObject
{
    private static readonly string[] _channelFields = ["isMuted", "warmUpScale", "intensityMax", "comfortLimit.mode", "comfortLimit.comfortMax",
        "comfortLimit.absoluteMax", "comfortLimit.overheat", "comfortLimit.overheatPercent", "comfortLimit.autoIncr", "comfortLimit.autoIncrMax",
        "comfortLimit.autoIncrScope", "comfortLimit.totalIncr"];
    private static readonly string[] _propertyFields = ["label", "channelAStatus", "channelBStatus", "mode", "updateTime", "updateValue"];
    public bool IsVisible => selected() is not null;
    public IReadOnlyList<TelemetryRow> Readings
    {
        get
        {
            if (selected() is not { } slot)
                return [];
            List<TelemetryRow> rows = [];
            foreach (string propertyName in _propertyFields)
                Add(slot.Device.Props, propertyName, "DgLab.State." + propertyName);
            Add(slot.Device.SlotState, "hasDevice", "DgLab.State.hasDevice");
            Add(slot.Device.SlotState, "markLight", "DgLab.State.markLight");
            Add(slot.Device.SlotState, "edge.edgeState", "DgLab.State.edgeState");
            foreach (string channel in new[] { "A", "B" })
            {
                foreach (string propertyName in _channelFields)
                {
                    var state = slot.Device.SlotState?["channel" + channel] as JsonObject;
                    string label = L10n.Format("DgLab.ChannelProperty", L10n.Get("Channel." + channel), L10n.Get("DgLab.State." + propertyName));
                    Add(state, propertyName, label, localized: true);
                }
            }
            return rows;

            void Add(JsonObject? root, string path, string label, bool localized = false)
            {
                JsonNode? value = root;
                foreach (string segment in path.Split('.'))
                    value = (value as JsonObject)?[segment];
                if (value is not JsonValue scalar)
                    return;
                string text = DisplayValue(path, scalar);
                if (text.Length > 0)
                    rows.Add(new(localized ? label : L10n.Get(label), text));
            }
        }
    }

    public void Refresh()
    {
        Raise(nameof(IsVisible));
        Raise(nameof(Readings));
    }

    private static string DisplayValue(string path, JsonValue value)
    {
        if (value.TryGetValue<bool>(out bool flag))
            return L10n.Get(flag ? "DgLab.State.True" : "DgLab.State.False");
        if (DgLabV4Slot.Numeric(value) is { } number)
        {
            string? group = path switch
            {
                "channelAStatus" or "channelBStatus" when number is >= 0 and <= 4 => "ChannelStatus",
                "edge.edgeState" when number is >= 0 and <= 4 => "EdgeState",
                "mode" when number is >= 0 and <= 3 => "Mode",
                "comfortLimit.autoIncrScope" when number is >= 1 and <= 3 => "Scope",
                _ => null
            };
            return group is not null && number == Math.Truncate(number) ? L10n.Get($"DgLab.Value.{group}{(int)number}") : number.ToString("0.###", L10n.Current.Culture);
        }
        string text = DgLabV4Slot.Text(value) ?? "";
        return path is "markLight" or "comfortLimit.mode" && text is "yellow" or "green" or "red" or "purple" or "blue" or "cyan" or "simple" or "complex"
            ? L10n.Get("DgLab.Value." + text) : text;
    }
}
