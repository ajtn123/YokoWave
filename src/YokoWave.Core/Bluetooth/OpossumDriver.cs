using System.Buffers.Binary;

namespace YokoWave.Core.Bluetooth;

internal sealed class OpossumDriver(string id, BluetoothDeviceProfile profile, IBluetoothConnection link,
    Action<DeviceStatusUpdate> publish, Action<Exception> fault) : DgOutputDriver(id, profile, link, publish, fault)
{
    private int _levels;
    private int _hasLevels;
    protected override DeviceCommandHandlers CreateCommandHandlers() => new DeviceCommandHandlers()
        .Add<QueryDevice>((command, ct) => command.Query is DeviceQuery.All or DeviceQuery.Battery
            ? Battery(BluetoothDeviceProfiles.DgBattery, ct) : throw new Localization.LocalizedOperationException("Error.DeviceFeatures"))
        .Add<ConfigureSensorStream>((command, ct) => Write(OpossumProtocol.Stream(command.Enabled), ct))
        .Add<SynchronizeDisplay>((_, ct) =>
        {
            var levels = Volatile.Read(ref _levels);
            var packet = OpossumProtocol.Display(levels >> 8, levels & 255);
            if (packet.Length > Link.MaximumWriteLength) throw new Localization.LocalizedOperationException("Error.DisplayPacketSize");
            return Write(packet, ct);
        });
    public override async Task Initialize(CancellationToken ct)
    {
        await Stop(ct);
        await Write([0x50, 1, 1], ct);
        await Battery(BluetoothDeviceProfiles.DgBattery, ct);
    }
    public override async Task Stop(CancellationToken ct)
    {
        await Write([0xb3, 0, 0], ct);
        await Write(DgLabProtocol.OpossumWave(OutputFrame.Zero), ct);
    }
    public override async Task Send(OutputFrame frame, CancellationToken ct)
    {
        var a = DgLabProtocol.Strength(WaveformRendering.Strength(frame, 0, true), 200, frame.Gentle);
        var b = DgLabProtocol.Strength(WaveformRendering.Strength(frame, 1, true), 200, frame.Gentle);
        var known = Volatile.Read(ref _hasLevels) != 0;
        var levels = Volatile.Read(ref _levels);
        int? updateA = known && a == levels >> 8 ? null : a;
        int? updateB = known && b == (levels & 255) ? null : b;
        if (updateA.HasValue || updateB.HasValue) await Write(OpossumProtocol.Strength(updateA, updateB), ct);
        await Write(DgLabProtocol.OpossumWave(frame), ct);
    }
    protected override (int A, int B)? ParseLevels(byte[] bytes) => DgLabProtocol.ParseOpossum(bytes);
    protected override void AdditionalTelemetry(byte[] bytes)
    {
        if (DgLabProtocol.ParseOpossum(bytes) is { } levels)
        {
            Volatile.Write(ref _levels, levels.A << 8 | levels.B);
            Volatile.Write(ref _hasLevels, 1);
            if (Link.MaximumWriteLength >= 24) RequestCommand(new SynchronizeDisplay());
        }
        if (bytes.Length == 16 && bytes[0] == 0xd0)
            Publish(new(Id, telemetry: new(Buttons: BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(2, 2)), Sequence: bytes[1])));
    }
    public override async Task Shutdown(CancellationToken ct)
    {
        await Stop(ct);
        await Write(OpossumProtocol.Stream(false), ct);
    }
}
