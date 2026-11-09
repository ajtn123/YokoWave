using YokoWave.Core;

namespace YokoWave.App.ViewModels;

public sealed class FrequencyPulseRow(FrequencyPulse value, Func<bool> canEdit, Action changed) : ObservableObject
{
    private FrequencyPulse _value = value;
    public FrequencyPulse Value => _value;
    public int FrequencyHz { get => _value.FrequencyHz; set => Update(_value with { FrequencyHz = Math.Clamp(value, 1, 100) }); }
    public int PulseWidth { get => _value.PulseWidth; set => Update(_value with { PulseWidth = Math.Clamp(value, 0, 100) }); }
    private void Update(FrequencyPulse value)
    {
        if (!canEdit() || value == _value)
            return;
        _value = value;
        Raise(nameof(FrequencyHz));
        Raise(nameof(PulseWidth));
        changed();
    }
}

public sealed class FrequencySequenceEditorViewModel(IEnumerable<FrequencyPulse> values, Func<bool> canEdit, Action changed)
    : OrderedRowsEditorViewModel<FrequencyPulseRow>(values.Select(v => new FrequencyPulseRow(v, canEdit, changed)), () => new(new(), canEdit, changed), 1, 100, canEdit, changed)
{
    public FrequencyPulse[] Snapshot() => Rows.Select(r => r.Value).ToArray();
}
