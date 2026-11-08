using System.Buffers.Binary;

namespace YokoWave.Core.Bluetooth;

/// <summary>Independent implementation of the public Paw V1.1 trigger and notification layouts.</summary>
public static class PawProtocol
{
    public static byte[] Configure(ConfigurePaw command)
    {
        command.Validate();
        byte[] packet = new byte[17];
        packet[0] = 0x50;
        packet[1] = (byte)command.IndicatorColor;
        switch (command.Trigger)
        {
            case PawNoTrigger: break;
            case PawLiveData:
                packet[2] = 0xd0;
                packet[3] = 1;
                break;
            case PawRandomReaction random:
                packet[2] = 3;
                packet[3] = (byte)random.EventId;
                Word(packet, 4, random.MinimumSeconds);
                Word(packet, 6, random.MaximumSeconds);
                Word(packet, 8, random.ReactionSeconds);
                Dynamics(packet.AsSpan(10, 4), random.Dynamics ?? new());
                break;
            case PawProbability probability:
                packet[2] = 4;
                for (var index = 0; index < probability.Events.Count; index++)
                {
                    packet[3 + index * 2] = (byte)probability.Events[index].EventId;
                    packet[4 + index * 2] = (byte)probability.Events[index].Probability;
                }
                Word(packet, 15, probability.CooldownSeconds);
                break;
            case PawVoltage voltage:
                packet[2] = 0x0f;
                packet[3] = (byte)voltage.EventId;
                packet[4] = voltage.HighImpedance ? (byte)1 : (byte)0;
                packet[5] = (byte)voltage.MinimumRaw;
                packet[6] = (byte)voltage.MaximumRaw;
                packet[7] = (byte)voltage.MappingRange;
                break;
            case PawButtonMotion button:
                var motion = button.Motion ?? new();
                packet[2] = 5;
                packet[3] = (byte)button.ButtonEventId;
                packet[4] = (byte)((button.TriggerOnRelease ? 0x80 : 0) | (motion.UseAngles ? 0x40 : 0) | motion.TriggerDebounce << 3 | motion.CancelDebounce);
                Dynamics(packet.AsSpan(5, 4), button.Dynamics ?? new());
                packet[9] = (byte)motion.EventId;
                if (motion.UseAngles)
                {
                    Axis(packet, 10, motion.X ?? new());
                    Axis(packet, 12, motion.Y ?? new());
                    Axis(packet, 14, motion.Z ?? new());
                }
                else
                {
                    packet[10] = motion.AccelerationAbove ? (byte)1 : (byte)0;
                    Word(packet, 11, motion.AccelerationThreshold);
                }
                packet[16] = (byte)motion.MappingRange;
                break;
            default: throw new Localization.LocalizedArgumentException("Error.ProtocolSettings");
        }
        return packet;
    }
    private static void Dynamics(Span<byte> data, PawParameterDynamics dynamics)
    {
        data[0] = (byte)dynamics.IncreaseTimeCode;
        data[1] = (byte)dynamics.CancelDecrease;
        data[2] = (byte)dynamics.DecreaseTimeCode;
        data[3] = (byte)dynamics.TriggerIncrease;
    }
    private static void Word(Span<byte> data, int offset, int value) => BinaryPrimitives.WriteUInt16BigEndian(data.Slice(offset, 2), (ushort)value);
    private static void Axis(Span<byte> data, int offset, PawAxisRange range)
    {
        data[offset] = unchecked((byte)(sbyte)range.Minimum);
        data[offset + 1] = unchecked((byte)(sbyte)range.Maximum);
    }
    public static byte[] ShoulderLight(ConfigureShoulderLight command)
    {
        command.Validate();
        return command.Blink == ShoulderBlink.Solid ? [0x70, (byte)command.Color]
            : [0x70, (byte)command.Color, (byte)command.AlternateColor, (byte)command.Blink];
    }
    public static PawCalibration? ParseCalibration(ReadOnlySpan<byte> data)
    {
        if (data.Length != 14 || data[0] != 0xf1 || data[1] != 0x61) return null;
        var values = new short[6];
        for (var index = 0; index < 6; index++) values[index] = BinaryPrimitives.ReadInt16BigEndian(data.Slice(2 + index * 2, 2));
        if (values[0] > values[1] || values[2] > values[3] || values[4] > values[5]) return null;
        return new(values[0], values[1], values[2], values[3], values[4], values[5]);
    }
    public static double Voltage(int raw)
    {
        ProtocolGuard.Byte(raw);
        return raw <= 232 ? Math.Round(raw / 8d, MidpointRounding.AwayFromZero) * .07 : 2.1;
    }
}
