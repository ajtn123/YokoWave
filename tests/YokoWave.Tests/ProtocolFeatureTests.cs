using Xunit;
using YokoWave.Core;
using YokoWave.Core.Bluetooth;
using YokoWave.Core.Localization;

namespace YokoWave.Tests;

public sealed class ProtocolFeatureTests
{
    private static WaveformAsset Wave => new(Guid.NewGuid(), "Constant", WaveShape.Custom, 10, 20, [1, 1]);

    [Fact]
    public void PawTriggerVectorsCoverAllLayoutsAndBoundaryFields()
    {
        Assert.Equal("5001000000000000000000000000000000", Convert.ToHexString(PawProtocol.Configure(new(new PawNoTrigger()))));
        Assert.Equal("5001D00100000000000000000000000000", Convert.ToHexString(PawProtocol.Configure(new(new PawLiveData()))));
        byte[] random = PawProtocol.Configure(new(new PawRandomReaction(24, 1, 65535, 256, new(120, 42, 119, 255)), 6));
        Assert.Equal("500603180001FFFF0100782A77FF000000", Convert.ToHexString(random));
        var probability = new PawProbability([new(1, 150), new(24, 100)], 65535);
        Assert.Equal(new[] { 150, 50 }, probability.EffectiveEvents().Select(entry => entry.Probability));
        Assert.Equal("500104019618640000000000000000FFFF", Convert.ToHexString(PawProtocol.Configure(new(probability))));
        Assert.Equal("50010F1801FEFF80000000000000000000", Convert.ToHexString(PawProtocol.Configure(new(new PawVoltage(24, true, 254, 255, 128)))));
        var motion = new PawMotionTrigger(24, true, TriggerDebounce: 7, CancelDebounce: 6, MappingRange: 255,
            X: new(-128, 127), Y: new(-20, 20), Z: new(0, 0));
        Assert.Equal("50010501FE0102030418807FEC140000FF", Convert.ToHexString(PawProtocol.Configure(new(new PawButtonMotion(1, true, new(1, 2, 3, 4), motion)))));
        byte[] acceleration = PawProtocol.Configure(new(new PawButtonMotion(0, Motion: new(2, AccelerationAbove: false, AccelerationThreshold: 400))));
        Assert.Equal(new byte[] { 0, 1, 144, 0, 0, 0 }, acceleration[10..16]);
        Assert.Throws<LocalizedArgumentException>(() => PawProtocol.Configure(new(new PawProbability([new(1, 100), new(1, 20)]))));
        Assert.Throws<LocalizedArgumentException>(() => PawProtocol.Configure(new(new PawButtonMotion(0))));
        Assert.Throws<LocalizedArgumentException>(() => PawProtocol.Configure(new(new PawVoltage(MinimumRaw: 200, MaximumRaw: 100))));
    }

    [Fact]
    public void PawCalibrationAndShoulderLightUseSignedFieldsAndPublicFormats()
    {
        byte[] data = Convert.FromHexString("F161FF0000FEFFD8002800000000");
        var calibration = Assert.IsType<PawCalibration>(PawProtocol.ParseCalibration(data));
        Assert.Equal(new PawAxisRange(-128, 127), calibration.X);
        Assert.Equal(new PawAxisRange(-20, 20), calibration.Y);
        Assert.Null(PawProtocol.ParseCalibration(data[..^1]));
        Assert.Equal(new byte[] { 0x70, 7 }, PawProtocol.ShoulderLight(new(7)));
        Assert.Equal(new byte[] { 0x70, 2, 3, 2 }, PawProtocol.ShoulderLight(new(2, 3, ShoulderBlink.Fast)));
        Assert.Equal(2.1, PawProtocol.Voltage(255));
        Assert.Equal(.07, PawProtocol.Voltage(8), 8);
    }

    [Fact]
    public void FullElectricalStatusIncludesRawAxesProgramAndAuxiliaryMotor()
    {
        var a = V1Protocol.Parse(V1Protocol.WithChecksum([0x35, 0x71, 1, 2, 1, 1, 20, 16]))!;
        Assert.Equal(276, a.IntensityA);
        Assert.Equal(16, a.ModeA);
        Assert.Equal(AuxiliaryMotorMode.Pattern3, V1Protocol.Parse(V1Protocol.WithChecksum([0x35, 0x71, 3, 0x13]))!.AuxiliaryMotor);
        var imu = V1Protocol.Parse(V1Protocol.WithChecksum([0x35, 0x71, 6, 0xff, 0xff, 0, 2, 0, 3, 0x80, 0, 0x7f, 0xff, 0, 4]))!;
        Assert.Equal(new SensorAxes(-1, 2, 3), imu.AccelerationAxes);
        Assert.Equal(new SensorAxes(-32768, 32767, 4), imu.GyroscopeAxes);
        Assert.Null(V1Protocol.Parse(V1Protocol.WithChecksum([0x35, 0x71, 3, 7])));
    }

