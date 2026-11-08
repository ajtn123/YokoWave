using System.Buffers.Binary;
using YokoWave.Core;
using YokoWave.Core.Bluetooth;
using Xunit;

namespace YokoWave.Tests;

public sealed class BluetoothTests
{
    [Fact]
    public void PublishedV1FramesHaveCorrectFieldsByteOrderAndChecksum()
    {
        Assert.Equal("35110300000000000049", Convert.ToHexString(V1Protocol.Stop()));
        Assert.Equal("357104AA", Convert.ToHexString(V1Protocol.Query(4)));
        Assert.Equal("351103010114111464E8", Convert.ToHexString(Assert.Single(V1Protocol.Encode(new(100, 100, 20)))));
        Assert.Equal(V1Protocol.Stop(), Assert.Single(V1Protocol.Encode(new(double.NaN, double.NegativeInfinity, 20))));
        var limited = V1Protocol.Encode(new(1000, -1, 20));
        Assert.Equal(276, BinaryPrimitives.ReadUInt16BigEndian(limited[0].AsSpan(4, 2)));
        Assert.Equal(0, limited[1][3]);
    }

    [Fact]
    public void YcyDjUsesV2CombinedFrameWithSeparateFrequencyAndPulseWidth()
    {
        Assert.Equal(ElectricalGeneration.CombinedV2, ElectricalDeviceProfile.FromName("YCY-DJ")!.Generation);
        Assert.Equal(ElectricalGeneration.LegacyV1, ElectricalDeviceProfile.FromName("YYC-DJ")!.Generation);
        Assert.Null(ElectricalDeviceProfile.FromName("YCY-FJB-01"));
        Assert.Equal("35110200000100000001004A", Convert.ToHexString(V2Protocol.Stop()));
        var output = new OutputFrame(100, 50, 20, new(0.18, 98, 18), new(0.8, 40, 80));
        Assert.Equal("35110201146212008A2850D3", Convert.ToHexString(V2Protocol.Encode(output)));
        Assert.ThrowsAny<ArgumentException>(() => V2Protocol.Encode(output with { ReferenceSample = new(0.5, 0, 0, [1, 2, 3]) }));
    }

    [Fact]
    public void NotificationParserRejectsCorruptTruncatedAndOutOfRangeReadings()
    {
        Assert.Equal(82, V1Protocol.Parse(Convert.FromHexString("35710452FC"))!.Battery);
        var contact = V1Protocol.Parse(V1Protocol.WithChecksum([0x35, 0x71, 0x01, 0x02, 0x00, 0, 0, 0]));
        Assert.Equal(ElectrodeContact.Connected, contact!.ContactA);
        Assert.False(contact.OutputAEnabled);
        Assert.Null(V1Protocol.Parse(Convert.FromHexString("35710452FD")));
        Assert.Null(V1Protocol.Parse(Convert.FromHexString("35710452")));
        Assert.Null(V1Protocol.Parse(V1Protocol.WithChecksum([0x35, 0x71, 4, 101])));
        Assert.Equal((byte?)3, V1Protocol.Parse(V1Protocol.WithChecksum([0x35, 0x71, 0x55, 3]))!.ErrorCode);
    }

    [Fact]
    public async Task ConnectConfirmsBothOutputsOffAndReportsRealBattery()
    {
        var adapter = new FakeBluetoothAdapter();
        await using var controller = new DeviceController(new ElectricalBluetoothTransport(adapter));
        var device = Assert.Single(await controller.ScanAsync(TestContext.Current.CancellationToken));
        Assert.Null(device.Battery);
        await controller.ConnectAsync(device, 40, TestContext.Current.CancellationToken);
        var link = adapter.Links[device.Id];
        Assert.Equal(V2Protocol.Stop(), link.Writes[0]);
        Assert.All(link.Writes.Where(p => p[1] == 0x11), packet => Assert.Equal(V2Protocol.Stop(), packet));
        await WaitAsync(() => controller.Connections[device.Id].Device.Battery == 82);
        Assert.Equal(ElectrodeContact.NotConnected, controller.Connections[device.Id].ContactA);
        await controller.SendAsync(device.Id, new(90, 90, 20), TestContext.Current.CancellationToken);
        Assert.Equal(110, BinaryPrimitives.ReadUInt16BigEndian(link.Writes[^1].AsSpan(3, 2)));
        await controller.DisconnectAsync(device.Id, TestContext.Current.CancellationToken);
        Assert.True(link.Closed);
        Assert.Equal(V2Protocol.Stop(), link.Writes[^1]);
    }

