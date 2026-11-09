namespace YokoWave.Core.Bluetooth;

internal sealed class PawDriver(string id, BluetoothDeviceProfile profile, IBluetoothConnection link,
    Action<DeviceStatusUpdate> publish, Action<Exception> fault) : DgSensorDriver(id, profile, link, publish, fault)
{
    private readonly PawTelemetryTracker _telemetry = new();
    private ConfigurePaw _configuration = new(new PawLiveData());

    protected override byte[] Stream(bool enabled) => PawProtocol.Configure(new(enabled ? new PawLiveData() : new PawNoTrigger(), IndicatorColor));
    protected override DeviceTelemetry? ParseTelemetry(byte[] data) => _telemetry.Parse(data);
    protected override DeviceCommandHandlers CreateCommandHandlers() => new DeviceCommandHandlers()
        .Add<ConfigurePaw>(async (command, cancellationToken) =>
        {
            await WriteAsync(PawProtocol.Configure(command), cancellationToken).ConfigureAwait(false);
            _configuration = command;
            IndicatorColor = command.IndicatorColor;
            StreamEnabled = command.Trigger is PawLiveData;
        })
        .Add<ConfigureSensorStream>(async (command, cancellationToken) =>
        {
            var configuration = new ConfigurePaw(command.Enabled ? new PawLiveData() : new PawNoTrigger(), command.IndicatorColor);
            await WriteAsync(PawProtocol.Configure(configuration), cancellationToken).ConfigureAwait(false);
            _configuration = configuration;
            IndicatorColor = command.IndicatorColor;
            StreamEnabled = command.Enabled;
        })
        .Add<ConfigureIndicator>(async (command, cancellationToken) =>
        {
            var configuration = _configuration with { IndicatorColor = command.Color };
            await WriteAsync(PawProtocol.Configure(configuration), cancellationToken).ConfigureAwait(false);
            _configuration = configuration;
            IndicatorColor = command.Color;
        })
        .Add<ConfigureShoulderLight>((command, cancellationToken) => WriteAsync(PawProtocol.ShoulderLight(command), cancellationToken))
        .Add<ResetPawParameters>((_, cancellationToken) => WriteAsync([0x5f], cancellationToken))
        .Add<CalibratePawAngles>(async (_, cancellationToken) =>
        {
            await WriteAsync([0x60], cancellationToken).ConfigureAwait(false);
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
