namespace YokoWave.Core.Bluetooth;

[Flags]
public enum OpossumButton : ushort
{
    Select1 = 1 << 0,
    Select2 = 1 << 1,
    Home = 1 << 2,
    Up = 1 << 8,
    Down = 1 << 9,
    Left = 1 << 10,
    Right = 1 << 11,
    B = 1 << 12,
    A = 1 << 13,
    G = 1 << 14,
    D = 1 << 15
}

public static class OpossumProtocol
{
    public static IReadOnlyList<OpossumButton> PressedButtons(ushort bits) => Enum.GetValues<OpossumButton>()
        .Where(button => (bits & (ushort)button) != 0).ToArray();
    public static byte[] Stream(bool enabled) => [0x50, 1, enabled ? (byte)1 : (byte)0];
    public static byte[] Display(int channelA, int channelB) =>
        [0xb2, .. Convert.FromHexString("FFFF00FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF0809"), ProtocolGuard.Byte(channelA, 0, 200), ProtocolGuard.Byte(channelB, 0, 200)];
    public static byte[] Strength(int? channelA, int? channelB) => [0xb3,
        channelA.HasValue ? ProtocolGuard.Byte(channelA.Value, 0, 200) : (byte)0xff,
        channelB.HasValue ? ProtocolGuard.Byte(channelB.Value, 0, 200) : (byte)0xff];
}
