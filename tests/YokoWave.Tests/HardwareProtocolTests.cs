using System.Security.Cryptography;
using YokoWave.Core;
using YokoWave.Core.Bluetooth;
using YokoWave.Core.Localization;
using Xunit;

namespace YokoWave.Tests;

public sealed class HardwareProtocolTests
{
    [Fact]
    public void CoyoteV2PublishedBitFieldsAndPulseExamples()
    {
        Assert.Equal("00F83F", Convert.ToHexString(DgLabProtocol.V2Strength(100, 0)));
        Assert.Equal("FF0700", Convert.ToHexString(DgLabProtocol.V2Strength(0, 100)));
        Assert.Equal((2047, 2047), DgLabProtocol.ParseV2Strength(Convert.FromHexString("FFFF3F")));
        Assert.Null(DgLabProtocol.ParseV2Strength(Convert.FromHexString("FFFFFF")));
        var frame = new OutputFrame(100, 100, 100, new(1, 100, 1));
        Assert.Equal("21010A", Convert.ToHexString(DgLabProtocol.V2Pulse(frame, false))); // X=1, Y=9, Z=20.
        Assert.Equal("210102", Convert.ToHexString(DgLabProtocol.V2Pulse(frame with
        {
            ReferenceSample = new(1, 100, .2)
        }, false)));
        Assert.Equal(BluetoothDeviceProfiles.Coyote2(0x1506), BluetoothDeviceProfiles.V2WaveA.Characteristic);
        Assert.Equal(BluetoothDeviceProfiles.Coyote2(0x1505), BluetoothDeviceProfiles.V2WaveB.Characteristic);
    }

    [Fact]
    public void CoyoteV3EncodesFourDistinct25MillisecondSamplesAndAbsoluteStrength()
    {
        WaveformSample[] samples = [new(1, 10, 0), new(1, 1000d / 101, .25), new(1, 1000d / 600, .5), new(1, 1, 1)];
        var frame = new OutputFrame(50, 25, 20, WindowA: samples, WindowB: samples);
        Assert.Equal("B03F64326464C8F0001932646464C8F000193264", Convert.ToHexString(DgLabProtocol.V3(frame, 3)));
        Assert.Equal("BF787880808080", Convert.ToHexString(DgLabProtocol.Limits(60)));
        Assert.Equal((100, 50), DgLabProtocol.ParseV3([0xb1, 3, 100, 50]));
        Assert.Null(DgLabProtocol.ParseV3([0xb1, 16, 100, 50]));
        Assert.Null(DgLabProtocol.ParseV3([0xb1, 3, 201, 0]));
        Assert.Equal((byte)0, DgLabProtocol.V3(frame with
        {
            ChannelA = 0
        })[11]);
        Assert.Throws<LocalizedArgumentException>(() => DgLabProtocol.V3(frame with { ReferenceSample = new(1, 0), WindowA = null }));
    }

    [Fact]
    public void PumpMatchesManufacturersAesVectorAndBigEndianDuration()
    {
        var padding = Convert.FromHexString("BF0FA00101003C5A15D81EC2D3724A63");
        Assert.Equal("1A464D2FC299C556140ACE015084751D", Convert.ToHexString(
            YokonexToyProtocol.PumpCommand(1, PumpDirection.Forward, padding: padding, seconds: 60)));
        var runtime = HardwareGattLink.Decrypt(YokonexToyProtocol.PumpCommand(1, PumpDirection.Reverse));
        Assert.Equal(new byte[] { 0xbf, 0x0f, 0xa0, 1, 2, 0, 1 }, runtime[..7]);
        var reply = Convert.FromHexString("BF0FB0021234ABCD0000000000000000");
        Assert.Equal(reply, YokonexToyProtocol.DecryptPump(HardwareGattLink.Encrypt(reply)));
        Assert.Null(YokonexToyProtocol.DecryptPump(new byte[15]));
        Assert.Null(YokonexToyProtocol.DecryptPump(HardwareGattLink.Encrypt(padding)));
    }

    [Fact]
    public void MotorChecksumChannelMaskAndDirectionRestrictions()
    {
        Assert.Equal("351045", Convert.ToHexString(YokonexToyProtocol.MotorInfo()));
        var frame = new OutputFrame(100, 50, 20, new(1, 20, 1, [1, .5, .25]), ChannelC: 100);
        Assert.Equal("351214050565", Convert.ToHexString(YokonexToyProtocol.MotorOutput(frame, 7)));
        Assert.Equal("351214000560", Convert.ToHexString(YokonexToyProtocol.MotorOutput(frame, 5)));
        Assert.Throws<LocalizedArgumentException>(() => YokonexToyProtocol.MotorOutput(frame with { ReferenceSample = new(1, 20, 1, [double.NaN, .5, .25]) }, 7));
        var opossum = DgLabProtocol.OpossumWave(frame);
        Assert.Equal(20, opossum.Length);
        Assert.Equal((byte)0xb0, opossum[0]);
        Assert.Equal(new byte[] { 100, 100, 100, 100 }, opossum[8..12]);
        Assert.Equal(new byte[] { 50, 50, 50, 50 }, opossum[16..20]);
    }

