namespace YokoWave.Core.Bluetooth;

internal sealed class PawDriver(string id, BluetoothDeviceProfile profile, IBluetoothConnection link,
    Action<DeviceStatusUpdate> publish, Action<Exception> fault) : DgSensorDriver(id, profile, link, publish, fault)
{
    protected override byte[] Stream(bool enabled) => PawProtocol.Configure(new(enabled ? new PawLiveData() : new PawNoTrigger(), IndicatorColor));
    private readonly PawTelemetryTracker _telemetry = new();
    private ConfigurePaw _configuration = new(new PawLiveData());
    protected override DeviceTelemetry? ParseTelemetry(byte[] data) => _telemetry.Parse(data);
    protected override DeviceCommandHandlers CreateCommandHandlers() => new DeviceCommandHandlers()
        .Add<ConfigurePaw>(async (command, ct) =>
        {
            await Write(PawProtocol.Configure(command), ct);
            _configuration = command;
            IndicatorColor = command.IndicatorColor;
            StreamEnabled = command.Trigger is PawLiveData;
        })
        .Add<ConfigureSensorStream>(async (command, ct) =>
        {
            var configuration = new ConfigurePaw(command.Enabled ? new PawLiveData() : new PawNoTrigger(), command.IndicatorColor);
            await Write(PawProtocol.Configure(configuration), ct);
            _configuration = configuration;
            IndicatorColor = command.IndicatorColor;
            StreamEnabled = command.Enabled;
        })
        .Add<ConfigureIndicator>(async (command, ct) =>
        {
            var configuration = _configuration with { IndicatorColor = command.Color };
            await Write(PawProtocol.Configure(configuration), ct);
            _configuration = configuration;
            IndicatorColor = command.Color;
        })
        .Add<ConfigureShoulderLight>((command, ct) => Write(PawProtocol.ShoulderLight(command), ct))
        .Add<ResetPawParameters>((_, ct) => Write([0x5f], ct))
        .Add<CalibratePawAngles>(async (_, ct) =>
        {
            await Write([0x60], ct);
            StreamEnabled = false;
            _configuration = new(new PawNoTrigger(), IndicatorColor);
        });
    protected override void AdditionalTelemetry(byte[] bytes)
    {
        if (bytes.Length == 4 && bytes[0] == 0x51 && bytes[2] == 3 && bytes[3] <= 100)
        {
            Ready();
            Publish(new(Id, battery: bytes[3], telemetry: new(IndicatorColor: bytes[1])));
        }
    }
}
