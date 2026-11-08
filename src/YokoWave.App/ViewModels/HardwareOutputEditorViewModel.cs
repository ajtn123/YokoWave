using YokoWave.Core;
using YokoWave.Core.Bluetooth;

namespace YokoWave.App.ViewModels;

public sealed class HardwareOutputEditorViewModel : ObservableObject
{
    private HardwareOutputSettings _settings;
    private readonly Func<bool> _canEdit;
    public HardwareOutputSettings Settings => _settings;
    public IReadOnlyList<SessionOption> AuxiliaryModes
    { get; } = Enum.GetValues<AuxiliaryMotorMode>()
        .Select(mode => new SessionOption((int)mode, $"AuxiliaryMotor.{mode}")).ToArray();
    public SessionOption SelectedAuxiliaryMode
    {
        get => AuxiliaryModes.First(option => option.Value == (int)_settings.AuxiliaryMotor);
        set
        {
            if (value is not null) Change(_settings with { AuxiliaryMotor = (AuxiliaryMotorMode)value.Value });
        }
    }
    public bool RelativeStrength
    {
        get => _settings.RelativeStrength;
        set => Change(_settings with { RelativeStrength = value });
    }
    public double LimitA
    {
        get => _settings.ChannelALimit;
        set => Change(_settings with { ChannelALimit = value });
    }
    public double LimitB
    {
        get => _settings.ChannelBLimit;
        set => Change(_settings with { ChannelBLimit = value });
    }
    public HardwareOutputEditorViewModel(HardwareOutputSettings? settings, Func<bool> canEdit)
    {
        _settings = settings ?? new();
        _canEdit = canEdit;
    }
    private void Change(HardwareOutputSettings value)
    {
        if (!_canEdit()) return;
        value.Validate();
        if (Set(ref _settings, value, nameof(Settings))) Raise(nameof(SelectedAuxiliaryMode));
    }
    public void RefreshLocalization()
    {
        foreach (var option in AuxiliaryModes) option.RefreshLocalization();
        Raise(nameof(SelectedAuxiliaryMode));
    }
}