    [Fact]
    public void SensorFormatsKeepSignedPressureAndRawValues()
    {
        var civet = CivetProtocol.Parse([0xd0, 0, 0, 0, 0, 0, 0, 0, 0x2e, 0xfb]);
        Assert.Equal(-12.34, civet!.PressureKpa);
        var paw = new PawTelemetryTracker().Parse([0xd0, 0, 1, 1, 255, 0xff, 0x80, 0x7f, 42]);
        Assert.True(paw!.Pressed);
        Assert.Equal(-1, paw.AngleX);
        Assert.Equal(-128, paw.AngleY);
        Assert.Equal(127, paw.AngleZ);
        Assert.Equal(255, paw.Acceleration);
        Assert.Equal(42, paw.VoltageRaw);
        Assert.Null(new PawTelemetryTracker().Parse([0xd0, 0]));
        Assert.Null(new PawTelemetryTracker().Parse([0x5a, 0, 25, 3]));
        Assert.False(new PawTelemetryTracker().Parse([0x5b, 0, 1])!.EventActive);
        Assert.Equal(17, PawProtocol.Configure(new(new PawLiveData())).Length);
        Assert.Equal("5001D00100000000000000000000000000", Convert.ToHexString(PawProtocol.Configure(new(new PawLiveData()))));
        Assert.Equal("66000000000000000000000002", Convert.ToHexString(CivetProtocol.Configure(new(true))));
    }

    [Theory]
    [InlineData(BluetoothProtocol.CoyoteV2)]
    [InlineData(BluetoothProtocol.CoyoteV3)]
    [InlineData(BluetoothProtocol.Opossum)]
    [InlineData(BluetoothProtocol.Paw)]
    [InlineData(BluetoothProtocol.Civet)]
    [InlineData(BluetoothProtocol.YokonexMotor)]
    [InlineData(BluetoothProtocol.YokonexPump)]
    public async Task EachProfileInitializesReadsStatusAndClosesItsOwnLink(BluetoothProtocol protocol)
    {
        var adapter = new HardwareGattAdapter(protocol);
        await using var controller = new DeviceController(new BluetoothDeviceTransport(adapter));
        var device = Assert.Single(await controller.ScanAsync(TestContext.Current.CancellationToken));
        Assert.Equal(protocol.ToString(), device.ProtocolId);
        await controller.ConnectAsync(device, TestContext.Current.CancellationToken);
        var link = adapter.Links[device.Id];
        await Wait(() => controller.Connections[device.Id].Telemetry is not null || controller.Connections[device.Id].Device.ChannelMask is not null);
        if (device.IsInput)
        {
            await Assert.ThrowsAsync<LocalizedOperationException>(() => controller.AcquireTargetsAsync(Guid.NewGuid(), [device.Id], TestContext.Current.CancellationToken));
            if (protocol == BluetoothProtocol.Civet)
            {
                await controller.ResetInputAsync(device.Id, TestContext.Current.CancellationToken);
                Assert.Contains(link.Writes, write => write.Data[0] == 0x66);
            }
        }
        await controller.DisconnectAsync(device.Id, TestContext.Current.CancellationToken);
        Assert.True(link.Closed);
        Assert.Empty(controller.Connections);
    }

    [Fact]
    public async Task ScanUsesKnownProfilesAndRejectsOldFirmwareBeforeOpeningGatt()
    {
        var adapter = new HardwareGattAdapter(BluetoothProtocol.UnsupportedPaw);
        adapter.Peripherals.Add(new("other", "47L123unknown", -40));
        await using var transport = new BluetoothDeviceTransport(adapter);
        var device = Assert.Single(await transport.ScanAsync(TestContext.Current.CancellationToken));
        Assert.False(device.IsSupported);
        await Assert.ThrowsAsync<LocalizedOperationException>(() => transport.ConnectAsync(device, TestContext.Current.CancellationToken));
        Assert.Empty(adapter.Links);
        Assert.Null(BluetoothDeviceProfiles.Identify("unrelated device"));
        Assert.Equal(BluetoothProtocol.YokonexMotor, BluetoothDeviceProfiles.Identify("", [BluetoothDeviceProfiles.Standard(0xff40)])!.Protocol);
    }

