using YokoWave.Core.Bluetooth;
using YokoWave.Core.Localization;

namespace YokoWave.App.ViewModels;

public sealed partial class MainViewModel
{
    public bool HasTelemetry => SelectedConnected && SelectedDevice?.Device.Profile is { Protocol: not BluetoothProtocol.YokonexV1 and not BluetoothProtocol.YokonexV2 };
    public bool CanResetPressure => SelectedConnected && SelectedDevice?.Device.Profile?.Protocol == BluetoothProtocol.Civet;
    public string TelemetryTimestamp => _controller.Connections.TryGetValue(SelectedDevice?.Id ?? "", out var connection) && connection.TelemetryCapturedAt is { } at
        ? L10n.Format("Telemetry.Updated", at.ToLocalTime().ToString("HH:mm:ss")) : L10n.Get("Input.Waiting");
    public IReadOnlyList<TelemetryRow> TelemetryReadings
    {
        get
        {
            if (!_controller.Connections.TryGetValue(SelectedDevice?.Id ?? "", out var connection) || connection.Telemetry is not { } data) return [];
            List<TelemetryRow> rows = [];
            void Add(string key, object? value) { if (value is not null) rows.Add(new(L10n.Get(key), value.ToString()!)); }
            Add("Telemetry.Pressure", data.PressureKpa is { } p ? L10n.Format("Telemetry.Kpa", p) : null);
            Add("Telemetry.RawPressureA", data.PressureA); Add("Telemetry.RawPressureB", data.PressureB);
            Add("Telemetry.Pressed", data.Pressed is { } pressed ? L10n.Get(pressed ? "Telemetry.Down" : "Telemetry.Up") : null);
            Add("Telemetry.Acceleration", data.Acceleration);
            Add("Telemetry.Angles", data.AngleX is { } x ? L10n.Format("Telemetry.XYZ", x, data.AngleY, data.AngleZ) : null);
            Add("Telemetry.Voltage", data.VoltageRaw); Add("Telemetry.Event", data.EventId); Add("Telemetry.Parameter", data.Parameter);
            Add("Telemetry.IntensityA", data.IntensityA); Add("Telemetry.IntensityB", data.IntensityB);
            Add("Telemetry.Buttons", data.Buttons); Add("Telemetry.Model", data.ModelId);
            return rows;
        }
    }
    private AsyncCommand? _resetPressureCommand;
    public AsyncCommand ResetPressureCommand => _resetPressureCommand ??= new(async () =>
    {
        if (CanResetPressure && SelectedDevice is { } device) await _controller.ResetInputAsync(device.Id);
    }, ShowError, () => CanResetPressure);
    private void RefreshTelemetry()
    {
        foreach (var name in new[] { nameof(HasTelemetry), nameof(CanResetPressure), nameof(TelemetryReadings), nameof(TelemetryTimestamp) }) Raise(name);
        _resetPressureCommand?.NotifyCanExecuteChanged();
    }
}
public sealed record TelemetryRow(string Label, string Value);
