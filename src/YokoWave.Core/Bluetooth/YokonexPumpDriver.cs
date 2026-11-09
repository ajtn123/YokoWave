using System.Buffers.Binary;

namespace YokoWave.Core.Bluetooth;

internal sealed class YokonexPumpDriver(string id, BluetoothDeviceProfile profile, IBluetoothConnection link,
    Action<DeviceStatusUpdate> publish, Action<Exception> fault) : BluetoothProtocolDriver(id, profile, link, publish, fault)
{
    private TaskCompletionSource? _stopped;
    protected override DeviceCommandHandlers CreateCommandHandlers() => new DeviceCommandHandlers()
        .Add<QueryDevice>(async (command, cancellationToken) =>
        {
            if (command.Query is DeviceQuery.All)
            {
                await WriteAsync(YokonexToyProtocol.PumpCommand(4), cancellationToken).ConfigureAwait(false);
                await WriteAsync(YokonexToyProtocol.PumpCommand(5), cancellationToken).ConfigureAwait(false);
            }
            else if (command.Query == DeviceQuery.Battery)
            {
                await WriteAsync(YokonexToyProtocol.PumpCommand(5), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                throw new Localization.LocalizedOperationException("Error.DeviceFeatures");
            }
        });
    public override async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await StopAsync(cancellationToken).ConfigureAwait(false);
        await WriteAsync(YokonexToyProtocol.PumpCommand(4), cancellationToken).ConfigureAwait(false);
        await WriteAsync(YokonexToyProtocol.PumpCommand(5), cancellationToken).ConfigureAwait(false);
    }
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _stopped = stopped;
        await WriteAsync(YokonexToyProtocol.PumpCommand(3), cancellationToken).ConfigureAwait(false);
        await WriteAsync(YokonexToyProtocol.PumpCommand(4), cancellationToken).ConfigureAwait(false);
        await ConfirmAsync(stopped.Task, cancellationToken, "Error.BluetoothStopState").ConfigureAwait(false);
    }
    public override async Task SendAsync(OutputFrame frame, CancellationToken cancellationToken)
    {
        if (frame.Pump is { } pump && (pump.Direction != PumpDirection.Off || pump.Water))
        {
            pump.Validate();
            await WriteAsync(YokonexToyProtocol.PumpCommand(1, pump.Direction, seconds: (ushort)Math.Max(1, pump.PeristalticSeconds)), cancellationToken).ConfigureAwait(false);
            await WriteAsync(YokonexToyProtocol.PumpCommand(2, water: pump.Water, seconds: (ushort)Math.Max(1, pump.WaterSeconds)), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await StopAsync(cancellationToken).ConfigureAwait(false);
        }
    }
    public override void OnNotification(GattNotification notification)
    {
        if (notification.Address != Profile.Notify || YokonexToyProtocol.DecryptPump(notification.Data) is not { } plain)
            return;
        switch (plain[3])
        {
            case 1 when plain[4] <= 2 && plain[5] <= 1:
                if (plain[4] == 0 && plain[5] == 0)
                    _stopped?.TrySetResult();
                Publish(new(Id, outputAEnabled: plain[4] != 0, outputBEnabled: plain[5] != 0,
                    telemetry: new(Peristaltic: (PumpDirection)plain[4], WaterPump: plain[5] != 0)));
                break;
            case 2:
                Publish(new(Id, telemetry: new(PressureA: BinaryPrimitives.ReadUInt16BigEndian(plain.AsSpan(4, 2)), PressureB: BinaryPrimitives.ReadUInt16BigEndian(plain.AsSpan(6, 2)))));
                break;
            case 3 when plain[4] <= 100:
                Publish(new(Id, battery: plain[4]));
                break;
        }
    }
}
