using System.Text.Json;
using System.Text.Json.Nodes;
using YokoWave.Core;
using YokoWave.Pipelines;

namespace YokoWave.App.ViewModels.Pipelines;

/// <summary>Scalar configuration drafts share the same field controls as protocol command drafts.</summary>
internal static class NodeParameterForm
{
    public static IReadOnlyList<ParameterFieldViewModel> Create(NodeDefinition definition, Action<string> changed)
    {
        if (definition.Type is not (BuiltInNodes.RangeMap or BuiltInNodes.Counter or BuiltInNodes.Cycle or BuiltInNodes.Automation or
            BuiltInNodes.Mix or BuiltInNodes.Smooth or BuiltInNodes.Threshold or BuiltInNodes.Touch))
            return [];
        var root = JsonNode.Parse(definition.Configuration.GetRawText())!;
        List<ParameterFieldViewModel> fields = [];
        void Visit(JsonNode node)
        {
            if (node is not JsonObject obj)
                return;
            foreach (var (name, value) in obj)
            {
                if (value is JsonObject)
                {
                    Visit(value);
                    continue;
                }
                if (value is null)
                    continue;
                void Write(JsonNode? updated)
                {
                    obj[name] = updated;
                    changed(root.ToJsonString());
                }
                var kind = value.GetValueKind();
                if (kind is JsonValueKind.True or JsonValueKind.False)
                    fields.Add(ParameterFieldViewModel.BooleanSetting(name, value.GetValue<bool>(), Write));
                else if (kind == JsonValueKind.Number)
                {
                    var range = Range(name);
                    fields.Add(ParameterFieldViewModel.NumericSetting(name, value.GetValue<double>(), range.Min, range.Max, name == "Goal", Write));
                }
                else if (name == "Mode")
                    fields.Add(ParameterFieldViewModel.ChoiceSetting(name, value.GetValue<string>(), Enum.GetNames<StrengthMode>(), "StrengthMode", Write));
                else if (name == "Playback")
                    fields.Add(ParameterFieldViewModel.ChoiceSetting(name, value.GetValue<string>(), Enum.GetNames<TouchPlayback>(), "TouchMode", Write));
            }
        }
        Visit(root);
        return fields;
    }
    private static (double Min, double Max) Range(string name) => name switch
    {
        "Goal" => (0, 1000000),
        "Threshold" => (-1000000, 1000000),
        "WaitSeconds" or "MinimumWait" or "MaximumWait" => (1, 7200),
        "BurstSeconds" or "MinimumBurst" or "MaximumBurst" => (1, 60),
        "IntervalSeconds" => (1, 600),
        "Seconds" => (0, 60),
        "RandomAddition" or "Step" or "Ceiling" or "Boost" => (0, 100),
        "MinimumStrength" or "MaximumStrength" or "Amount" => (0, 1),
        _ => (-1000000, 1000000)
    };
}
