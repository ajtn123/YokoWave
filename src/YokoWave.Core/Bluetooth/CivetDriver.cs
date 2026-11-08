namespace YokoWave.Core.Bluetooth;

internal sealed class CivetDriver(string id, BluetoothDeviceProfile profile, IBluetoothConnection link,
    Action<DeviceStatusUpdate> publish, Action<Exception> fault) : DgSensorDriver(id, profile, link, publish, fault)
{
    protected override byte[] Stream(bool enabled) => CivetProtocol.Stream(enabled, IndicatorColor);
    protected override DeviceCommandHandlers CreateCommandHandlers() => new DeviceCommandHandlers()
        .Add<QueryDevice>((command, ct) => command.Query is DeviceQuery.All or DeviceQuery.Battery
            ? ReadBattery(ct) : throw new Localization.LocalizedOperationException("Error.DeviceFeatures"))
        .Add<ConfigureSensorStream>(async (command, ct) =>
        {
            await Write(CivetProtocol.Stream(command.Enabled, command.IndicatorColor), ct);
            StreamEnabled = command.Enabled;
            IndicatorColor = command.IndicatorColor;
        })
        .Add<ConfigureIndicator>(async (command, ct) =>
        {
            await Write(CivetProtocol.Stream(StreamEnabled, command.Color), ct);
            IndicatorColor = command.Color;
        })
        .Add<ConfigurePressureSensor>((command, ct) => Write(CivetProtocol.Configure(command), ct));
    protected override DeviceTelemetry? ParseTelemetry(byte[] data) => CivetProtocol.Parse(data);
    protected override Task ReadBattery(CancellationToken ct) => Battery(BluetoothDeviceProfiles.DgBattery, ct);
    public override Task ResetInput(CancellationToken ct) => Write(CivetProtocol.Configure(new(true)), ct);
}
