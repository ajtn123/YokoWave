namespace YokoWave.Core.Bluetooth;

internal abstract class DgSensorDriver(string id, BluetoothDeviceProfile profile, IBluetoothConnection link,
    Action<DeviceStatusUpdate> publish, Action<Exception> fault) : BluetoothProtocolDriver(id, profile, link, publish, fault)
{
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    protected virtual Task ReadBattery(CancellationToken ct) => Task.CompletedTask;
    public override async Task Initialize(CancellationToken ct)
    {
        await Write(DgLabProtocol.SensorStream(Profile.Protocol, true), ct); await ReadBattery(ct); await Confirm(_ready.Task, ct);
    }
    public override Task Stop(CancellationToken ct) => Task.CompletedTask;
    public override Task Send(OutputFrame frame, CancellationToken ct) => Task.CompletedTask;
    public override Task Shutdown(CancellationToken ct) => Write(DgLabProtocol.SensorStream(Profile.Protocol, false), ct);
    protected void Ready() => _ready.TrySetResult();
    public override void OnNotification(GattNotification notification)
    {
        var bytes = notification.Data;
        if (notification.Address == BluetoothDeviceProfiles.DgBattery)
        { if (bytes.Length == 1 && bytes[0] <= 100) Publish(new(Id, battery: bytes[0])); return; }
        if (notification.Address != Profile.Notify) return;
        if (DgLabProtocol.ParseSensor(Profile.Protocol, bytes) is { } telemetry)
        { Ready(); Publish(new(Id, telemetry: telemetry)); }
        AdditionalTelemetry(bytes);
    }
    protected virtual void AdditionalTelemetry(byte[] bytes) { }
}

internal sealed class PawDriver(string id, BluetoothDeviceProfile profile, IBluetoothConnection link,
    Action<DeviceStatusUpdate> publish, Action<Exception> fault) : DgSensorDriver(id, profile, link, publish, fault)
{
    protected override void AdditionalTelemetry(byte[] bytes)
    {
        if (bytes.Length == 4 && bytes[0] == 0x51 && bytes[2] == 3 && bytes[3] <= 100)
        { Ready(); Publish(new(Id, battery: bytes[3])); }
    }
}

internal sealed class CivetDriver(string id, BluetoothDeviceProfile profile, IBluetoothConnection link,
    Action<DeviceStatusUpdate> publish, Action<Exception> fault) : DgSensorDriver(id, profile, link, publish, fault)
{
    protected override Task ReadBattery(CancellationToken ct) => Battery(BluetoothDeviceProfiles.DgBattery, ct);
    public override Task ResetInput(CancellationToken ct) => Write(DgLabProtocol.ResetPressure(), ct);
}
