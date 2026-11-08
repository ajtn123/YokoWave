using System.Text.Json.Nodes;
using YokoWave.Core.Bluetooth;
using YokoWave.Core.Localization;

namespace YokoWave.App.ViewModels;

/// <summary>Edits the scalar fields of a typed command draft without reflecting over live view models.</summary>
public sealed class ProtocolParameter : ObservableObject
{
    private readonly Action<JsonNode?> _write;
    private readonly string _name;
    private double _number;
    private bool _enabled;
    private SessionOption? _selected;
    public string Label => L10n.Get($"ProtocolField.{_name}");
    private readonly string _group;
    public bool HasGroup => _group.Length > 0;
    public string Group => string.Join(" / ", _group.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(part =>
    {
        var pieces = part.Split(' ');
        var label = L10n.Get($"ProtocolField.{pieces[0]}");
        return pieces.Length > 1 ? L10n.Format("Protocol.FieldIndex", label, pieces[1]) : label;
    }));
    public double Minimum
    { get; private init; }
    public double Maximum
    { get; private init; }
    public bool IsNumber
    { get; private init; }
    public bool IsBoolean
    { get; private init; }
    public bool IsChoice => Choices.Count > 0;
    public IReadOnlyList<SessionOption> Choices
    { get; private init; } = [];
    private string[] _enumNames = [];
    public double Number
    {
        get => _number;
        set
        {
            if (double.IsFinite(value) && Set(ref _number, Math.Clamp(Math.Round(value), Minimum, Maximum))) _write(JsonValue.Create((int)_number));
        }
    }
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (Set(ref _enabled, value)) _write(JsonValue.Create(value));
        }
    }
    public SessionOption? Selected
    {
        get => _selected;
        set
        {
            if (value is not null && Set(ref _selected, value)) _write(JsonValue.Create(_enumNames[value.Value]));
        }
    }
    private ProtocolParameter(string name, string group, Action<JsonNode?> write)
    {
        _name = name;
        _group = group;
        _write = write;
    }
    internal static ProtocolParameter NumberField(string name, string group, double value, double minimum, double maximum, Action<JsonNode?> write) =>
        new(name, group, write) { _number = value, IsNumber = true, Minimum = minimum, Maximum = maximum };
    internal static ProtocolParameter BooleanField(string name, string group, bool value, Action<JsonNode?> write) =>
        new(name, group, write) { _enabled = value, IsBoolean = true };
    internal static ProtocolParameter ChoiceField(string name, string group, string value, string[] names, Action<JsonNode?> write)
    {
        var parameter = new ProtocolParameter(name, group, write)
        {
            _enumNames = names,
            Choices = names.Select((entry, index) => new SessionOption(index, $"ProtocolEnum.{name}.{entry}")).ToArray()
        };
        parameter._selected = parameter.Choices.First(option => names[option.Value] == value);
        return parameter;
    }
    public void Refresh()
    {
        foreach (var option in Choices) option.RefreshLocalization();
        Raise(nameof(Label));
        Raise(nameof(Group));
        Raise(nameof(Selected));
    }
}

internal static class ProtocolParameterForm
{
    public static IReadOnlyList<ProtocolParameter> Create(string json, string commandKey, BluetoothDeviceProfile? profile, Action<string> update)
    {
        var root = JsonNode.Parse(json)!;
        List<ProtocolParameter> fields = [];
        void Visit(JsonNode node, string group)
        {
            if (node is JsonArray array)
            {
                for (var index = 0; index < array.Count; index++)
                    if (array[index] is { } child) Visit(child, $"{group} {index + 1}");
                return;
            }
            if (node is not JsonObject obj) return;
            foreach (var (name, value) in obj)
            {
                if (value is JsonObject or JsonArray)
                {
                    Visit(value, group.Length == 0 ? name : $"{group} / {name}");
                    continue;
                }
                if (value is null) continue;
                void Write(JsonNode? updated)
                {
                    obj[name] = updated;
                    update(root.ToJsonString());
                }
                var displayGroup = group.Replace("Trigger / ", "").Replace("Trigger", "");
                if (value.GetValueKind() == System.Text.Json.JsonValueKind.True || value.GetValueKind() == System.Text.Json.JsonValueKind.False)
                    fields.Add(ProtocolParameter.BooleanField(name, displayGroup, value.GetValue<bool>(), Write));
                else if (value.GetValueKind() == System.Text.Json.JsonValueKind.Number)
                {
                    var (min, max) = Range(name, group, commandKey);
                    fields.Add(ProtocolParameter.NumberField(name, displayGroup, value.GetValue<double>(), min, max, Write));
                }
                else if (Enums(name) is { } names)
                    fields.Add(ProtocolParameter.ChoiceField(name, displayGroup, value.GetValue<string>(),
                        name == "Query" && profile is not null ? profile.SupportedQueries.Select(query => query.ToString()).ToArray() : names, Write));
            }
        }
        Visit(root, "");
        return fields;
    }
    private static string[]? Enums(string field) => field switch
    {
        "Query" => Enum.GetNames<DeviceQuery>(),
        "Action" => Enum.GetNames<StepCounterAction>(),
        "Blink" => Enum.GetNames<ShoulderBlink>(),
        "Orientation" => Enum.GetNames<SensorOrientation>(),
        _ => null
    };
    private static (int Minimum, int Maximum) Range(string field, string group, string command) => field switch
    {
        "IndicatorColor" => (1, 6),
        "Color" => command == "Indicator" ? (1, 6) : (0, 7),
        "AlternateColor" => (0, 7),
        "EventId" => (group.Contains("Motion", StringComparison.Ordinal) ? 0 : 1, 24),
        "ButtonEventId" => (0, 24),
        "Probability" => (0, 200),
        "MinimumSeconds" or "MaximumSeconds" or "ReactionSeconds" => (1, 65535),
        "CooldownSeconds" => (0, 65535),
        "IncreaseSeconds" or "DecreaseSeconds" => (0, 120),
        "AccelerationThreshold" => (0, 400),
        "TriggerDebounce" or "CancelDebounce" => (0, 7),
        "Minimum" or "Maximum" => (-128, 127),
        _ => (0, 255)
    };
}
