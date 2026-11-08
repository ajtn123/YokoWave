using System.Buffers.Binary;

namespace YokoWave.Core.Bluetooth;

/// <summary>Published V2 real-time mode: both channel configurations in one packet.</summary>
public static class V2Protocol
{
    public static byte[] Stop() => Encode(OutputFrame.Zero);
    public static byte[] QueryVersion() => [0x36, 0x13, 0, 0, 0x35];
    public static byte[] QueryLight() => V1Protocol.Query(7);
    public static byte[] SetLight(bool enabled) => V1Protocol.WithChecksum([0x35, 0x15, (byte)(enabled ? 1 : 0)]);
    public static byte[] Fixed(OutputFrame frame, NativeProgramSettings settings)
    {
        settings.Validate();
        var a = settings.PresetA == 0 ? 0 : V1Protocol.Parameters(frame, 0).Strength;
        var b = settings.PresetB == 0 ? 0 : V1Protocol.Parameters(frame, 1).Strength;
        return V1Protocol.WithChecksum([0x35, 0x11, 1, (byte)(a >> 8), (byte)a, (byte)Math.Max(1, settings.PresetA),
            (byte)(b >> 8), (byte)b, (byte)Math.Max(1, settings.PresetB)]);
    }
    public static byte[] Buffered(int channel, int strength, IReadOnlyList<FrequencyPulse> samples)
    {
        ProtocolGuard.Byte(channel, 1, 2);
        ProtocolGuard.Word(strength, 0, V1Protocol.MaxStrength);
        ProtocolGuard.Require(samples.Count is >= 1 and <= 100);
        byte[] packet = new byte[6 + samples.Count * 2];
        packet[0] = 0x35;
        packet[1] = 0x11;
        packet[2] = 3;
        packet[3] = (byte)channel;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4, 2), (ushort)strength);
        for (var index = 0; index < samples.Count; index++)
        {
            samples[index].Validate();
            packet[6 + index * 2] = (byte)samples[index].FrequencyHz;
            packet[7 + index * 2] = (byte)samples[index].PulseWidth;
        }
        return V1Protocol.WithChecksum(packet);
    }

    public static byte[] Encode(OutputFrame frame)
    {
        var a = V1Protocol.Parameters(frame, 0);
        var b = V1Protocol.Parameters(frame, 1);
        byte[] data = [0x35, 0x11, 0x02, 0, 0, (byte)Math.Max(1, a.Frequency), (byte)a.PulseWidth,
            0, 0, (byte)Math.Max(1, b.Frequency), (byte)b.PulseWidth];
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(3, 2), (ushort)a.Strength);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(7, 2), (ushort)b.Strength);
        return V1Protocol.WithChecksum(data);
    }
}
