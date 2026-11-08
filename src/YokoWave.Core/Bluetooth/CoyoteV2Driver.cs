namespace YokoWave.Core.Bluetooth;

internal sealed class CoyoteV2Driver(string id, BluetoothDeviceProfile profile, IBluetoothConnection link,
    Action<DeviceStatusUpdate> publish, Action<Exception> fault) : DgOutputDriver(id, profile, link, publish, fault)
{
    protected override DeviceCommandHandlers CreateCommandHandlers() => new DeviceCommandHandlers().Add<QueryDevice>(async (command, ct) =>
    {
        if (command.Query is DeviceQuery.All or DeviceQuery.Battery) await Battery(BluetoothDeviceProfiles.V2Battery, ct);
        if (command.Query is DeviceQuery.All or DeviceQuery.ChannelA or DeviceQuery.ChannelB) await Battery(BluetoothDeviceProfiles.V2Strength, ct);
        if (command.Query is DeviceQuery.All or DeviceQuery.ChannelA) await Battery(BluetoothDeviceProfiles.V2WaveA, ct);
        if (command.Query is DeviceQuery.All or DeviceQuery.ChannelB) await Battery(BluetoothDeviceProfiles.V2WaveB, ct);
        if (command.Query is not (DeviceQuery.All or DeviceQuery.Battery or DeviceQuery.ChannelA or DeviceQuery.ChannelB))
            throw new Localization.LocalizedOperationException("Error.DeviceFeatures");
    });
    public override async Task Initialize(CancellationToken ct)
    {
        await Stop(ct);
        await Battery(BluetoothDeviceProfiles.V2Strength, ct);
        await Battery(BluetoothDeviceProfiles.V2Battery, ct);
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
        var native = frame.NativeProgram?.Settings;
        var a = native is { Kind: NativeProgramKind.CoyoteV2Pulse } ? DgLabProtocol.V2Pulse(frame.ChannelA > 0 ? native.PulseA ?? new() : new(0, 0, 0)) : DgLabProtocol.V2Pulse(frame, false);
        var b = native is { Kind: NativeProgramKind.CoyoteV2Pulse } ? DgLabProtocol.V2Pulse(frame.ChannelB > 0 ? native.PulseB ?? new() : new(0, 0, 0)) : DgLabProtocol.V2Pulse(frame, true);
        await Link.WriteAsync(BluetoothDeviceProfiles.V2WaveA, a, ct);
        await Link.WriteAsync(BluetoothDeviceProfiles.V2WaveB, b, ct);
    }
    protected override (int A, int B)? ParseLevels(byte[] bytes) => DgLabProtocol.ParseV2Strength(bytes);
    public override void OnNotification(GattNotification notification)
    {
        if (notification.Address == BluetoothDeviceProfiles.V2WaveA || notification.Address == BluetoothDeviceProfiles.V2WaveB)
        {
            if (DgLabProtocol.ParseV2Pulse(notification.Data) is { } pulse)
                Publish(new(Id, telemetry: notification.Address == BluetoothDeviceProfiles.V2WaveA ? new(PulseA: pulse) : new(PulseB: pulse)));
            return;
        }
        base.OnNotification(notification);
    }
}
