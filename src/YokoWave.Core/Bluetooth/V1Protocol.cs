using System.Buffers.Binary;
using YokoWave.Core.Localization;

namespace YokoWave.Core.Bluetooth;

/// <summary>Independent encoder for the published YSKJ EMS V1.6 wire format.</summary>
public static class V1Protocol
{
    public const string DeviceName = "YYC-DJ";
    public static readonly Guid Service = Guid.Parse("0000ff30-0000-1000-8000-00805f9b34fb");
    public static readonly Guid WriteCharacteristic = Guid.Parse("0000ff31-0000-1000-8000-00805f9b34fb");
    public static readonly Guid NotifyCharacteristic = Guid.Parse("0000ff32-0000-1000-8000-00805f9b34fb");
    public const int MaxStrength = 276;

    public static byte[] Stop() => Channel(3, 0, 0, 0);
    public static byte[] Query(byte type) => WithChecksum([0x35, 0x71, type]);

    public static IReadOnlyList<byte[]> Encode(OutputFrame frame)
    {
        if (frame.ReferenceSample?.Motors is not null || frame.ReferenceSampleB?.Motors is not null)
            throw new LocalizedArgumentException("Error.ElectricalWaveform");
        var a = Parameters(frame.ChannelA, frame.Frequency, frame.ReferenceSample, frame.Gentle);
        var b = Parameters(frame.ChannelB, frame.Frequency, frame.ReferenceSampleB ?? frame.ReferenceSample, frame.Gentle);
        return a == b ? [Channel(3, a.Strength, a.Frequency, a.PulseWidth)]
            : [Channel(1, a.Strength, a.Frequency, a.PulseWidth), Channel(2, b.Strength, b.Frequency, b.PulseWidth)];
    }

    internal static (int Strength, int Frequency, int PulseWidth) Parameters(double strength, double frequency, WaveformSample? sample, bool gentle = false)
    {
        if (sample?.Motors is not null) throw new LocalizedArgumentException("Error.ElectricalWaveform");
        var level = double.IsFinite(strength) ? Math.Clamp(strength, 0, 100) : 0;
        if (level == 0) return (0, 0, 0);
        var code = sample?.FrequencyCode ?? frequency;
        var pulse = sample?.PulseWidthCode ?? 100;
        if (!double.IsFinite(code) || !double.IsFinite(pulse)) throw new LocalizedArgumentException("Error.Samples");
        var encodedStrength = gentle ? (int)Math.Floor(level * 180 / 100)
            : (int)Math.Round(level * MaxStrength / 100, MidpointRounding.AwayFromZero);
        if (encodedStrength == 0) return (0, 0, 0);
        return (encodedStrength,
            (int)Math.Clamp(Math.Round(code), 1, 100), (int)Math.Clamp(Math.Round(pulse), 0, 100));
    }

    private static byte[] Channel(byte channel, int strength, int frequency, int pulseWidth)
    {
        byte[] data = [0x35, 0x11, channel, (byte)(strength > 0 ? 1 : 0), 0, 0,
            (byte)(strength > 0 ? 0x11 : 0), (byte)frequency, (byte)pulseWidth];
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(4, 2), (ushort)strength);
        return WithChecksum(data);
    }

    public static byte[] WithChecksum(ReadOnlySpan<byte> data)
    {
        var packet = new byte[data.Length + 1];
        data.CopyTo(packet);
        foreach (var value in data) packet[^1] = unchecked((byte)(packet[^1] + value));
        return packet;
    }

    public static V1Status? Parse(ReadOnlySpan<byte> packet)
    {
        if (packet.Length == 5 && packet[0] == 0x36 && packet[1] == 0x13 &&
            packet[2] > 0 && packet[3] > 0 && WithChecksum(packet[..^1])[^1] == packet[^1])
            return new(HardwareVersion: packet[2], SoftwareVersion: packet[3]);
        if (packet.Length < 5 || packet[0] != 0x35 || packet[1] != 0x71) return null;
        var expectedLength = packet[2] switch { 1 or 2 => 9, 3 or 4 or 7 or 0x55 => 5, 5 => 6, 6 => 16, _ => 0 };
        if (packet.Length != expectedLength || WithChecksum(packet[..^1])[^1] != packet[^1]) return null;
        return packet[2] switch
        {
            4 when packet[3] <= 100 => new(Battery: packet[3]),
            1 when packet[3] <= 2 && packet[4] <= 1 => new(ContactA: (ElectrodeContact)packet[3], OutputAEnabled: packet[4] == 1),
            2 when packet[3] <= 2 && packet[4] <= 1 => new(ContactB: (ElectrodeContact)packet[3], OutputBEnabled: packet[4] == 1),
            0x55 => new(ErrorCode: packet[3]),
            7 when packet[3] <= 1 => new(LightEnabled: packet[3] == 1),
            _ => null
        };
    }
}

public sealed record V1Status(int? Battery = null, ElectrodeContact? ContactA = null,
    ElectrodeContact? ContactB = null, byte? ErrorCode = null, bool? OutputAEnabled = null, bool? OutputBEnabled = null,
    byte? HardwareVersion = null, byte? SoftwareVersion = null, bool? LightEnabled = null);
