using System.Buffers.Binary;

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
    public static byte[] MotionStreaming(bool enabled) => WithChecksum([0x35, 0x14, (byte)(enabled ? 1 : 0)]);
    public static byte[] AuxiliaryMotor(AuxiliaryMotorMode mode)
    {
        ProtocolGuard.Require(Enum.IsDefined(mode));
        return WithChecksum([0x35, 0x12, (byte)mode]);
    }
    public static IReadOnlyList<byte[]> Fixed(OutputFrame frame, NativeProgramSettings settings)
    {
        settings.Validate();
        int a = Parameters(frame, 0).Strength;
        int b = Parameters(frame, 1).Strength;
        if (settings.PresetA == 0)
            a = 0;
        if (settings.PresetB == 0)
            b = 0;
        return a == b && settings.PresetA == settings.PresetB ? [FixedChannel(3, a, settings.PresetA)]
            : [FixedChannel(1, a, settings.PresetA), FixedChannel(2, b, settings.PresetB)];
    }

    public static IReadOnlyList<byte[]> Encode(OutputFrame frame)
    {
        var a = Parameters(frame, 0);
        var b = Parameters(frame, 1);
        return a == b ? [Channel(3, a.Strength, a.Frequency, a.PulseWidth)]
            : [Channel(1, a.Strength, a.Frequency, a.PulseWidth), Channel(2, b.Strength, b.Frequency, b.PulseWidth)];
    }

    internal static (int Strength, int Frequency, int PulseWidth) Parameters(OutputFrame frame, int channel)
    {
        double level = WaveformRendering.Strength(frame, channel);
        if (level == 0)
            return (0, 0, 0);
        var sample = WaveformRendering.Sample(frame, channel);
        int encodedStrength = frame.Gentle ? (int)Math.Floor(level * 180 / 100)
            : (int)Math.Round(level * MaxStrength / 100, MidpointRounding.AwayFromZero);
        if (encodedStrength == 0)
            return (0, 0, 0);
        return (encodedStrength,
            (int)Math.Clamp(Math.Round(sample.FrequencyHz), 1, 100), (int)Math.Round(WaveformRendering.Width(frame, channel) * 100));
    }

    public static byte[] WithChecksum(ReadOnlySpan<byte> data)
    {
        byte[] packet = new byte[data.Length + 1];
        data.CopyTo(packet);
        foreach (byte value in data)
            packet[^1] = unchecked((byte)(packet[^1] + value));
        return packet;
    }

    public static V1Status? Parse(ReadOnlySpan<byte> packet)
    {
        if (packet.Length == 5 && packet[0] == 0x36 && packet[1] == 0x13 &&
            packet[2] > 0 && packet[3] > 0 && WithChecksum(packet[..^1])[^1] == packet[^1])
        {
            return new(HardwareVersion: packet[2], SoftwareVersion: packet[3]);
        }

        if (packet.Length < 5 || packet[0] != 0x35 || packet[1] != 0x71)
            return null;
        int expectedLength = packet[2] switch { 1 or 2 => 9, 3 or 4 or 7 or 0x55 => 5, 5 => 6, 6 => 16, _ => 0 };
        if (packet.Length != expectedLength || WithChecksum(packet[..^1])[^1] != packet[^1])
            return null;
        return packet[2] switch
        {
            4 when packet[3] <= 100 => new(Battery: packet[3]),
            1 or 2 when packet[3] <= 2 && packet[4] <= 1 => ReadChannel(packet),
            3 when Enum.IsDefined((AuxiliaryMotorMode)packet[3]) => new(AuxiliaryMotor: (AuxiliaryMotorMode)packet[3]),
            0x55 => new(ErrorCode: packet[3]),
            7 when packet[3] <= 1 => new(LightEnabled: packet[3] == 1),
            5 => new(Steps: BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(3, 2))),
            6 => ReadMotion(packet),
            _ => null
        };
    }
    private static byte[] FixedChannel(int channel, int strength, int programId)
    {
        ProtocolGuard.Byte(programId, 0, 16);
        byte[] packet = [0x35, 0x11, (byte)channel, strength > 0 ? (byte)1 : (byte)0, (byte)(strength >> 8), (byte)strength,
            strength > 0 ? (byte)programId : (byte)0, 0, 0];
        return WithChecksum(packet);
    }

    private static byte[] Channel(byte channel, int strength, int frequency, int pulseWidth)
    {
        byte[] data = [0x35, 0x11, channel, (byte)(strength > 0 ? 1 : 0), 0, 0,
            (byte)(strength > 0 ? 0x11 : 0), (byte)frequency, (byte)pulseWidth];
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(4, 2), (ushort)strength);
        return WithChecksum(data);
    }
    private static V1Status? ReadChannel(ReadOnlySpan<byte> packet)
    {
        bool enabled = packet[4] == 1;
        int strength = enabled ? BinaryPrimitives.ReadUInt16BigEndian(packet[5..7]) : 0;
        int mode = enabled ? packet[7] : 0;
        if (enabled && (strength > MaxStrength || mode is < 1 or > 17))
            return null;
        return packet[2] == 1
            ? new(ContactA: (ElectrodeContact)packet[3], OutputAEnabled: enabled, IntensityA: strength, ModeA: mode)
            : new(ContactB: (ElectrodeContact)packet[3], OutputBEnabled: enabled, IntensityB: strength, ModeB: mode);
    }
    private static V1Status ReadMotion(ReadOnlySpan<byte> packet)
    {
        double x = BinaryPrimitives.ReadInt16BigEndian(packet.Slice(3, 2));
        double y = BinaryPrimitives.ReadInt16BigEndian(packet.Slice(5, 2));
        double z = BinaryPrimitives.ReadInt16BigEndian(packet.Slice(7, 2));
        var acceleration = new SensorAxes((int)x, (int)y, (int)z);
        var gyroscope = new SensorAxes(BinaryPrimitives.ReadInt16BigEndian(packet[9..11]),
            BinaryPrimitives.ReadInt16BigEndian(packet[11..13]), BinaryPrimitives.ReadInt16BigEndian(packet[13..15]));
        if (x == 0 && y == 0 && z == 0)
            return new(AccelerationAxes: acceleration, GyroscopeAxes: gyroscope);
        double angle = (Math.Abs(Math.Atan2(x, Math.Sqrt(y * y + z * z))) +
            Math.Abs(Math.Atan2(y, Math.Sqrt(x * x + z * z)))) * 180 / Math.PI;
        return new(MotionAngle: Math.Clamp(angle, 0, 180), AccelerationAxes: acceleration, GyroscopeAxes: gyroscope);
    }
}

public sealed record V1Status(int? Battery = null, ElectrodeContact? ContactA = null,
    ElectrodeContact? ContactB = null, byte? ErrorCode = null, bool? OutputAEnabled = null, bool? OutputBEnabled = null,
    byte? HardwareVersion = null, byte? SoftwareVersion = null, bool? LightEnabled = null,
    double? MotionAngle = null, int? Steps = null, int? IntensityA = null, int? IntensityB = null,
    int? ModeA = null, int? ModeB = null, AuxiliaryMotorMode? AuxiliaryMotor = null,
    SensorAxes? AccelerationAxes = null, SensorAxes? GyroscopeAxes = null);
