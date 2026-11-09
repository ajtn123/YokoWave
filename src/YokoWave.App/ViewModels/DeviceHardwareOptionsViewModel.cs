using YokoWave.Core;
using YokoWave.Core.Bluetooth;

namespace YokoWave.App.ViewModels;

public sealed class DeviceHardwareOptionsViewModel : ObservableObject
{
    private DeviceOutputSettings _settings;
    private int _pendingUpdates;
    private readonly Func<DeviceOutputSettings, Task> _persist;
    private readonly Action<Exception> _report;
    private readonly DeviceController _controller;
    private readonly DeviceDescriptor _device;
    private readonly Func<bool> _canEdit;
    public bool CanEdit => _canEdit();
    public bool HasGentle => _device.Kind is DeviceKind.DualChannel or DeviceKind.Vibration or DeviceKind.Linear;
    public bool HasAuxiliary => Supports(DeviceCapability.AuxiliaryMotor);
    public bool HasRelative => Supports(DeviceCapability.RelativeStrength);
    public bool HasLimits => Supports(DeviceCapability.ChannelLimits);
    public bool HasBalances => Supports(DeviceCapability.Balance);
    public bool HasOptions => HasGentle || HasAuxiliary || HasRelative || HasLimits || HasBalances;
    private bool Supports(DeviceCapability capability) => _device.Profile?.Supports(capability) == true;
    private HardwareOutputSettings Hardware => _settings.Hardware ?? new();
    private DgBalanceSettings Balances => _settings.Balances ?? new();
    public IReadOnlyList<SessionOption> AuxiliaryModes { get; } = Enum.GetValues<AuxiliaryMotorMode>().Select(m => new SessionOption((int)m, "AuxiliaryMotor." + m)).ToArray();
    public SessionOption AuxiliaryMode { get => AuxiliaryModes.Single(m => m.Value == (int)Hardware.AuxiliaryMotor); set { if (value is not null) Update(_settings with { Hardware = Hardware with { AuxiliaryMotor = (AuxiliaryMotorMode)value.Value } }); } }
    public bool Gentle { get => _settings.Gentle; set => Update(_settings with { Gentle = value }); }
    public bool RelativeStrength { get => Hardware.RelativeStrength; set => Update(_settings with { Hardware = Hardware with { RelativeStrength = value } }); }
    public double LimitA { get => Hardware.ChannelALimit; set { if (double.IsFinite(value)) Update(_settings with { Hardware = Hardware with { ChannelALimit = Math.Clamp(value, 0, 100) } }); } }
    public double LimitB { get => Hardware.ChannelBLimit; set { if (double.IsFinite(value)) Update(_settings with { Hardware = Hardware with { ChannelBLimit = Math.Clamp(value, 0, 100) } }); } }
    public int FrequencyA { get => Balances.FrequencyA; set => Update(_settings with { Balances = Balances with { FrequencyA = (byte)Math.Clamp(value, 0, 255) } }); }
    public int FrequencyB { get => Balances.FrequencyB; set => Update(_settings with { Balances = Balances with { FrequencyB = (byte)Math.Clamp(value, 0, 255) } }); }
    public int WidthA { get => Balances.WidthA; set => Update(_settings with { Balances = Balances with { WidthA = (byte)Math.Clamp(value, 0, 255) } }); }
    public int WidthB { get => Balances.WidthB; set => Update(_settings with { Balances = Balances with { WidthB = (byte)Math.Clamp(value, 0, 255) } }); }
    public DeviceHardwareOptionsViewModel(DeviceDescriptor device, DeviceController controller, Func<bool> canEdit, Func<DeviceOutputSettings, Task> persist, Action<Exception> report)
    {
        _device = device;
        _controller = controller;
        _settings = controller.GetOutputSettings(device.Id);
        _canEdit = canEdit;
        _persist = persist;
        _report = report;
    }
    private void Update(DeviceOutputSettings settings)
    {
        if (!CanEdit || settings == _settings)
            return;
        settings.Validate();
        if (!settings.Accepts(_device))
            return;
        _settings = settings;
        _pendingUpdates++;
        _ = PersistAsync(settings);
        RaiseSettings();
    }
    private async Task PersistAsync(DeviceOutputSettings settings)
    {
        try
        {
            await _persist(settings);
        }
        catch (Exception error) { _report(error); }
        finally
        {
            _pendingUpdates--;
            Refresh();
        }
    }
    private void RaiseSettings()
    {
        foreach (var property in new[] { nameof(Gentle), nameof(RelativeStrength), nameof(LimitA), nameof(LimitB), nameof(FrequencyA), nameof(FrequencyB), nameof(WidthA), nameof(WidthB), nameof(AuxiliaryMode) })
            Raise(property);
    }
    public void Refresh()
    {
        var stored = _controller.GetOutputSettings(_device.Id);
        if (_pendingUpdates == 0 && _settings != stored)
        {
            _settings = stored;
            RaiseSettings();
        }
        Raise(nameof(CanEdit));
        foreach (var mode in AuxiliaryModes)
            mode.RefreshLocalization();
        Raise(nameof(AuxiliaryMode));
    }
}