    [Fact]
    public async Task CoyoteV3StopNeedsMatchingSequenceAndBothZeroReadings()
    {
        var adapter = new HardwareGattAdapter(BluetoothProtocol.CoyoteV3);
        await using var transport = new BluetoothDeviceTransport(adapter);
        var device = Assert.Single(await transport.ScanAsync(TestContext.Current.CancellationToken));
        await transport.ConnectAsync(device, TestContext.Current.CancellationToken);
        var link = adapter.Links[device.Id];
        link.Reply = false;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var stop = transport.SendAsync(device.Id, OutputFrame.Zero, timeout.Token);
        var sequence = link.Writes[^1].Data[1] >> 4;
        link.Notify([0xb1, (byte)(sequence % 15 + 1), 0, 0]);
        Assert.False(stop.IsCompleted);
        link.Notify([0xb1, (byte)sequence, 1, 0]);
        Assert.False(stop.IsCompleted);
        link.Notify([0xb1, (byte)sequence, 0, 0]);
        await stop;
        link.Reply = true;
    }

    private static async Task Wait(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
            await Task.Delay(10, TestContext.Current.CancellationToken);
        Assert.True(condition());
    }

    [Fact]
    public async Task FailedInitializationReleasesEveryNewLinkAndDoesNotRegisterAConnection()
    {
        var adapter = new HardwareGattAdapter(BluetoothProtocol.CoyoteV3) { FailWrites = true };
        await using var controller = new DeviceController(new BluetoothDeviceTransport(adapter));
        var device = Assert.Single(await controller.ScanAsync(TestContext.Current.CancellationToken));
        var error = await Assert.ThrowsAsync<LocalizedAggregateException>(() => controller.ConnectAsync(device, TestContext.Current.CancellationToken));
        Assert.Equal(2, error.InnerExceptions.Count);
        Assert.All(error.InnerExceptions, inner => Assert.IsType<IOException>(inner));
        Assert.Empty(controller.Connections);
        Assert.True(adapter.Links[device.Id].Closed);
    }

}

internal sealed class HardwareGattAdapter : IBluetoothAdapter
{
    public List<BluetoothPeripheral> Peripherals { get; } = [];
    public Dictionary<string, HardwareGattLink> Links { get; } = [];
    public bool FailWrites { get; set; }
    public int MaximumWriteLength { get; set; } = 20;
    public HardwareGattAdapter(params BluetoothProtocol[] protocols)
    {
        foreach (var protocol in protocols)
        {
            var name = protocol switch
            {
                BluetoothProtocol.YokonexV1 => "YYC-DJ",
                BluetoothProtocol.YokonexV2 => "YCY-DJ",
                BluetoothProtocol.CoyoteV2 => "D-LAB ESTIM01",
                BluetoothProtocol.CoyoteV3 => "47L121000",
                BluetoothProtocol.Opossum => "47L127000",
                BluetoothProtocol.Paw => "47L120300",
                BluetoothProtocol.Civet => "47L124000",
                BluetoothProtocol.UnsupportedPaw => "47L120100",
                _ => protocol.ToString()
            };
            var services = protocol == BluetoothProtocol.YokonexMotor ? new[] { BluetoothDeviceProfiles.Standard(0xff40) }
                : protocol == BluetoothProtocol.YokonexPump ? new[] { BluetoothDeviceProfiles.Standard(0xffb0) } : [];
            Peripherals.Add(new(protocol.ToString(), name, -40, services));
        }
    }
    public Task<IReadOnlyList<BluetoothPeripheral>> ScanAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<BluetoothPeripheral>>(Peripherals.ToArray());
    public Task<IBluetoothConnection> ConnectAsync(BluetoothPeripheral peripheral, CancellationToken ct)
    {
        var link = new HardwareGattLink(BluetoothDeviceProfiles.Identify(peripheral.Name, peripheral.Services)!) { FailWrites = FailWrites, MaximumWriteLength = MaximumWriteLength };
        Links[peripheral.Id] = link;
        return Task.FromResult<IBluetoothConnection>(link);
    }
}

internal sealed class HardwareGattLink(BluetoothDeviceProfile profile) : IBluetoothConnection
{
    public List<(GattAddress Address, byte[] Data)> Writes { get; } = [];
    public List<GattAddress> Reads { get; } = [];
    public int MaximumWriteLength { get; set; } = 20;
    private int _levelA, _levelB;
    public bool Closed { get; private set; }
    public bool Reply { get; set; } = true;
    public bool FailWrites { get; set; }
    public event Action<byte[]>? NotificationReceived;
    public event Action<GattNotification>? AdditionalNotificationReceived;
    public event Action? Disconnected;
    public void Notify(byte[] data) => NotificationReceived?.Invoke(data);
    public void LoseConnection() => Disconnected?.Invoke();
    public Task WriteAsync(byte[] packet, CancellationToken ct) => WriteAsync(profile.Write, packet, ct);
    public Task WriteAsync(GattAddress address, byte[] packet, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
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
            var plain = Decrypt(packet);
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
    public Task<byte[]?> ReadAsync(GattAddress address, CancellationToken ct)
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
