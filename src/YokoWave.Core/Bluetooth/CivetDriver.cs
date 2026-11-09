namespace YokoWave.Core.Bluetooth;

internal sealed class CivetDriver(string id, BluetoothDeviceProfile profile, IBluetoothConnection link,
    Action<DeviceStatusUpdate> publish, Action<Exception> fault) : DgSensorDriver(id, profile, link, publish, fault)
{
    protected override byte[] Stream(bool enabled) => CivetProtocol.Stream(enabled);
    protected override DeviceCommandHandlers CreateCommandHandlers() => new DeviceCommandHandlers()
        .Add<QueryDevice>((command, cancellationToken) => command.Query is DeviceQuery.All or DeviceQuery.Battery
            ? ReadBatteryAsync(cancellationToken) : throw new Localization.LocalizedOperationException("Error.DeviceFeatures"))
        .Add<ConfigureSensorStream>((command, cancellationToken) => WriteAsync(CivetProtocol.Stream(command.Enabled), cancellationToken))
        .Add<ConfigurePressureSensor>((command, cancellationToken) => WriteAsync(CivetProtocol.Configure(command), cancellationToken));
    protected override DeviceTelemetry? ParseTelemetry(byte[] data) => CivetProtocol.Parse(data);
    protected override Task ReadBatteryAsync(CancellationToken cancellationToken) => QueryBatteryAsync(BluetoothDeviceProfiles.DgBattery, cancellationToken);
    public override Task ResetInputAsync(CancellationToken cancellationToken) => WriteAsync(CivetProtocol.Configure(new(true)), cancellationToken);
}
