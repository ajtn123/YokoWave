using YokoWave.Core;
using YokoWave.Core.Localization;

namespace YokoWave.App.ViewModels;

public sealed class NativeProgramEditorViewModel : ObservableObject
{
    private NativeProgramSettings _settings;
    private readonly Func<bool> _canEdit;
    public FrequencySequenceEditorViewModel BufferA { get; }
    public FrequencySequenceEditorViewModel BufferB { get; }
    public System.Collections.ObjectModel.ObservableCollection<SessionOption> Kinds { get; } = new(Enum.GetValues<NativeProgramKind>()
        .Select(kind => new SessionOption((int)kind, $"NativeKind.{kind}")));
    public SessionOption? SelectedKind
    {
        get => Kinds.FirstOrDefault(option => option.Value == (int)_settings.Kind);
        set
        {
            if (!_canEdit() || value is null)
                return;
            Change(_settings with { Kind = (NativeProgramKind)value.Value });
            Refresh();
        }
    }
    public bool IsPreset => _settings.Kind is NativeProgramKind.ElectricalPreset or NativeProgramKind.MotorPreset;
    public bool IsMotor => _settings.Kind == NativeProgramKind.MotorPreset;
    public bool IsBuffered => _settings.Kind == NativeProgramKind.ElectricalBuffered;
    public bool IsPulse => _settings.Kind == NativeProgramKind.CoyoteV2Pulse;
    public int MaximumPreset => IsMotor ? 255 : 16;
    public int PresetA
    { get => _settings.PresetA; set => Change(_settings with { PresetA = value }); }
    public int PresetB
    { get => _settings.PresetB; set => Change(_settings with { PresetB = value }); }
    public int PresetC
    { get => _settings.PresetC; set => Change(_settings with { PresetC = value }); }
    private CoyotePulse A => _settings.PulseA ?? new();
    private CoyotePulse B => _settings.PulseB ?? new();
    public int XA
    { get => A.X; set => Change(_settings with { PulseA = A with { X = value } }); }
    public int YA
    { get => A.Y; set => Change(_settings with { PulseA = A with { Y = value } }); }
    public int ZA
    { get => A.Z; set => Change(_settings with { PulseA = A with { Z = value } }); }
    public int XB
    { get => B.X; set => Change(_settings with { PulseB = B with { X = value } }); }
    public int YB
    { get => B.Y; set => Change(_settings with { PulseB = B with { Y = value } }); }
    public int ZB
    { get => B.Z; set => Change(_settings with { PulseB = B with { Z = value } }); }
    public bool Repeat
    { get => _settings.Repeat; set => Change(_settings with { Repeat = value }); }
    public NativeProgramSettings Settings
    {
        get
        {
            var settings = IsBuffered ? _settings with
            {
                BufferA = BufferA.Snapshot(),
                BufferB = BufferB.Snapshot()
            } : _settings;
            settings.Validate();
            return settings.Snapshot();
        }
    }
    public string? ValidationError
    {
        get
        {
            try
            {
                _ = Settings;
                return null;
            }
            catch (LocalizedArgumentException)
            {
                return "Error.ProtocolSettings";
            }
        }
    }
    public NativeProgramEditorViewModel(NativeProgramSettings? settings, Func<bool> canEdit)
    {
        _settings = settings ?? new();
        _canEdit = canEdit;
        BufferA = new(_settings.BufferA ?? [new FrequencyPulse()], canEdit, () => Raise(nameof(Settings)));
        BufferB = new(_settings.BufferB ?? [new FrequencyPulse()], canEdit, () => Raise(nameof(Settings)));
    }
    public void ConfigureDevice(DeviceDescriptor? device)
    {
        var kinds = Enum.GetValues<NativeProgramKind>().Where(kind => device is null || new NativeProgramSettings(kind).Accepts(device)).ToArray();
        if (Kinds.Select(k => k.Value).SequenceEqual(kinds.Select(k => (int)k)))
            return;
        Kinds.Clear();
        foreach (var kind in kinds)
            Kinds.Add(new((int)kind, "NativeKind." + kind));
        Raise(nameof(SelectedKind));
    }
    public void Refresh()
    {
        foreach (var option in Kinds)
            option.RefreshLocalization();
        foreach (string? name in new[] { nameof(SelectedKind), nameof(IsPreset), nameof(IsMotor), nameof(IsBuffered), nameof(IsPulse), nameof(MaximumPreset) })
            Raise(name);
    }
    private void Change(NativeProgramSettings settings)
    {
        if (!_canEdit() || _settings == settings)
            return;
        _settings = settings;
        Raise(nameof(Settings));
    }
}
