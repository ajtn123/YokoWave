using System.Buffers.Binary;

namespace YokoWave.Core.Bluetooth;

/// <summary>Independent encoders for the public Coyote V2/V3 and Opossum formats.</summary>
public static class DgLabProtocol
{
    public static int Strength(double percent, int maximum, bool gentle = false) =>
        double.IsFinite(percent) ? (int)Math.Floor(Math.Clamp(percent, 0, 100) * maximum / 100 * (gentle ? 0.65 : 1)) : 0;
    public static byte Frequency(double milliseconds)
    {
        var period = Math.Clamp(double.IsFinite(milliseconds) ? milliseconds : 1000, 10, 1000);
        return (byte)Math.Floor(period <= 100 ? period : period <= 600 ? (period - 100) / 5 + 100 : (period - 600) / 10 + 200);
    }
    public static byte[] V3(OutputFrame frame, byte sequence = 0)
    {
        byte[] data = new byte[20]; data[0] = 0xb0; data[1] = (byte)((sequence & 15) << 4 | 15);
        data[2] = (byte)Strength(WaveformRendering.Strength(frame, 0, true), 200, frame.Gentle); data[3] = (byte)Strength(WaveformRendering.Strength(frame, 1, true), 200, frame.Gentle);
        for (var index = 0; index < 4; index++)
        {
            var a = Pulse(frame, false, index); var b = Pulse(frame, true, index);
            data[4 + index] = Frequency(a.Period); data[8 + index] = data[2] == 0 ? (byte)0 : a.Amplitude;
            data[12 + index] = Frequency(b.Period); data[16 + index] = data[3] == 0 ? (byte)0 : b.Amplitude;
        }
        return data;
    }
    public static byte[] Limits(double percent, DgBalanceSettings? balances = null)
    {
        balances ??= new();
        var limit = (byte)Strength(percent, 200);
        return [0xbf, limit, limit, balances.FrequencyA, balances.FrequencyB, balances.WidthA, balances.WidthB];
    }
    public static byte[] V2Strength(double a, double b, bool gentle = false)
    {
        var value = Strength(a, 2047, gentle) << 11 | Strength(b, 2047, gentle);
        return [(byte)value, (byte)(value >> 8), (byte)(value >> 16)];
    }
    public static (int A, int B)? ParseV2Strength(ReadOnlySpan<byte> data)
    {
        if (data.Length != 3 || (data[2] & 0xc0) != 0) return null;
        var value = data[0] | data[1] << 8 | data[2] << 16;
        return (value >> 11 & 2047, value & 2047);
    }
    public static byte[] V2Pulse(OutputFrame frame, bool channelB)
    {
        var pulse = Pulse(frame, channelB, 0, false);
        var period = (int)Math.Round(Math.Clamp(pulse.Period, 10, 1000));
        // Keep X:Y at or below the published 1:9 ratio, including the 10 ms example.
        var x = Math.Clamp((int)Math.Round(Math.Sqrt(period / 1000d) * 15), 1, Math.Min(31, period / 10));
        var y = Math.Clamp(period - x, 0, 1023);
        var z = Strength(WaveformRendering.Strength(frame, channelB ? 1 : 0), 2047, frame.Gentle) == 0 ? 0 : (int)Math.Round(pulse.Amplitude / 5d);
        var packed = x | y << 5 | Math.Clamp(z, 0, 20) << 15;
        return [(byte)packed, (byte)(packed >> 8), (byte)(packed >> 16)];
    }
    private static (double Period, byte Amplitude) Pulse(OutputFrame frame, bool b, int index, bool window = true)
    {
        var channel = b ? 1 : 0;
        var sample = WaveformRendering.Sample(frame, channel, index);
        return (Math.Round(1000 / sample.FrequencyHz, 9), (byte)Math.Round(WaveformRendering.Width(frame, channel, index, window) * 100));
    }
    public static byte[] OpossumWave(OutputFrame frame)
    {
        byte[] packet = new byte[20]; packet[0] = 0xb0;
        for (var index = 0; index < 4; index++)
        {
            packet[8 + index] = MotorAmplitude(frame, 0, index, true);
            packet[16 + index] = MotorAmplitude(frame, 1, index, true);
        }
        return packet;
    }
    public static byte MotorAmplitude(OutputFrame frame, int channel, int index = 0, bool window = false)
    {
        return WaveformRendering.Strength(frame, channel, window) <= 0 ? (byte)0
            : (byte)Math.Round(WaveformRendering.Width(frame, channel, index, window) * 100);
    }
    public static (int A, int B)? ParseV3(ReadOnlySpan<byte> data) =>
        data.Length == 4 && data[0] == 0xb1 && data[1] <= 15 && data[2] <= 200 && data[3] <= 200 ? (data[2], data[3]) : null;
    public static (int A, int B)? ParseOpossum(ReadOnlySpan<byte> data) =>
        data.Length == 3 && data[0] == 0xb3 && data[1] <= 200 && data[2] <= 200 ? (data[1], data[2]) : null;
    public static DeviceTelemetry? ParseSensor(BluetoothProtocol protocol, ReadOnlySpan<byte> data)
    {
        if (protocol == BluetoothProtocol.Civet)
            return data.Length >= 10 && data[0] == 0xd0 ? new(PressureKpa: BinaryPrimitives.ReadInt16LittleEndian(data[8..10]) / 100d) : null;
        if (protocol != BluetoothProtocol.Paw || data.IsEmpty) return null;
        return data[0] switch
        {
            0xd0 when data.Length >= 9 => new(Pressed: data[3] != 0, Acceleration: data[4], AngleX: (sbyte)data[5], AngleY: (sbyte)data[6], AngleZ: (sbyte)data[7], VoltageRaw: data[8]),
            0x5a or 0x5c when data.Length == 4 && data[2] is >= 1 and <= 24 => new(EventId: data[2], Parameter: data[3]),
            0x5b when data.Length == 3 && data[2] is >= 1 and <= 24 => new(EventId: data[2], Parameter: 0),
            _ => null
        };
    }
    public static byte[] SensorStream(BluetoothProtocol protocol, bool enabled)
    {
        byte[] command = new byte[17]; command[0] = 0x50; command[1] = 1; command[2] = enabled ? (byte)0xd0 : (byte)0;
        if (protocol == BluetoothProtocol.Paw && enabled) command[3] = 1;
        return command;
    }
    public static byte[] ResetPressure() => Convert.FromHexString("66000000000000000000000002");
}
