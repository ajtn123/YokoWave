namespace YokoWave.Core.Bluetooth;

public static class OpossumProtocol
{
    public static byte[] Stream(bool enabled, int indicatorColor = 1) => [0x50, ProtocolGuard.Byte(indicatorColor, 1, 6), enabled ? (byte)1 : (byte)0];
    public static byte[] Display(int channelA, int channelB) =>
        [0xb2, .. Convert.FromHexString("FFFF00FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF0809"), ProtocolGuard.Byte(channelA, 0, 200), ProtocolGuard.Byte(channelB, 0, 200)];
    public static byte[] Strength(int? channelA, int? channelB) => [0xb3,
        channelA.HasValue ? ProtocolGuard.Byte(channelA.Value, 0, 200) : (byte)0xff,
        channelB.HasValue ? ProtocolGuard.Byte(channelB.Value, 0, 200) : (byte)0xff];
}
