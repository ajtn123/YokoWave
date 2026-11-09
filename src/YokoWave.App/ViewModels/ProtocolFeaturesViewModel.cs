using YokoWave.Core;
using YokoWave.Core.Bluetooth;
using YokoWave.Core.Localization;

namespace YokoWave.App.ViewModels;

/// <summary>Transient command drafts; device configuration is never persisted here.</summary>
public sealed class ProtocolFeaturesViewModel : ObservableObject
{
    private readonly DeviceController _controller;
    private string? _deviceId;
    private BluetoothDeviceProfile? _profile;
    private ProtocolCommandOption? _selected;
    private string _json = "{}";
    public IReadOnlyList<ProtocolCommandOption> Commands { get; private set; } = [];
    public bool IsVisible => Commands.Count > 0;
    public bool CanConfigure => _deviceId is { } id && _controller.Connections.ContainsKey(id) && !_controller.IsDeviceBusy(id);
    public ProtocolCommandOption? SelectedCommand
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value))
                return;
            Json = value?.Template ?? "{}";
            Raise(nameof(Help));
        }
    }
    public IReadOnlyList<ParameterFieldViewModel> Parameters { get; private set; } = [];
    public string Json
    {
        get => _json;
        set
        {
            _json = value;
            Raise(nameof(Json));
            try
            {
                Parameters = ProtocolParameterForm.Create(value, _selected?.Key ?? "", _profile, updated =>
                {
                    _json = updated;
                    Raise(nameof(Json));
                });
            }
            catch (System.Text.Json.JsonException)
            {
                Parameters = [];
            }
            Raise(nameof(Parameters));
        }
    }
    public string Help => _selected is { } selected ? L10n.Get($"ProtocolHelp.{selected.Key}") : "";
    public AsyncCommand ApplyCommand { get; }
    public ActionCommand ResetDraftCommand { get; }
    public ActionCommand UseCalibrationCommand { get; }
    public bool HasCalibration => _deviceId is { } id && _controller.Connections.TryGetValue(id, out var connection) && connection.Telemetry?.Calibration is not null;
    public string Readings => _deviceId is { } id && _controller.Connections.TryGetValue(id, out var connection)
        ? SettingsJson.Write(new
        {
            connection.Steps,
            connection.Telemetry
        }) : "";
    public ProtocolFeaturesViewModel(DeviceController controller, Action<Exception> report)
    {
        _controller = controller;
        ApplyCommand = new(ExecuteAsync, report, () => CanConfigure && SelectedCommand is not null);
        ResetDraftCommand = new(() => Json = _selected?.Template ?? "{}");
        UseCalibrationCommand = new(UseCalibration);
    }
    public async Task ExecuteAsync()
    {
        if (!CanConfigure || _deviceId is not { } id || _selected is not { } selected)
            return;
        var command = selected.Parse(Json);
        command.Validate();
        await _controller.ExecuteCommandAsync(id, command);
        Refresh();
    }
    private void UseCalibration()
    {
        if (_deviceId is not { } id || !_controller.Connections.TryGetValue(id, out var connection) || connection.Telemetry?.Calibration is not { } calibration)
            return;
        SelectedCommand = Commands.FirstOrDefault(command => command.Key == "PawButtonMotion");
        if (_selected is null)
            return;
        Json = SettingsJson.Write(new PawTriggerDraft<PawButtonMotion>(new(Dynamics: new(), Motion: new(EventId: 2, UseAngles: true, X: calibration.X, Y: calibration.Y, Z: calibration.Z))));
    }
    public void SelectDevice(DeviceDescriptor? device)
    {
        if (_deviceId != device?.Id)
        {
            _deviceId = device?.Id;
            _profile = device?.Profile;
            Commands = AllCommands.Where(command => device?.Profile?.Supports(command.Capability) == true).ToArray();
            Raise(nameof(Commands));
            var command = Commands.FirstOrDefault();
            if (_selected == command)
            {
                Json = command?.Template ?? "{}";
                Raise(nameof(SelectedCommand));
                Raise(nameof(Help));
            }
            else
                SelectedCommand = command;
        }
        Refresh();
    }
    public void RefreshLocalization()
    {
        foreach (var command in Commands)
            command.Refresh();
        foreach (var field in Parameters)
            field.Refresh();
        Raise(nameof(Help));
    }
    public void Refresh()
    {
        foreach (var name in new[] { nameof(IsVisible), nameof(CanConfigure), nameof(Help), nameof(HasCalibration), nameof(Readings) })
            Raise(name);
        ApplyCommand.NotifyCanExecuteChanged();
    }
    internal static readonly ProtocolCommandOption[] AllCommands =
    [
        ProtocolCommandOption.For("Query", new QueryDevice()),
        ProtocolCommandOption.For("Steps", new ControlStepCounter(StepCounterAction.Start)),
        ProtocolCommandOption.For("Motion", new ConfigureMotionStream(true)),
        ProtocolCommandOption.For("Stream", new ConfigureSensorStream(true)),
        ProtocolCommandOption.For("Indicator", new ConfigureIndicator(1)),
        ProtocolCommandOption.For("Shoulder", new ConfigureShoulderLight()),
        ProtocolCommandOption.For("Calibrate", new CalibratePawAngles()),
        ProtocolCommandOption.For("ResetPaw", new ResetPawParameters()),
        ProtocolCommandOption.For("Pressure", new ConfigurePressureSensor(true)),
        ProtocolCommandOption.For("Display", new SynchronizeDisplay()),
        ProtocolCommandOption.Paw("PawNone", new PawNoTrigger()),
        ProtocolCommandOption.Paw("PawLive", new PawLiveData()),
        ProtocolCommandOption.Paw("PawRandom", new PawRandomReaction(Dynamics: new())),
        ProtocolCommandOption.Paw("PawProbability", new PawProbability([new(1, 100), new(2, 100), new(3, 0), new(4, 0), new(5, 0), new(6, 0)])),
        ProtocolCommandOption.Paw("PawVoltage", new PawVoltage()),
        ProtocolCommandOption.Paw("PawButtonMotion", new PawButtonMotion(Dynamics: new(), Motion: new(EventId: 2, X: new(), Y: new(), Z: new())))
    ];
}

public sealed record PawTriggerDraft<T>(T Trigger, int IndicatorColor = 1) where T : PawTrigger;

public sealed class ProtocolCommandOption : ObservableObject
{
    public string Key { get; }
    public string Label => L10n.Get($"ProtocolCommand.{Key}");
    public DeviceCapability Capability { get; }
    public string Template { get; }
    private readonly Func<string, DeviceCommand> _parse;
    private ProtocolCommandOption(string key, DeviceCapability capability, string template, Func<string, DeviceCommand> parse)
    {
        Key = key;
        Capability = capability;
        Template = template;
        _parse = parse;
    }
    public DeviceCommand Parse(string json) => _parse(json);
    internal static ProtocolCommandOption For<T>(string key, T command) where T : DeviceCommand =>
        new(key, command.Capability, SettingsJson.Write(command), json => SettingsJson.Read<T>(json));
    internal static ProtocolCommandOption Paw<T>(string key, T trigger) where T : PawTrigger =>
        new(key, DeviceCapability.PawTriggers, SettingsJson.Write(new PawTriggerDraft<T>(trigger)), json =>
        {
            var draft = SettingsJson.Read<PawTriggerDraft<T>>(json);
            return new ConfigurePaw(draft.Trigger, draft.IndicatorColor);
        });
    public void Refresh() => Raise(nameof(Label));
}
