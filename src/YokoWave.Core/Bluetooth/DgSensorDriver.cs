namespace YokoWave.Core.Bluetooth;

internal abstract class DgSensorDriver(string id, BluetoothDeviceProfile profile, IBluetoothConnection link,
    Action<DeviceStatusUpdate> publish, Action<Exception> fault) : BluetoothProtocolDriver(id, profile, link, publish, fault)
{
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    protected bool StreamEnabled
    { get; set; } = true;
    protected int IndicatorColor
    { get; set; } = 1;
    protected abstract DeviceTelemetry? ParseTelemetry(byte[] data);
    protected abstract byte[] Stream(bool enabled);
    protected virtual Task ReadBattery(CancellationToken ct) => Task.CompletedTask;
    public override async Task Initialize(CancellationToken ct)
    {
        await Write(Stream(true), ct);
        await ReadBattery(ct);
        await Confirm(_ready.Task, ct);
    }
    public override Task Stop(CancellationToken ct) => Task.CompletedTask;
    public override Task Send(OutputFrame frame, CancellationToken ct) => Task.CompletedTask;
    public override Task Shutdown(CancellationToken ct) => Write(Stream(false), ct);
    protected void Ready() => _ready.TrySetResult();
    public override void OnNotification(GattNotification notification)
    {
        var bytes = notification.Data;
        if (notification.Address == BluetoothDeviceProfiles.DgBattery)
        {
            if (bytes.Length == 1 && bytes[0] <= 100) Publish(new(Id, battery: bytes[0]));
            return;
        }
        if (notification.Address != Profile.Notify) return;
        if (ParseTelemetry(bytes) is { } telemetry)
        {
            Ready();
            Publish(new(Id, telemetry: telemetry));
        }
        AdditionalTelemetry(bytes);
    }
    protected virtual void AdditionalTelemetry(byte[] bytes) { }
}