    [Fact]
    public async Task FixedMotorProgramsValidateReportedCountsAndNeverPretendToLimitIntensity()
    {
        var adapter = new HardwareGattAdapter(BluetoothProtocol.YokonexMotor);
        await using var transport = new BluetoothDeviceTransport(adapter);
        var device = Assert.Single(await transport.ScanAsync(TestContext.Current.CancellationToken));
        await transport.ConnectAsync(device, TestContext.Current.CancellationToken);
        var native = new NativeProgramOutput(new(NativeProgramKind.MotorPreset, PresetA: 5, PresetB: 5), 0, null);
        var frame = new OutputFrame(100, 100, 20, NativeProgram: native);
        await transport.SendAsync(device.Id, frame, TestContext.Current.CancellationToken);
        var link = adapter.Links[device.Id];
        Assert.Contains(link.Writes, entry => entry.Data.SequenceEqual(V1Protocol.WithChecksum([0x35, 0x11, 3, 5])));
        int count = link.Writes.Count;
        await transport.SendAsync(device.Id, frame, TestContext.Current.CancellationToken);
        Assert.Equal(count, link.Writes.Count);
        await Assert.ThrowsAsync<LocalizedArgumentException>(() => transport.SendAsync(device.Id, frame with { OutputLimit = 60 }, cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<LocalizedArgumentException>(() => transport.SendAsync(device.Id, frame with { NativeProgram = native with { Settings = native.Settings with { PresetA = 6 } } }, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(20)]
    [InlineData(24)]
    public async Task OpossumSendsCompleteDisplayPacketOnlyWhenItFits(int payload)
    {
        var adapter = new HardwareGattAdapter(BluetoothProtocol.Opossum) { MaximumWriteLength = payload };
        await using var controller = new DeviceController(new BluetoothDeviceTransport(adapter));
        var device = await Connect(controller);
        var link = adapter.Links[device.Id];
        if (payload < 24)
        {
            await Assert.ThrowsAsync<LocalizedOperationException>(() => controller.ExecuteCommandAsync(device.Id, new SynchronizeDisplay(), cancellationToken: TestContext.Current.CancellationToken));
            Assert.DoesNotContain(link.Writes, entry => entry.Data[0] == 0xb2);
        }
        else
        {
            link.Notify([0xb3, 80, 120]);
            await controller.ExecuteCommandAsync(device.Id, new SynchronizeDisplay(), cancellationToken: TestContext.Current.CancellationToken);
            byte[] packet = link.Writes.Last(entry => entry.Data[0] == 0xb2).Data;
            Assert.Equal(24, packet.Length);
            Assert.Equal(new byte[] { 80, 120 }, packet[^2..]);
        }
        Assert.Equal(new byte[] { 0xb3, 255, 200 }, OpossumProtocol.Strength(null, 200));
    }

    [Fact]
    public async Task PawEventsRemainIndependentAndCalibrationCanBeReadWithoutInventingState()
    {
        var adapter = new HardwareGattAdapter(BluetoothProtocol.Paw);
        await using var controller = new DeviceController(new BluetoothDeviceTransport(adapter));
        var device = await Connect(controller);
        var link = adapter.Links[device.Id];
        link.Notify([0x5a, 1, 1, 80]);
        link.Notify([0x5a, 1, 2, 40]);
        link.Notify([0x5b, 1, 1]);
        link.Notify([0x5c, 1, 3, 22]);
        link.Notify(Convert.FromHexString("F161FF0000FEFFD8002800000000"));
        for (int i = 0; i < 100 && controller.Connections[device.Id].Telemetry?.Calibration is null; i++)
            await Task.Delay(10, TestContext.Current.CancellationToken);
        var data = controller.Connections[device.Id].Telemetry!;
        Assert.Contains(data.PawEvents!, entry => entry is { EventId: 1, Active: false, Parameter: 80 });
        Assert.Contains(data.PawEvents!, entry => entry is { EventId: 2, Active: true, Parameter: 40 });
        Assert.Contains(data.PawEvents!, entry => entry is { EventId: 3, Active: null, Parameter: 22 });
        Assert.NotNull(data.Calibration);
        await controller.ExecuteCommandAsync(device.Id, new ConfigurePaw(new PawVoltage()), cancellationToken: TestContext.Current.CancellationToken);
        await controller.ExecuteCommandAsync(device.Id, new ConfigureShoulderLight(2, 3, ShoulderBlink.Slow), cancellationToken: TestContext.Current.CancellationToken);
        await controller.ExecuteCommandAsync(device.Id, new ResetPawParameters(), cancellationToken: TestContext.Current.CancellationToken);
        await controller.ExecuteCommandAsync(device.Id, new CalibratePawAngles(), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(new byte[] { 0x60 }, link.Writes[^1].Data);
    }

    [Fact]
    public async Task CivetCanFlipAndZeroInOneCommandAndOtherProtocolsRejectIt()
    {
        Assert.Equal("66000000000000000000030002", Convert.ToHexString(CivetProtocol.Configure(new(true, SensorOrientation.Flipped))));
        var adapter = new HardwareGattAdapter(BluetoothProtocol.Civet);
        await using var controller = new DeviceController(new BluetoothDeviceTransport(adapter));
        var device = await Connect(controller);
        await controller.ExecuteCommandAsync(device.Id, new ConfigurePressureSensor(true, SensorOrientation.Flipped), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(13, adapter.Links[device.Id].Writes[^1].Data.Length);
        await Assert.ThrowsAsync<LocalizedOperationException>(() => controller.ExecuteCommandAsync(device.Id, new ControlStepCounter(StepCounterAction.Reset), cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void PumpWireTimerRetainsTheFullUnsignedRange()
    {
        byte[] packet = YokonexToyProtocol.PumpCommand(1, PumpDirection.Reverse, seconds: 65535);
        byte[] plain = HardwareGattLink.Decrypt(packet);
        Assert.Equal(new byte[] { 0xbf, 0x0f, 0xa0, 1, 2, 255, 255 }, plain[..7]);
        Assert.Throws<LocalizedArgumentException>(() => new PumpOutput(PeristalticSeconds: -1).Validate());
    }

    [Fact]
    public async Task ElectricalCommandsCoverAllCounterActionsAndEnforceMotionAndFaultBoundaries()
    {
        var adapter = new HardwareGattAdapter(BluetoothProtocol.YokonexV1);
        await using var transport = new BluetoothDeviceTransport(adapter);
        var device = Assert.Single(await transport.ScanAsync(TestContext.Current.CancellationToken));
        await transport.ConnectAsync(device, TestContext.Current.CancellationToken);
        var link = adapter.Links[device.Id];
        foreach (var action in Enum.GetValues<StepCounterAction>())
        {
            await transport.ExecuteCommandAsync(device.Id, new ControlStepCounter(action), TestContext.Current.CancellationToken);
            Assert.Equal(new byte[] { 0x35, 0x13, (byte)action, (byte)(0x48 + (int)action) }, link.Writes[^1].Data);
        }
        await transport.ExecuteCommandAsync(device.Id, new QueryDevice(), TestContext.Current.CancellationToken);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, link.Writes.TakeLast(6).Select(entry => entry.Data[2]));
        await transport.ExecuteCommandAsync(device.Id, new ConfigureMotionStream(true), TestContext.Current.CancellationToken);
        var auxiliary = new OutputFrame(0, 0, 20, Hardware: new(AuxiliaryMotor: AuxiliaryMotorMode.On));
        await Assert.ThrowsAsync<LocalizedOperationException>(() => transport.SendAsync(device.Id, auxiliary, TestContext.Current.CancellationToken));
        await transport.ExecuteCommandAsync(device.Id, new ConfigureMotionStream(false), TestContext.Current.CancellationToken);
        link.Notify(V1Protocol.WithChecksum([0x35, 0x71, 0x55, 4]));
        int count = link.Writes.Count;
        await Assert.ThrowsAsync<LocalizedOperationException>(() => transport.SendAsync(device.Id, auxiliary, TestContext.Current.CancellationToken));
        Assert.Equal(count, link.Writes.Count);
    }

    [Fact]
    public async Task V3RelativeUpdatesWaitForTheirOwnSequenceBeforeAnotherWrite()
    {
        var adapter = new HardwareGattAdapter(BluetoothProtocol.CoyoteV3);
        await using var transport = new BluetoothDeviceTransport(adapter);
        var device = Assert.Single(await transport.ScanAsync(TestContext.Current.CancellationToken));
        await transport.ConnectAsync(device, TestContext.Current.CancellationToken);
        var link = adapter.Links[device.Id];
        link.Reply = false;
        var frame = new OutputFrame(20, 20, 20, Hardware: new(RelativeStrength: true));
        var first = transport.SendAsync(device.Id, frame, TestContext.Current.CancellationToken);
        byte sequence = (byte)(link.Writes[^1].Data[1] >> 4);
        int count = link.Writes.Count;
        var second = transport.SendAsync(device.Id, frame, TestContext.Current.CancellationToken);
        link.Notify([0xb1, 0, 10, 10]);
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        Assert.Equal(count, link.Writes.Count);
        link.Notify([0xb1, sequence, 40, 40]);
        await Task.WhenAll(first, second);
        Assert.Equal(0, link.Writes[^1].Data[1] & 15);
        link.Reply = true;
    }
    private static async Task<DeviceDescriptor> Connect(DeviceController controller)
    {
        var device = Assert.Single(await controller.ScanAsync(TestContext.Current.CancellationToken));
        await controller.ConnectAsync(device, TestContext.Current.CancellationToken);
        return device;
    }
}
