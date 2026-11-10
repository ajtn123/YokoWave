using System.Buffers.Binary;
using YokoWave.Core;
using YokoWave.Core.Bluetooth;
using YokoWave.Core.Localization;
using YokoWave.Tests.Support.Bluetooth;

namespace YokoWave.Tests.Bluetooth;

public sealed class BluetoothConnectionTests
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
        Assert.Equal(BluetoothProtocol.YokonexV2, BluetoothDeviceProfiles.Identify("YCY-DJ")!.Protocol);
        Assert.Equal(BluetoothProtocol.YokonexV1, BluetoothDeviceProfiles.Identify("YYC-DJ")!.Protocol);
        Assert.Null(BluetoothDeviceProfiles.Identify("YCY-FJB-01"));
        Assert.Equal("35110200000100000001004A", Convert.ToHexString(V2Protocol.Stop()));
        var output = new OutputFrame(100, 50, 20, new(1, 98, .18), new(1, 40, .8));
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
        await using var controller = new DeviceController(new BluetoothDeviceTransport(adapter));
        var device = Assert.Single(await controller.ScanAsync(TestContext.Current.CancellationToken));
        Assert.Null(device.Battery);
        await controller.ConnectAsync(device, TestContext.Current.CancellationToken);
        var link = adapter.Links[device.Id];
        Assert.Equal(V2Protocol.Stop(), link.Writes[0]);
        Assert.DoesNotContain(link.Writes, packet => packet[1] == 0x12);
        Assert.All(link.Writes.Where(p => p[1] == 0x11), packet => Assert.Equal(V2Protocol.Stop(), packet));
        await AsyncAssert.EventuallyAsync(() => controller.Connections[device.Id].Device.Battery == 82);
        Assert.Equal(ElectrodeContact.NotConnected, controller.Connections[device.Id].ContactA);
        await controller.SendAsync(device.Id, new(90, 90, 20), TestContext.Current.CancellationToken);
        Assert.Equal(248, BinaryPrimitives.ReadUInt16BigEndian(link.Writes[^1].AsSpan(3, 2)));
        await controller.DisconnectAsync(device.Id, TestContext.Current.CancellationToken);
        Assert.True(link.Closed);
        Assert.Equal(V2Protocol.Stop(), link.Writes[^1]);
    }

    [Fact]
    public async Task MissingOutputOffConfirmationCancelsAndClosesConnection()
    {
        var adapter = new FakeBluetoothAdapter
        {
            ReplyToQueries = false
        };
        await using var transport = new BluetoothDeviceTransport(adapter);
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
        var adapter = new FakeBluetoothAdapter
        {
            FailFirstWrite = true
        };
        await using var controller = new DeviceController(new BluetoothDeviceTransport(adapter));
        var device = Assert.Single(await controller.ScanAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<IOException>(() => controller.ConnectAsync(device, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty(controller.Connections);
        Assert.True(adapter.Links[device.Id].Closed);
    }

    [Fact]
    public async Task ElectricalConnectionAndZeroOutputDoNotRequireAnUnusedAuxiliaryMotorCommand()
    {
        var adapter = new FakeBluetoothAdapter { Rejection = packet => packet[1] == 0x12 ? (byte)3 : null };
        await using var controller = new DeviceController(new BluetoothDeviceTransport(adapter));
        var device = Assert.Single(await controller.ScanAsync(TestContext.Current.CancellationToken));
        await controller.ConnectAsync(device, TestContext.Current.CancellationToken);
        await controller.SendAsync(device.Id, new(0, 0, 20), TestContext.Current.CancellationToken);
        await controller.DisconnectAsync(device.Id, TestContext.Current.CancellationToken);

        var link = adapter.Links[device.Id];
        Assert.True(link.Closed);
        Assert.DoesNotContain(link.Writes, packet => packet[1] == 0x12);
        Assert.All(link.Writes.Where(packet => packet[1] == 0x11), packet => Assert.Equal(V2Protocol.Stop(), packet));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EnabledAuxiliaryMotorIsStoppedBeforeElectricalOutputEvenWhenItsStopWriteFails(bool reported)
    {
        var adapter = new FakeBluetoothAdapter();
        await using var transport = new BluetoothDeviceTransport(adapter);
        var device = Assert.Single(await transport.ScanAsync(TestContext.Current.CancellationToken));
        await transport.ConnectAsync(device, TestContext.Current.CancellationToken);
        var link = adapter.Links[device.Id];
        if (reported)
        {
            link.Notify(V1Protocol.WithChecksum([0x35, 0x71, 3, (byte)AuxiliaryMotorMode.On]));
        }
        else
        {
            await transport.SendAsync(device.Id, new(0, 0, 20, Hardware: new(AuxiliaryMotor: AuxiliaryMotorMode.On)), TestContext.Current.CancellationToken);
            link.Notify(V1Protocol.WithChecksum([0x35, 0x71, 3, (byte)AuxiliaryMotorMode.Off]));
        }
        link.FailNextWrite = true;
        int before = link.Writes.Count;

        await Assert.ThrowsAsync<IOException>(() => transport.SendAsync(device.Id, OutputFrame.Zero, TestContext.Current.CancellationToken));

        Assert.Equal(V2Protocol.Stop(), link.Writes[before]);
        await transport.SendAsync(device.Id, OutputFrame.Zero, TestContext.Current.CancellationToken);
        Assert.Equal(V1Protocol.AuxiliaryMotor(AuxiliaryMotorMode.Off), link.Writes[^2]);
        Assert.Equal(V2Protocol.Stop(), link.Writes[^1]);
    }

    [Theory]
    [InlineData(0x11)]
    [InlineData(0x71)]
    public async Task InitializationRejectionClosesTheLinkPreservesTheCodeAndAllowsFreshReconnection(int command)
    {
        bool reject = true;
        var adapter = new FakeBluetoothAdapter { Rejection = packet => reject && packet[1] == command ? (byte)3 : null };
        await using var controller = new DeviceController(new BluetoothDeviceTransport(adapter));
        var device = Assert.Single(await controller.ScanAsync(TestContext.Current.CancellationToken));

        var error = await Assert.ThrowsAsync<LocalizedOperationException>(() => controller.ConnectAsync(device, TestContext.Current.CancellationToken));

        Assert.Equal("Error.BluetoothRejected", error.LocalizedMessage.Key);
        Assert.Equal((byte)3, error.LocalizedMessage.Arguments[0]);
        Assert.Contains(L10n.Get("ProtocolError.3"), error.Message);
        Assert.Empty(controller.Connections);
        var failed = adapter.Links[device.Id];
        Assert.True(failed.Closed);
        reject = false;
        await controller.ConnectAsync(device, TestContext.Current.CancellationToken);
        Assert.NotSame(failed, adapter.Links[device.Id]);
        Assert.Single(controller.Connections);
        await controller.SendAsync(device.Id, new(20, 0, 20), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RejectionWhileAwaitingInitialStatusReportsTheDeviceErrorInsteadOfTimingOut()
    {
        var adapter = new FakeBluetoothAdapter { ReplyToQueries = false };
        await using var transport = new BluetoothDeviceTransport(adapter);
        var device = Assert.Single(await transport.ScanAsync(TestContext.Current.CancellationToken));
        var connect = transport.ConnectAsync(device, TestContext.Current.CancellationToken);
        await AsyncAssert.EventuallyAsync(() => adapter.Links.TryGetValue(device.Id, out var link) &&
            link.Writes.Any(packet => packet.SequenceEqual(V1Protocol.Query(2))));
        adapter.Links[device.Id].Notify(V1Protocol.WithChecksum([0x35, 0x71, 0x55, 4]));

        var error = await Assert.ThrowsAsync<LocalizedOperationException>(() => connect.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));

        Assert.Equal((byte)4, error.LocalizedMessage.Arguments[0]);
        Assert.True(adapter.Links[device.Id].Closed);
    }

    [Fact]
    public async Task RemoteRejectionBlocksFurtherOutputButStillAllowsStop()
    {
        var adapter = new FakeBluetoothAdapter();
        await using var transport = new BluetoothDeviceTransport(adapter);
        var device = Assert.Single(await transport.ScanAsync(TestContext.Current.CancellationToken));
        await transport.ConnectAsync(device, TestContext.Current.CancellationToken);
        var link = adapter.Links[device.Id];
        int faultCount = 0;
        transport.Faulted += (_, _) => faultCount++;
        link.Notify(V1Protocol.WithChecksum([0x35, 0x71, 0x55, 4]));
        link.Notify(V1Protocol.WithChecksum([0x35, 0x71, 0x55, 4]));
        Assert.Equal(1, faultCount);
        int writes = link.Writes.Count;
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => transport.SendAsync(device.Id, new(20, 20, 20), TestContext.Current.CancellationToken));
        Assert.Equal(writes, link.Writes.Count);
        await transport.SendAsync(device.Id, OutputFrame.Zero, TestContext.Current.CancellationToken);
        Assert.Equal(V2Protocol.Stop(), link.Writes[^1]);
    }

    [Fact]
    public async Task FailedStopDuringDisconnectStillRemovesConnectionAndClosesLink()
    {
        var adapter = new FakeBluetoothAdapter();
        await using var controller = new DeviceController(new BluetoothDeviceTransport(adapter));
        var device = Assert.Single(await controller.ScanAsync(TestContext.Current.CancellationToken));
        await controller.ConnectAsync(device, cancellationToken: TestContext.Current.CancellationToken);
        var link = adapter.Links[device.Id];
        link.FailWrites = true;
        var error = await Assert.ThrowsAsync<YokoWave.Core.Localization.LocalizedAggregateException>(() => controller.DisconnectAsync(device.Id, TestContext.Current.CancellationToken));
        Assert.Equal(2, error.InnerExceptions.Count);
        Assert.All(error.Flatten().InnerExceptions, inner => Assert.IsType<IOException>(inner));
        Assert.Empty(controller.Connections);
        Assert.True(link.Closed);
    }

    [Fact]
    public async Task DisconnectPreservesShutdownAndNativeCleanupFailures()
    {
        var adapter = new FakeBluetoothAdapter();
        await using var transport = new BluetoothDeviceTransport(adapter);
        var device = Assert.Single(await transport.ScanAsync(TestContext.Current.CancellationToken));
        await transport.ConnectAsync(device, TestContext.Current.CancellationToken);
        var link = adapter.Links[device.Id];
        link.FailWrites = true;
        link.FailDispose = true;
        var error = await Assert.ThrowsAsync<YokoWave.Core.Localization.LocalizedAggregateException>(() => transport.DisconnectAsync(device.Id, TestContext.Current.CancellationToken));
        Assert.Contains(error.Flatten().InnerExceptions, inner => inner.Message == "Injected disconnected radio");
        Assert.Contains(error.Flatten().InnerExceptions, inner => inner.Message == "Injected native cleanup failure");
        Assert.True(link.Closed);
        await transport.DisconnectAsync(device.Id, TestContext.Current.CancellationToken);
    }
}
