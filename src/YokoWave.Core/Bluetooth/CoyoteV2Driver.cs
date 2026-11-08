namespace YokoWave.Core.Bluetooth;

internal sealed class CoyoteV2Driver(string id, BluetoothDeviceProfile profile, IBluetoothConnection link,
    Action<DeviceStatusUpdate> publish, Action<Exception> fault) : DgOutputDriver(id, profile, link, publish, fault)
{
    public override async Task Initialize(CancellationToken ct)
    {
        await Stop(ct); await Battery(BluetoothDeviceProfiles.V2Strength, ct); await Battery(BluetoothDeviceProfiles.V2Battery, ct);
    }
    public override async Task Stop(CancellationToken ct)
    {
        var stopped = ExpectStop();
        await Link.WriteAsync(BluetoothDeviceProfiles.V2Strength, [0, 0, 0], ct);
        await WriteWaves(OutputFrame.Zero, ct);
        await Battery(BluetoothDeviceProfiles.V2Strength, ct);
        await Confirm(stopped, ct, "Error.BluetoothStopState");
    }
    public override async Task Send(OutputFrame frame, CancellationToken ct)
    {
        await Link.WriteAsync(BluetoothDeviceProfiles.V2Strength, DgLabProtocol.V2Strength(WaveformRendering.Strength(frame, 0), WaveformRendering.Strength(frame, 1), frame.Gentle), ct);
        await WriteWaves(frame, ct);
    }
    private async Task WriteWaves(OutputFrame frame, CancellationToken ct)
    {
        await Link.WriteAsync(BluetoothDeviceProfiles.V2WaveA, DgLabProtocol.V2Pulse(frame, false), ct);
        await Link.WriteAsync(BluetoothDeviceProfiles.V2WaveB, DgLabProtocol.V2Pulse(frame, true), ct);
    }
    protected override (int A, int B)? ParseLevels(byte[] bytes) => DgLabProtocol.ParseV2Strength(bytes);
}