    [Fact]
    public async Task MissingOutputOffConfirmationCancelsAndClosesConnection()
    {
        var adapter = new FakeBluetoothAdapter { ReplyToQueries = false };
        await using var transport = new ElectricalBluetoothTransport(adapter);
        var device = Assert.Single(await transport.ScanAsync(TestContext.Current.CancellationToken));
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(80));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transport.ConnectAsync(device, timeout.Token));
        var link = adapter.Links[device.Id];
        Assert.True(link.Closed);
        Assert.All(link.Writes.Where(p => p[1] == 0x11), packet => Assert.Equal(V2Protocol.Stop(), packet));
    }

    [Fact]
    public async Task InitialWriteFailureReleasesNativeConnection()
    {
        var adapter = new FakeBluetoothAdapter { FailFirstWrite = true };
        await using var controller = new DeviceController(new ElectricalBluetoothTransport(adapter));
        var device = Assert.Single(await controller.ScanAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<IOException>(() => controller.ConnectAsync(device, ct: TestContext.Current.CancellationToken));
        Assert.Empty(controller.Connections);
        Assert.True(adapter.Links[device.Id].Closed);
    }

    [Fact]
    public async Task RemoteRejectionBlocksFurtherOutputButStillAllowsStop()
    {
        var adapter = new FakeBluetoothAdapter();
        await using var transport = new ElectricalBluetoothTransport(adapter);
        var device = Assert.Single(await transport.ScanAsync(TestContext.Current.CancellationToken));
        await transport.ConnectAsync(device, TestContext.Current.CancellationToken);
        var link = adapter.Links[device.Id];
        var faultCount = 0;
        transport.Faulted += (_, _) => faultCount++;
        link.Notify(V1Protocol.WithChecksum([0x35, 0x71, 0x55, 4]));
        link.Notify(V1Protocol.WithChecksum([0x35, 0x71, 0x55, 4]));
        Assert.Equal(1, faultCount);
        var writes = link.Writes.Count;
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => transport.SendAsync(device.Id, new(20, 20, 20), TestContext.Current.CancellationToken));
        Assert.Equal(writes, link.Writes.Count);
        await transport.SendAsync(device.Id, OutputFrame.Zero, TestContext.Current.CancellationToken);
        Assert.Equal(V2Protocol.Stop(), link.Writes[^1]);
    }

    [Fact]
    public async Task FailedStopDuringDisconnectStillRemovesConnectionAndClosesLink()
    {
        var adapter = new FakeBluetoothAdapter();
        await using var controller = new DeviceController(new ElectricalBluetoothTransport(adapter));
        var device = Assert.Single(await controller.ScanAsync(TestContext.Current.CancellationToken));
        await controller.ConnectAsync(device, ct: TestContext.Current.CancellationToken);
        var link = adapter.Links[device.Id];
        link.FailWrites = true;
        await Assert.ThrowsAnyAsync<IOException>(() => controller.DisconnectAsync(device.Id, TestContext.Current.CancellationToken));
        Assert.Empty(controller.Connections);
        Assert.True(link.Closed);
    }

    [Fact]
    public async Task ElectricalSamplingKeepsIntensitySeparateFromPulseWidth()
    {
        var adapter = new FakeBluetoothAdapter();
        await using var controller = new DeviceController(new ElectricalBluetoothTransport(adapter));
        var device = Assert.Single(await controller.ScanAsync(TestContext.Current.CancellationToken));
        await controller.ConnectAsync(device, ct: TestContext.Current.CancellationToken);
        var engine = new SessionEngine(controller) { ChannelA = 30, ChannelB = 20 };
        var preset = DefaultWaveformCatalog.Presets[0];
        await engine.StartAsync([device.Id], preset, TimeSpan.FromSeconds(10));
        await engine.AdvanceAsync(TimeSpan.FromSeconds(0.1));
        Assert.Equal(30, controller.Connections[device.Id].Output.ChannelA);
        Assert.Equal(18, adapter.Links[device.Id].Writes[^1][6]);
        await engine.StopAsync();
        await Assert.ThrowsAnyAsync<ArgumentException>(() => engine.StartAsync([device.Id],
            DefaultWaveformCatalog.Presets.First(p => p.Family == WaveformFamily.Motor), TimeSpan.FromSeconds(10)));
        Assert.Equal(V2Protocol.Stop(), adapter.Links[device.Id].Writes[^1]);
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        for (var index = 0; index < 100 && !condition(); index++) await Task.Delay(10, TestContext.Current.CancellationToken);
        Assert.True(condition());
    }
}

