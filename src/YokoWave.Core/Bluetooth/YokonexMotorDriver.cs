namespace YokoWave.Core.Bluetooth;

internal sealed class YokonexMotorDriver(string id, BluetoothDeviceProfile profile, IBluetoothConnection link,
    Action<DeviceStatusUpdate> publish, Action<Exception> fault) : BluetoothProtocolDriver(id, profile, link, publish, fault)
{
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _motors;
    private MotorDeviceInfo? _info;
    private ProgramPacketWriter? _programWriter;
    protected override DeviceCommandHandlers CreateCommandHandlers() => new DeviceCommandHandlers()
        .Add<QueryDevice>((command, cancellationToken) => command.Query is DeviceQuery.All or DeviceQuery.Product
            ? WriteAsync(YokonexToyProtocol.MotorInfo(), cancellationToken) : throw new Localization.LocalizedOperationException("Error.DeviceFeatures"));
    public override async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await StopAsync(cancellationToken).ConfigureAwait(false);
        await WriteAsync(YokonexToyProtocol.MotorInfo(), cancellationToken).ConfigureAwait(false);
        await ConfirmAsync(_ready.Task, cancellationToken).ConfigureAwait(false);
    }
    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _programWriter?.Reset();
        return WriteAsync(YokonexToyProtocol.MotorStop(), cancellationToken);
    }
    public override async Task SendAsync(OutputFrame frame, CancellationToken cancellationToken)
    {
        if (frame.NativeProgram?.Settings is { Kind: NativeProgramKind.MotorPreset } native)
        {
            int[] waveforms = new[] { native.PresetA, native.PresetB, native.PresetC };
            double[] levels = new[] { frame.ChannelA, frame.ChannelB, frame.ChannelC };
            for (int channel = 0; channel < 3; channel++)
            {
                if ((_motors & 1 << channel) != 0 && levels[channel] > 0 && waveforms[channel] > (_info?.ModeCount(channel) ?? 0))
                    throw new Localization.LocalizedArgumentException("Error.NativePresetRange");
            }

            byte[][] packets = Enumerable.Range(0, 3).Where(channel => (_motors & 1 << channel) != 0)
                .GroupBy(channel => levels[channel] > 0 ? waveforms[channel] : 0)
                .Select(group => YokonexToyProtocol.MotorPreset(group.Aggregate(0, (mask, channel) => mask | 1 << channel), group.Key)).ToArray();
            await (_programWriter ??= new(WriteAsync)).SendAsync(packets, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            _programWriter?.Reset();
            await WriteAsync(YokonexToyProtocol.MotorOutput(frame, _motors), cancellationToken).ConfigureAwait(false);
        }
    }
    public override void OnNotification(GattNotification notification)
    {
        if (notification.Address != Profile.Notify)
            return;
        byte[] data = notification.Data;
        if (data.Length < 3 || data[0] != 0x35 || V1Protocol.WithChecksum(data.AsSpan(0, data.Length - 1))[^1] != data[^1])
            return;
        if (data.Length == 10 && data[1] == 0x10 && data[2] > 0 && data[3] > 0)
        {
            int mask = (data[4] > 0 ? 1 : 0) | (data[5] > 0 ? 2 : 0) | (data[6] > 0 ? 4 : 0);
            if (mask == 0)
                return;
            _motors = mask;
            _info = new(data[2], data[3], data[4], data[5], data[6]);
            _ready.TrySetResult();
            Publish(new(Id, channelMask: mask,
                            telemetry: new(ModelId: data[2], MotorInfo: _info)));
        }
        else if (data.Length == 5 && data[1] == 0x13 && data[2] == 1 && data[3] <= 100)
        {
            Publish(new(Id, battery: data[3]));
        }
    }
}
