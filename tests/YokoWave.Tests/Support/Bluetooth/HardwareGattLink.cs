using System.Security.Cryptography;
using YokoWave.Core.Bluetooth;

namespace YokoWave.Tests.Support.Bluetooth;

internal sealed class HardwareGattLink(BluetoothDeviceProfile profile) : IBluetoothConnection
{
    private int _levelA;
    private int _levelB;
    public List<(GattAddress Address, byte[] Data)> Writes { get; } = [];
    public List<GattAddress> Reads { get; } = [];
    public int MaximumWriteLength { get; set; } = 20;
    public bool Closed { get; private set; }
    public bool Reply { get; set; } = true;
    public bool FailWrites { get; set; }

    public event Action<byte[]>? NotificationReceived;
    public event Action<GattNotification>? AdditionalNotificationReceived;
    public event Action? Disconnected;
    public void Notify(byte[] data) => NotificationReceived?.Invoke(data);
    public void LoseConnection() => Disconnected?.Invoke();
    public Task WriteAsync(byte[] packet, CancellationToken cancellationToken) => WriteAsync(profile.Write, packet, cancellationToken);
    public Task WriteAsync(GattAddress address, byte[] packet, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Closed || FailWrites)
            throw new IOException("Closed link");
        Writes.Add((address, packet.ToArray()));
        if (!Reply)
            return Task.CompletedTask;
        if (profile.Protocol is BluetoothProtocol.YokonexV1 or BluetoothProtocol.YokonexV2 && packet[1] == 0x71)
        {
            if (packet[2] == 4)
                Notify(V1Protocol.WithChecksum([0x35, 0x71, 4, 80]));
            if (packet[2] is 1 or 2)
                Notify(V1Protocol.WithChecksum([0x35, 0x71, packet[2], 0, 0, 0, 0, 0]));
        }

        if (profile.Protocol == BluetoothProtocol.CoyoteV3 && packet[0] == 0xb0)
        {
            static int Apply(int actual, int kind, int value) => Math.Clamp(kind switch
            {
                1 => actual + value,
                2 => actual - value,
                3 => value,
                _ => actual
            }, 0, 200);
            _levelA = Apply(_levelA, packet[1] >> 2 & 3, packet[2]);
            _levelB = Apply(_levelB, packet[1] & 3, packet[3]);
            Notify([0xb1, (byte)(packet[1] >> 4), (byte)_levelA, (byte)_levelB]);
        }

        if (profile.Protocol == BluetoothProtocol.Opossum && packet[0] == 0xb3)
        {
            if (packet[1] != 255)
                _levelA = packet[1];
            if (packet[2] != 255)
                _levelB = packet[2];
            Notify([0xb3, (byte)_levelA, (byte)_levelB]);
        }

        if (profile.Protocol == BluetoothProtocol.Paw && packet[0] == 0x50)
        {
            Notify([0x51, 1, 3, 80]);
            Notify([0xd0, 1, 1, 0, 2, 3, 4, 5, 6]);
        }

        if (profile.Protocol == BluetoothProtocol.Civet && packet[0] == 0x50)
            Notify([0xd0, 1, 0, 0, 0, 0, 0, 0, 123, 0]);
        if (profile.Protocol == BluetoothProtocol.YokonexMotor && packet[1] == 0x10)
            Notify(V1Protocol.WithChecksum([0x35, 0x10, 1, 1, 5, 5, 5, 0, 0]));
        if (profile.Protocol == BluetoothProtocol.YokonexPump)
        {
            byte[] plain = Decrypt(packet);
            byte[] response = new byte[16];
            response[0] = 0xbf;
            response[1] = 0x0f;
            response[2] = 0xb0;
            if (plain[3] == 4)
            {
                response[3] = 1;
                Notify(Encrypt(response));
                response[3] = 2;
                response[5] = 7;
                Notify(Encrypt(response));
            }

            if (plain[3] == 5)
            {
                response[3] = 3;
                response[4] = 75;
                Notify(Encrypt(response));
            }
        }

        return Task.CompletedTask;
    }

    public Task<byte[]?> ReadAsync(GattAddress address, CancellationToken cancellationToken)
    {
        Reads.Add(address);
        if (address == BluetoothDeviceProfiles.V2WaveA || address == BluetoothDeviceProfiles.V2WaveB)
            return Task.FromResult<byte[]?>([1, 1, 1]);
        if (address == BluetoothDeviceProfiles.V2Strength)
            return Task.FromResult<byte[]?>([0, 0, 0]);
        byte[] battery = [80];
        AdditionalNotificationReceived?.Invoke(new(address, battery));
        return Task.FromResult<byte[]?>(battery);
    }

    public static byte[] Encrypt(byte[] data)
    {
        using var aes = Aes.Create();
        aes.Key = Convert.FromHexString("F638BC9CFA477480AB3242F6B04557A1");
        return aes.EncryptEcb(data, PaddingMode.None);
    }

    public static byte[] Decrypt(byte[] data)
    {
        using var aes = Aes.Create();
        aes.Key = Convert.FromHexString("F638BC9CFA477480AB3242F6B04557A1");
        return aes.DecryptEcb(data, PaddingMode.None);
    }

    public ValueTask DisposeAsync()
    {
        Closed = true;
        return ValueTask.CompletedTask;
    }
}