internal sealed class FakeBluetoothAdapter : IBluetoothAdapter
{
    public bool ReplyToQueries { get; init; } = true;
    public bool FailFirstWrite { get; init; }
    public List<BluetoothPeripheral> Devices { get; } = [new("ble:000000000001", "YCY-DJ", -50)];
    public Dictionary<string, FakeBluetoothConnection> Links { get; } = [];
    public Task<IReadOnlyList<BluetoothPeripheral>> ScanAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<BluetoothPeripheral>>(Devices.ToArray());
    public Task<IBluetoothConnection> ConnectAsync(BluetoothPeripheral peripheral, CancellationToken ct)
    {
        var link = new FakeBluetoothConnection { ReplyToQueries = ReplyToQueries, FailNextWrite = FailFirstWrite };
        Links[peripheral.Id] = link;
        return Task.FromResult<IBluetoothConnection>(link);
    }
}

internal sealed class FakeBluetoothConnection : IBluetoothConnection
{
    public bool ReplyToFeatures { get; set; } = true;
    public bool LightEnabled { get; private set; } = true;
    public bool ReplyToQueries { get; init; }
    public bool FailNextWrite { get; set; }
    public bool FailWrites { get; set; }
    public bool Closed { get; private set; }
    public List<byte[]> Writes { get; } = [];
    public event Action<byte[]>? NotificationReceived;
    public event Action? Disconnected;
    public void Notify(byte[] packet) => NotificationReceived?.Invoke(packet);
    public void LoseConnection() => Disconnected?.Invoke();
    public Task WriteAsync(byte[] packet, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (FailWrites) throw new IOException("Injected disconnected radio");
        if (FailNextWrite) { FailNextWrite = false; throw new IOException("Injected write failure"); }
        Writes.Add(packet.ToArray());
        if (ReplyToFeatures && packet.SequenceEqual(V2Protocol.QueryVersion())) Notify(V1Protocol.WithChecksum([0x36, 0x13, 1, 2]));
        if (ReplyToFeatures && packet[1] == 0x15) LightEnabled = packet[2] == 1;
        if (ReplyToQueries && packet[1] == 0x71)
        {
            if (packet[2] == 4) Notify(V1Protocol.WithChecksum([0x35, 0x71, 4, 82]));
            else if (packet[2] is 1 or 2) Notify(V1Protocol.WithChecksum([0x35, 0x71, packet[2], 0, 0, 0, 0, 0]));
            else if (packet[2] == 7 && ReplyToFeatures) Notify(V1Protocol.WithChecksum([0x35, 0x71, 7, (byte)(LightEnabled ? 1 : 0)]));
        }
        return Task.CompletedTask;
    }
    public ValueTask DisposeAsync() { Closed = true; return ValueTask.CompletedTask; }
}
