using YokoWave.Core;
using YokoWave.Core.Bluetooth;
using YokoWave.Core.Localization;

namespace YokoWave.App.ViewModels;

public sealed partial class PlaybackModeViewModel
{
    private PumpOutput _pump = new();
    private DgBalanceSettings _balances = new();
    private static readonly SessionOption[] PumpOptions = Options<PumpDirection>("PumpDirection");
    public IReadOnlyList<SessionOption> PumpDirections => PumpOptions;
    public bool IsPump => Definition.IsPump;
    public bool HasStrength => !IsPump;
    public bool HasDgBalances => TargetDescriptors().Any(device => device.Profile?.Protocol == BluetoothProtocol.CoyoteV3);
    public bool HasChannelA => _targets.Count == 0 || TargetDescriptors().Any(device => device.HasChannel(0));
    public bool HasChannelC => TargetDescriptors().Any(device => device.HasChannel(2));
    public double ChannelC
    {
        get => _channelC; set
        {
            if (double.IsFinite(value))
            {
                Set(ref _channelC, Math.Clamp(value, 0, OutputLimit));
                _engine.ChannelC = _channelC;
                if (value != _channelC)
                    Raise(nameof(ChannelC));
            }
        }
    }
    private double _channelC;
    public string SelectedOutputCLabel => L10n.Format("Device.Output", Outputs().Select(output => WaveformRendering.Strength(output, 2)).DefaultIfEmpty().Max());
    public SessionOption SelectedPumpDirection
    {
        get => PumpDirections.First(option => option.Value == (int)_pump.Direction);
        set
        {
            if (value is not null && Enum.IsDefined((PumpDirection)value.Value))
                ChangePump(_pump with
                {
                    Direction = (PumpDirection)value.Value
                });
        }
    }
    public bool WaterEnabled
    {
        get => _pump.Water; set => ChangePump(_pump with { Water = value });
    }
    private void ChangePump(PumpOutput value)
    {
        if (Set(ref _pump, value, nameof(SelectedPumpDirection)))
        {
            _engine.Pump = value;
            Raise(nameof(WaterEnabled));
        }
    }
    public double FrequencyBalanceA
    {
        get => _balances.FrequencyA; set => ChangeBalance(value, b => _balances with { FrequencyA = b }, nameof(FrequencyBalanceA));
    }
    public double FrequencyBalanceB
    {
        get => _balances.FrequencyB; set => ChangeBalance(value, b => _balances with { FrequencyB = b }, nameof(FrequencyBalanceB));
    }
    public double WidthBalanceA
    {
        get => _balances.WidthA; set => ChangeBalance(value, b => _balances with { WidthA = b }, nameof(WidthBalanceA));
    }
    public double WidthBalanceB
    {
        get => _balances.WidthB; set => ChangeBalance(value, b => _balances with { WidthB = b }, nameof(WidthBalanceB));
    }
    private void ChangeBalance(double value, Func<byte, DgBalanceSettings> update, string name)
    {
        if (CanEditRouting && double.IsFinite(value))
        {
            _balances = update((byte)Math.Clamp(Math.Round(value), 0, 255));
            Raise(name);
        }
    }
    private IEnumerable<DeviceDescriptor> TargetDescriptors() => _targets.Select(id =>
        _controller.Connections.TryGetValue(id, out var connection) ? connection.Device : Device(id)?.Device).OfType<DeviceDescriptor>();
    public bool CanAssignDevice(DeviceDescriptor device) => PlaybackCompatibility.CanRun(device, Kind, _reactiveSettings);
    public bool AcceptsWaveform(WaveformPreset preset) => TargetDescriptors().All(device => PlaybackCompatibility.Accepts(device, preset));
    private string? HardwareValidationError
    {
        get
        {
            if (TargetDescriptors().Any(device => !CanAssignDevice(device)))
                return "Error.ModeDeviceMismatch";
            if (!UsesWaveforms)
                return null;
            var program = CreateSessionProgram();
            var waves = program.Playlist ?? [];
            return waves.Concat(new[] { SelectedPreset?.Preset, program.ChannelBWaveform }.OfType<WaveformPreset>())
                .Any(wave => !AcceptsWaveform(wave)) ? "Error.WaveDeviceMismatch" : null;
        }
    }
    private void RefreshHardware()
    {
        foreach (var name in new[] { nameof(HasChannelA), nameof(HasChannelC), nameof(HasDgBalances), nameof(SelectedOutputCLabel) })
            Raise(name);
    }
    private void RefreshHardwareLocalization()
    {
        foreach (var option in PumpDirections)
            option.RefreshLocalization();
        Raise(nameof(SelectedPumpDirection));
        Raise(nameof(SelectedOutputCLabel));
    }
}
