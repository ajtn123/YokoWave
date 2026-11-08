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
    public bool HasStrength => !IsPump && !IsNativeMotor && !IsServerMode;
    public bool HasDgBalances => !IsServerMode && TargetDescriptors().Any(device => device.Profile?.Protocol == BluetoothProtocol.CoyoteV3);
    public bool HasChannelA => _targets.Count == 0 || TargetDescriptors().Any(device => device.HasChannel(0));
    public bool HasChannelC => !IsServerMode && TargetDescriptors().Any(device => device.HasChannel(2));
    public double ChannelC
    {
        get => _channelC;
        set
        {
            if (double.IsFinite(value))
            {
                Set(ref _channelC, Math.Clamp(value, 0, OutputLimit));
                _engine.ChannelC = _channelC;
                RefreshNativeStrength(nameof(NativeEnabledC));
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
    public int PeristalticSeconds
    {
        get => _pump.PeristalticSeconds;
        set => ChangePump(_pump with { PeristalticSeconds = value });
    }
    public int WaterSeconds
    {
        get => _pump.WaterSeconds;
        set => ChangePump(_pump with { WaterSeconds = value });
    }
    public bool WaterEnabled
    {
        get => _pump.Water;
        set => ChangePump(_pump with { Water = value });
    }
    private void ChangePump(PumpOutput value)
    {
        value.Validate();
        if (Set(ref _pump, value, nameof(SelectedPumpDirection)))
        {
            _engine.Pump = value;
            Raise(nameof(WaterEnabled));
        }
    }
    public double FrequencyBalanceA
    {
        get => _balances.FrequencyA;
        set => ChangeBalance(value, b => _balances with { FrequencyA = b }, nameof(FrequencyBalanceA));
    }
    public double FrequencyBalanceB
    {
        get => _balances.FrequencyB;
        set => ChangeBalance(value, b => _balances with { FrequencyB = b }, nameof(FrequencyBalanceB));
    }
    public double WidthBalanceA
    {
        get => _balances.WidthA;
        set => ChangeBalance(value, b => _balances with { WidthA = b }, nameof(WidthBalanceA));
    }
    public double WidthBalanceB
    {
        get => _balances.WidthB;
        set => ChangeBalance(value, b => _balances with { WidthB = b }, nameof(WidthBalanceB));
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
    public bool CanAssignDevice(DeviceDescriptor device) => IsServerMode ? PlaybackCompatibility.CanRun(device, Kind) : PlaybackCompatibility.CanRun(device, Kind, _reactiveSettings) &&
        (!IsNativeProgram || NativeEditor.ValidationError is null && NativeEditor.Settings.Accepts(device));
    public bool AcceptsWaveform(WaveformPreset preset) => TargetDescriptors().All(device => PlaybackCompatibility.Accepts(device, preset));
    private string? HardwareValidationError
    {
        get
        {
            if (IsServerMode) return TargetDescriptors().Any(device => !CanAssignDevice(device)) ? "Error.RemoteLocalDevices" : null;
            if (IsNativeProgram && NativeEditor.ValidationError is { } error) return error;
            if (IsNativeMotor && (OutputLimit != 100 || ChannelA is > 0 and < 100 || ChannelB is > 0 and < 100 || ChannelC is > 0 and < 100 || HardwareEditor.LimitA != 100 || HardwareEditor.LimitB != 100)) return "Error.NativeMotorLimit";
            if (TargetDescriptors().Any(device => !CanAssignDevice(device) || !HardwareEditor.Settings.Accepts(device, Definition.NeedsMotion(_reactiveSettings))))
                return "Error.ModeDeviceMismatch";
            if (IsNativeMotor)
            {
                var native = NativeEditor.Settings;
                var presets = new[] { native.PresetA, native.PresetB, native.PresetC };
                var levels = new[] { ChannelA, ChannelB, ChannelC };
                foreach (var id in _targets)
                    if (_controller.Connections.TryGetValue(id, out var target) && target.Telemetry?.MotorInfo is { } info)
                        for (var channel = 0; channel < 3; channel++)
                            if (target.Device.HasChannel(channel) && levels[channel] > 0 && presets[channel] > info.ModeCount(channel)) return "Error.NativePresetRange";
            }
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
        foreach (var name in new[] { nameof(HasChannelA), nameof(HasChannelC), nameof(HasDgBalances), nameof(HasHardwareOptions), nameof(HasAuxiliaryMotor), nameof(HasAnalogChannelC), nameof(SelectedOutputCLabel) })
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
