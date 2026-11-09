using YokoWave.Core;
using YokoWave.Core.Bluetooth;
using YokoWave.Core.Localization;

namespace YokoWave.App.ViewModels;

public sealed class DeviceTelemetryViewModel : ObservableObject
{
    private readonly DeviceController _controller;
    private string? _deviceId;
    private DeviceConnection? Connection => _deviceId is { } id && _controller.Connections.TryGetValue(id, out var connection) ? connection : null;
    public bool IsVisible => Connection?.Device.Profile is not null;
    public bool CanResetPressure => Connection?.Device.Profile?.Supports(DeviceCapability.PressureReset) == true && !_controller.IsDeviceBusy(_deviceId!);
    public string Timestamp => Connection?.TelemetryCapturedAt is { } at
        ? L10n.Format("Telemetry.Updated", at.ToLocalTime().ToString("HH:mm:ss")) : L10n.Get("Input.Waiting");
    public AsyncCommand ResetPressureCommand { get; }
    public IReadOnlyList<TelemetryRow> Readings
    {
        get
        {
            if (Connection is not { Telemetry: { } data } connection)
                return [];
            List<TelemetryRow> rows = [];
            void Add(string key, object? value)
            {
                if (value is not null)
                    rows.Add(new(L10n.Get(key), value.ToString()!));
            }
            void Axes(string key, SensorAxes? axes)
            {
                if (axes is { } value)
                    Add(key, L10n.Format("Telemetry.XYZ", value.X, value.Y, value.Z));
            }
            Add("Telemetry.Pressure", data.PressureKpa is { } pressure ? L10n.Format("Telemetry.Kpa", pressure) : null);
            Add("Telemetry.RawPressureA", data.PressureA);
            Add("Telemetry.RawPressureB", data.PressureB);
            Add("Telemetry.Pressed", data.Pressed is { } pressed ? L10n.Get(pressed ? "Telemetry.Down" : "Telemetry.Up") : null);
            Add("Telemetry.Acceleration", data.Acceleration);
            Axes("Telemetry.AccelerationAxes", data.AccelerationAxes);
            Axes("Telemetry.GyroscopeAxes", data.GyroscopeAxes);
            Add("Telemetry.Angles", data.AngleX is { } x ? L10n.Format("Telemetry.XYZ", x, data.AngleY, data.AngleZ) : null);
            Add("Telemetry.Voltage", data.VoltageRaw);
            Add("Telemetry.VoltageVolts", data.VoltageVolts is { } volts ? L10n.Format("Telemetry.Volts", volts) : null);
            Add("Telemetry.IntensityA", data.IntensityA);
            Add("Telemetry.IntensityB", data.IntensityB);
            Add("Telemetry.ModeA", data.ModeA);
            Add("Telemetry.ModeB", data.ModeB);
            Add("Telemetry.AuxiliaryMotor", data.AuxiliaryMotor is { } motor ? L10n.Get($"AuxiliaryMotor.{motor}") : null);
            Add("Telemetry.Steps", connection.Steps);
            Add("Telemetry.Buttons", data.Buttons);
            if (data.Buttons is { } bits)
            {
                var buttons = OpossumProtocol.PressedButtons(bits);
                Add("Telemetry.ButtonNames", buttons.Count == 0 ? L10n.Get("Telemetry.NoButtons")
                    : string.Join(", ", buttons.Select(button => L10n.Get($"OpossumButton.{button}"))));
            }
            Add("Telemetry.Model", data.ModelId);
            if (data.MotorInfo is { } info)
            {
                Add("Telemetry.Revision", info.Revision);
                Add("Telemetry.ProgramCounts", L10n.Format("Telemetry.XYZ", info.ModesA, info.ModesB, info.ModesC));
            }
            if (data.PawEvents is { } events)
            {
                foreach (var state in events)
                {
                    rows.Add(new(L10n.Format("Telemetry.PawEvent", state.EventId), L10n.Format("Telemetry.EventState",
                        L10n.Get(state.Active switch { true => "Telemetry.Triggered", false => "Telemetry.Cancelled", _ => "Input.Waiting" }), state.Parameter?.ToString() ?? "—")));
                }
            }
            Add("Telemetry.Peristaltic", data.Peristaltic is { } direction ? L10n.Get($"PumpDirection.{direction}") : null);
            Add("Pump.Water", data.WaterPump is { } water ? L10n.Get(water ? "State.On" : "State.Off") : null);
            Add("Telemetry.PulseA", FormatPulse(data.PulseA));
            Add("Telemetry.PulseB", FormatPulse(data.PulseB));
            Add("Telemetry.ProtocolError", data.ProtocolErrorCode is { } code ? L10n.Format("Telemetry.ProtocolErrorValue", code,
                L10n.Get($"ProtocolError.{(code is >= 1 and <= 5 ? code.ToString() : "Unknown")}")) : null);
            return rows;
        }
    }
    public DeviceTelemetryViewModel(DeviceController controller, Action<Exception> report)
    {
        _controller = controller;
        ResetPressureCommand = new(async () =>
        {
            if (CanResetPressure && _deviceId is { } id)
                await _controller.ExecuteCommandAsync(id, new ConfigurePressureSensor(true));
        }, report, () => CanResetPressure);
    }
    public void SelectDevice(DeviceDescriptor? device)
    {
        _deviceId = device?.Id;
        foreach (string? name in new[] { nameof(IsVisible), nameof(CanResetPressure), nameof(Readings), nameof(Timestamp) })
            Raise(name);
        ResetPressureCommand.NotifyCanExecuteChanged();
    }
    private static string? FormatPulse(CoyotePulse? pulse) => pulse is null ? null : L10n.Format("Telemetry.NativePulse", pulse.X, pulse.Y, pulse.Z);
}

public sealed record TelemetryRow(string Label, string Value);
