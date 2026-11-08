namespace YokoWave.Core.Bluetooth;

internal sealed class CivetDriver(string id, BluetoothDeviceProfile profile, IBluetoothConnection link,
    Action<DeviceStatusUpdate> publish, Action<Exception> fault) : DgSensorDriver(id, profile, link, publish, fault)
{
    protected override byte[] Stream(bool enabled) => CivetProtocol.Stream(enabled);
    protected override DeviceCommandHandlers CreateCommandHandlers() => new DeviceCommandHandlers()
        .Add<QueryDevice>((command, ct) => command.Query is DeviceQuery.All or DeviceQuery.Battery
            ? ReadBattery(ct) : throw new Localization.LocalizedOperationException("Error.DeviceFeatures"))
        .Add<ConfigureSensorStream>((command, ct) => Write(CivetProtocol.Stream(command.Enabled), ct))
        .Add<ConfigurePressureSensor>((command, ct) => Write(CivetProtocol.Configure(command), ct));
    protected override DeviceTelemetry? ParseTelemetry(byte[] data) => CivetProtocol.Parse(data);
    protected override Task ReadBattery(CancellationToken ct) => Battery(BluetoothDeviceProfiles.DgBattery, ct);
    public override Task ResetInput(CancellationToken ct) => Write(CivetProtocol.Configure(new(true)), ct);
}
