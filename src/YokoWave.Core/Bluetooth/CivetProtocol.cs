using System.Buffers.Binary;

namespace YokoWave.Core.Bluetooth;

public static class CivetProtocol
{
    public static byte[] Stream(bool enabled)
    {
        byte[] data = new byte[17];
        data[0] = 0x50;
        data[1] = 1;
        data[2] = enabled ? (byte)0xd0 : (byte)0;
        return data;
    }
    public static byte[] Configure(ConfigurePressureSensor command)
    {
        command.Validate();
        byte[] data = new byte[13];
        data[0] = 0x66;
        data[10] = (byte)command.Orientation;
        data[12] = command.ResetPressure ? (byte)2 : (byte)0;
        return data;
    }
    public static DeviceTelemetry? Parse(ReadOnlySpan<byte> data) => data.Length >= 10 && data[0] == 0xd0
        ? new(PressureKpa: BinaryPrimitives.ReadInt16LittleEndian(data[8..10]) / 100d, IndicatorColor: data[1]) : null;
}
