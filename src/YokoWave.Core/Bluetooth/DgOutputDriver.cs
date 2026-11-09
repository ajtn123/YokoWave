namespace YokoWave.Core.Bluetooth;

/// <summary>Shared intensity telemetry and zero-output acknowledgement for DG-LAB output protocols.</summary>
internal abstract class DgOutputDriver(string id, BluetoothDeviceProfile profile, IBluetoothConnection link,
    Action<DeviceStatusUpdate> publish, Action<Exception> fault) : BluetoothProtocolDriver(id, profile, link, publish, fault)
{
    private TaskCompletionSource? _stopped;
    protected Task ExpectStopAsync()
    {
        _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        return _stopped.Task;
    }
    protected abstract (int A, int B)? ParseLevels(byte[] bytes);
    protected virtual bool MatchesStop(byte[] bytes) => true;
    protected virtual void AdditionalTelemetry(byte[] bytes) { }
    public override void OnNotification(GattNotification notification)
    {
        byte[] bytes = notification.Data;
        if (notification.Address == BluetoothDeviceProfiles.DgBattery || notification.Address == BluetoothDeviceProfiles.V2Battery)
        {
            if (bytes.Length == 1 && bytes[0] <= 100)
                Publish(new(Id, battery: bytes[0]));
            return;
        }
        if (notification.Address != Profile.Notify)
            return;
        if (ParseLevels(bytes) is { } actual)
        {
            if (actual.A == 0 && actual.B == 0 && MatchesStop(bytes))
                _stopped?.TrySetResult();
            Publish(new(Id, outputAEnabled: actual.A > 0, outputBEnabled: actual.B > 0,
                telemetry: new(IntensityA: actual.A, IntensityB: actual.B)));
        }
        AdditionalTelemetry(bytes);
    }
}
