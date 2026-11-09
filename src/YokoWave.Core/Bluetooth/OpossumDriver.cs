using System.Buffers.Binary;

namespace YokoWave.Core.Bluetooth;

internal sealed class OpossumDriver(string id, BluetoothDeviceProfile profile, IBluetoothConnection link,
    Action<DeviceStatusUpdate> publish, Action<Exception> fault) : DgOutputDriver(id, profile, link, publish, fault)
{
    private int _levels;
    private int _hasLevels;
    protected override DeviceCommandHandlers CreateCommandHandlers() => new DeviceCommandHandlers()
        .Add<QueryDevice>((command, cancellationToken) => command.Query is DeviceQuery.All or DeviceQuery.Battery
            ? QueryBatteryAsync(BluetoothDeviceProfiles.DgBattery, cancellationToken) : throw new Localization.LocalizedOperationException("Error.DeviceFeatures"))
        .Add<ConfigureSensorStream>((command, cancellationToken) => WriteAsync(OpossumProtocol.Stream(command.Enabled), cancellationToken))
        .Add<SynchronizeDisplay>((_, cancellationToken) =>
        {
            int levels = Volatile.Read(ref _levels);
            byte[] packet = OpossumProtocol.Display(levels >> 8, levels & 255);
            if (packet.Length > Link.MaximumWriteLength)
                throw new Localization.LocalizedOperationException("Error.DisplayPacketSize");
            return WriteAsync(packet, cancellationToken);
        });
    public override async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await StopAsync(cancellationToken).ConfigureAwait(false);
        await WriteAsync([0x50, 1, 1], cancellationToken).ConfigureAwait(false);
        await QueryBatteryAsync(BluetoothDeviceProfiles.DgBattery, cancellationToken).ConfigureAwait(false);
    }
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await WriteAsync([0xb3, 0, 0], cancellationToken).ConfigureAwait(false);
        await WriteAsync(DgLabProtocol.OpossumWave(OutputFrame.Zero), cancellationToken).ConfigureAwait(false);
    }
    public override async Task SendAsync(OutputFrame frame, CancellationToken cancellationToken)
    {
        int a = DgLabProtocol.Strength(WaveformRendering.Strength(frame, 0, true), 200, frame.Gentle);
        int b = DgLabProtocol.Strength(WaveformRendering.Strength(frame, 1, true), 200, frame.Gentle);
        bool known = Volatile.Read(ref _hasLevels) != 0;
        int levels = Volatile.Read(ref _levels);
        int? updateA = known && a == levels >> 8 ? null : a;
        int? updateB = known && b == (levels & 255) ? null : b;
        if (updateA.HasValue || updateB.HasValue)
            await WriteAsync(OpossumProtocol.Strength(updateA, updateB), cancellationToken).ConfigureAwait(false);
        await WriteAsync(DgLabProtocol.OpossumWave(frame), cancellationToken).ConfigureAwait(false);
    }
    protected override (int A, int B)? ParseLevels(byte[] bytes) => DgLabProtocol.ParseOpossum(bytes);
    protected override void AdditionalTelemetry(byte[] bytes)
    {
        if (DgLabProtocol.ParseOpossum(bytes) is { } levels)
        {
            Volatile.Write(ref _levels, levels.A << 8 | levels.B);
            Volatile.Write(ref _hasLevels, 1);
            if (Link.MaximumWriteLength >= 24)
                RequestCommand(new SynchronizeDisplay());
        }
        if (bytes.Length == 16 && bytes[0] == 0xd0)
            Publish(new(Id, telemetry: new(Buttons: BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(2, 2)), Sequence: bytes[1])));
    }
    public override async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        await StopAsync(cancellationToken).ConfigureAwait(false);
        await WriteAsync(OpossumProtocol.Stream(false), cancellationToken).ConfigureAwait(false);
    }
}
