using YokoWave.Core.Localization;

namespace YokoWave.Core.Bluetooth;

internal static class ProtocolGuard
{
    public static void Require(bool valid)
    {
        if (!valid) throw new LocalizedArgumentException("Error.ProtocolSettings");
    }
    public static byte Byte(int value, int minimum = 0, int maximum = 255)
    {
        Require(value >= minimum && value <= maximum);
        return (byte)value;
    }
    public static ushort Word(int value, int minimum = 0, int maximum = ushort.MaxValue)
    {
        Require(value >= minimum && value <= maximum);
        return (ushort)value;
    }
}
