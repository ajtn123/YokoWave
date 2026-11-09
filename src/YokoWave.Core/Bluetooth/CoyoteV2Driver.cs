namespace YokoWave.Core.Bluetooth;

internal sealed class CoyoteV2Driver(string id, BluetoothDeviceProfile profile, IBluetoothConnection link,
    Action<DeviceStatusUpdate> publish, Action<Exception> fault) : DgOutputDriver(id, profile, link, publish, fault)
{
    protected override DeviceCommandHandlers CreateCommandHandlers() => new DeviceCommandHandlers().Add<QueryDevice>(async (command, cancellationToken) =>
    {
        if (command.Query is DeviceQuery.All or DeviceQuery.Battery)
            await QueryBatteryAsync(BluetoothDeviceProfiles.V2Battery, cancellationToken).ConfigureAwait(false);
        if (command.Query is DeviceQuery.All or DeviceQuery.ChannelA or DeviceQuery.ChannelB)
            await QueryBatteryAsync(BluetoothDeviceProfiles.V2Strength, cancellationToken).ConfigureAwait(false);
        if (command.Query is DeviceQuery.All or DeviceQuery.ChannelA)
            await QueryBatteryAsync(BluetoothDeviceProfiles.V2WaveA, cancellationToken).ConfigureAwait(false);
        if (command.Query is DeviceQuery.All or DeviceQuery.ChannelB)
            await QueryBatteryAsync(BluetoothDeviceProfiles.V2WaveB, cancellationToken).ConfigureAwait(false);
        if (command.Query is not (DeviceQuery.All or DeviceQuery.Battery or DeviceQuery.ChannelA or DeviceQuery.ChannelB))
            throw new Localization.LocalizedOperationException("Error.DeviceFeatures");
    });
    public override async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await StopAsync(cancellationToken).ConfigureAwait(false);
        await QueryBatteryAsync(BluetoothDeviceProfiles.V2Strength, cancellationToken).ConfigureAwait(false);
        await QueryBatteryAsync(BluetoothDeviceProfiles.V2Battery, cancellationToken).ConfigureAwait(false);
    }
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        var stopped = ExpectStopAsync();
        await Link.WriteAsync(BluetoothDeviceProfiles.V2Strength, [0, 0, 0], cancellationToken).ConfigureAwait(false);
        await WriteWavesAsync(OutputFrame.Zero, cancellationToken).ConfigureAwait(false);
        await QueryBatteryAsync(BluetoothDeviceProfiles.V2Strength, cancellationToken).ConfigureAwait(false);
        await ConfirmAsync(stopped, cancellationToken, "Error.BluetoothStopState").ConfigureAwait(false);
    }
    public override async Task SendAsync(OutputFrame frame, CancellationToken cancellationToken)
    {
        await Link.WriteBatchAsync([new(BluetoothDeviceProfiles.V2Strength, DgLabProtocol.V2Strength(WaveformRendering.Strength(frame, 0), WaveformRendering.Strength(frame, 1), frame.Gentle)), .. WavePackets(frame)], cancellationToken).ConfigureAwait(false);
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
    private Task WriteWavesAsync(OutputFrame frame, CancellationToken cancellationToken) => Link.WriteBatchAsync(WavePackets(frame), cancellationToken);
    private static IReadOnlyList<GattWrite> WavePackets(OutputFrame frame)
    {
        var native = frame.NativeProgram?.Settings;
        byte[] a = native is { Kind: NativeProgramKind.CoyoteV2Pulse } ? DgLabProtocol.V2Pulse(frame.ChannelA > 0 ? native.PulseA ?? new() : new(0, 0, 0)) : DgLabProtocol.V2Pulse(frame, false);
        byte[] b = native is { Kind: NativeProgramKind.CoyoteV2Pulse } ? DgLabProtocol.V2Pulse(frame.ChannelB > 0 ? native.PulseB ?? new() : new(0, 0, 0)) : DgLabProtocol.V2Pulse(frame, true);
        return [new(BluetoothDeviceProfiles.V2WaveA, a), new(BluetoothDeviceProfiles.V2WaveB, b)];
    }
}
