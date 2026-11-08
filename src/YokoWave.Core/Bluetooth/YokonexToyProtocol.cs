using System.Buffers.Binary;
using System.Security.Cryptography;
using YokoWave.Core.Localization;

namespace YokoWave.Core.Bluetooth;

public static class YokonexToyProtocol
{
    // Public, fixed interoperability key from the manufacturer's pump protocol; not an account credential.
    private static readonly byte[] PumpKey = Convert.FromHexString("F638BC9CFA477480AB3242F6B04557A1");
    public static byte[] MotorInfo() => V1Protocol.WithChecksum([0x35, 0x10]);
    public static byte[] MotorPreset(int mask, int mode) => V1Protocol.WithChecksum([0x35, 0x11, ProtocolGuard.Byte(mask, 1, 7), ProtocolGuard.Byte(mode)]);
    public static byte[] MotorStop() => V1Protocol.WithChecksum([0x35, 0x12, 0, 0, 0]);
    public static byte[] MotorOutput(OutputFrame frame, int mask)
    {
        byte[] values = new byte[3];
        for (var channel = 0; channel < 3; channel++)
        {
            var strength = WaveformRendering.Strength(frame, channel);
            values[channel] = (mask & 1 << channel) == 0 ? (byte)0
                : (byte)Math.Floor(DgLabProtocol.Strength(strength, 20, frame.Gentle) * DgLabProtocol.MotorAmplitude(frame, channel) / 100d);
        }
        return V1Protocol.WithChecksum([0x35, 0x12, .. values]);
    }
    public static byte[] PumpCommand(byte command, PumpDirection direction = PumpDirection.Off, bool water = false, byte[]? padding = null, ushort seconds = 1)
    {
        byte[] data = new byte[16];
        RandomNumberGenerator.Fill(data);
        if (padding is not null)
        {
            if (padding.Length != 16) throw new ArgumentException(nameof(padding));
            padding.CopyTo(data, 0);
        }
        data[0] = 0xbf;
        data[1] = 0x0f;
        data[2] = 0xa0;
        data[3] = command;
        if (command is 1 or 2)
        {
            if (!Enum.IsDefined(direction)) throw new LocalizedArgumentException("Error.PumpSettings");
            data[4] = command == 1 ? (byte)direction : water ? (byte)1 : (byte)0;
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(5, 2), seconds);
            // Runtime renews one-second commands while running.
        }
        using var aes = Aes.Create();
        aes.Key = PumpKey;
        return aes.EncryptEcb(data, PaddingMode.None);
    }
    public static byte[]? DecryptPump(ReadOnlySpan<byte> encrypted)
    {
        if (encrypted.Length != 16) return null;
        using var aes = Aes.Create();
        aes.Key = PumpKey;
        var data = aes.DecryptEcb(encrypted, PaddingMode.None);
        return data[0] == 0xbf && data[1] == 0x0f && data[2] == 0xb0 ? data : null;
    }
}
