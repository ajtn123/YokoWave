using YokoWave.Core;
using YokoWave.Pipelines;

namespace YokoWave.App.ViewModels.Pipelines;

public sealed class OutputOptionsViewModel : ObservableObject
{
    private readonly Func<bool> _canEdit;
    private readonly Action _changed;
    private bool _gentle;
    private DgBalanceSettings _balances;
    public HardwareOutputEditorViewModel Hardware { get; }
    public bool Gentle
    {
        get => _gentle; set
        {
            if (_canEdit() && Set(ref _gentle, value))
                _changed();
        }
    }
    public DgBalanceSettings Balances => _balances;
    public int FrequencyA
    {
        get => _balances.FrequencyA; set => Balance(_balances with { FrequencyA = (byte)Math.Clamp(value, 0, 255) });
    }
    public int FrequencyB
    {
        get => _balances.FrequencyB; set => Balance(_balances with { FrequencyB = (byte)Math.Clamp(value, 0, 255) });
    }
    public int WidthA
    {
        get => _balances.WidthA; set => Balance(_balances with { WidthA = (byte)Math.Clamp(value, 0, 255) });
    }
    public int WidthB
    {
        get => _balances.WidthB; set => Balance(_balances with { WidthB = (byte)Math.Clamp(value, 0, 255) });
    }
    public OutputOptionsViewModel(bool gentle, HardwareOutputSettings? hardware, DgBalanceSettings? balances, Func<bool> canEdit, Action changed)
    {
        _gentle = gentle;
        _balances = balances ?? new();
        _canEdit = canEdit;
        _changed = changed;
        Hardware = new(hardware, canEdit);
        Hardware.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Hardware.Settings)) _changed(); };
    }
    private void Balance(DgBalanceSettings value)
    {
        if (!_canEdit() || _balances == value)
            return;
        _balances = value;
        _changed();
    }
    public void RefreshLocalization() => Hardware.RefreshLocalization();
}

public sealed class NativeOutputEditorViewModel : DeviceOutputEditorViewModel
{
    private NativeOutputNode _settings;
    private NativeProgramSettings _program;
    public NativeProgramEditorViewModel NativeEditor { get; }
    public OutputOptionsViewModel Options { get; }
    public NativeOutputEditorViewModel(PipelineEditorViewModel editor, NodeDefinition definition) : base(editor, definition,
        ((NativeOutputNode)editor.Registry.Get(definition.Type).Read(definition.Configuration)).DeviceId)
    {
        _settings = (NativeOutputNode)editor.Registry.Get(Type).Read(definition.Configuration);
        _program = _settings.Settings ?? new();
        Options = new(_settings.Gentle, _settings.Hardware, _settings.Balances, () => CanEdit, SaveOptions);
        NativeEditor = new(_program, () => CanEdit);
        NativeEditor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(NativeEditor.Settings))
                return;
            if (NativeEditor.ValidationError is null)
                Change(() => _program = NativeEditor.Settings);
            Raise(nameof(ValidationError));
            Editor.Validate();
        };
    }
    private void SaveOptions() => Change(() => _settings = _settings with { Gentle = Options.Gentle, Hardware = Options.Hardware.Settings, Balances = Options.Balances });
    public string ValidationError => NativeEditor.ValidationError is { } key ? Core.Localization.L10n.Get(key) : "";
    public override NodeDefinition ToDefinition() => NodeDefinition.Create(Type, _settings with
    {
        DeviceId = DeviceId,
        Settings = _program
    }, Id);
    public override void RefreshLocalization()
    {
        base.RefreshLocalization();
        NativeEditor.Refresh();
        Options.RefreshLocalization();
        Raise(nameof(ValidationError));
    }
}
