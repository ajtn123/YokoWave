using System.Buffers.Binary;

namespace YokoWave.Core.Bluetooth;

/// <summary>Published V2 real-time mode: both channel configurations in one packet.</summary>
public static class V2Protocol
{
    public static byte[] Stop() => Encode(OutputFrame.Zero);
    public static byte[] QueryVersion() => [0x36, 0x13, 0, 0, 0x35];
    public static byte[] QueryLight() => V1Protocol.Query(7);
    public static byte[] SetLight(bool enabled) => V1Protocol.WithChecksum([0x35, 0x15, (byte)(enabled ? 1 : 0)]);

    public static byte[] Encode(OutputFrame frame)
    {
        var a = V1Protocol.Parameters(frame.ChannelA, frame.Frequency, frame.ReferenceSample, frame.Gentle);
        var b = V1Protocol.Parameters(frame.ChannelB, frame.Frequency, frame.ReferenceSampleB ?? frame.ReferenceSample, frame.Gentle);
        byte[] data = [0x35, 0x11, 0x02, 0, 0, (byte)Math.Max(1, a.Frequency), (byte)a.PulseWidth,
            0, 0, (byte)Math.Max(1, b.Frequency), (byte)b.PulseWidth];
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(3, 2), (ushort)a.Strength);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(7, 2), (ushort)b.Strength);
        return V1Protocol.WithChecksum(data);
    }
}
