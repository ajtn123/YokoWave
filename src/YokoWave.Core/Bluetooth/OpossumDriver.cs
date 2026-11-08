using System.Buffers.Binary;

namespace YokoWave.Core.Bluetooth;

internal sealed class OpossumDriver(string id, BluetoothDeviceProfile profile, IBluetoothConnection link,
    Action<DeviceStatusUpdate> publish, Action<Exception> fault) : DgOutputDriver(id, profile, link, publish, fault)
{
    public override async Task Initialize(CancellationToken ct)
    {
        await Stop(ct); await Write([0x50, 1, 1], ct); await Battery(BluetoothDeviceProfiles.DgBattery, ct);
    }
    public override async Task Stop(CancellationToken ct)
    { await Write([0xb3, 0, 0], ct); await Write(DgLabProtocol.OpossumWave(OutputFrame.Zero), ct); }
    public override async Task Send(OutputFrame frame, CancellationToken ct)
    {
        await Write([0xb3, (byte)DgLabProtocol.Strength(WaveformRendering.Strength(frame, 0, true), 200, frame.Gentle),
            (byte)DgLabProtocol.Strength(WaveformRendering.Strength(frame, 1, true), 200, frame.Gentle)], ct);
        await Write(DgLabProtocol.OpossumWave(frame), ct);
    }
    protected override (int A, int B)? ParseLevels(byte[] bytes) => DgLabProtocol.ParseOpossum(bytes);
    protected override void AdditionalTelemetry(byte[] bytes)
    {
        if (bytes.Length == 16 && bytes[0] == 0xd0)
            Publish(new(Id, telemetry: new(Buttons: BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(2, 2)))));
    }
}
