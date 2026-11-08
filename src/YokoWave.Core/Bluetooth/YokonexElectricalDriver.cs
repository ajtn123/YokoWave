using YokoWave.Core.Localization;

namespace YokoWave.Core.Bluetooth;

internal sealed class YokonexElectricalDriver(string id, BluetoothDeviceProfile profile, IBluetoothConnection link,
    Action<DeviceStatusUpdate> publish, Action<Exception> fault) : BluetoothProtocolDriver(id, profile, link, publish, fault)
{
    private readonly TaskCompletionSource _zero = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _version = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource<bool> _light = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _aOff, _bOff;
    private byte _hardware, _software;
    private AuxiliaryMotorMode _auxiliaryMotor;
    private bool _motionEnabled;
    private readonly BufferedFrequencyWriter _buffer = new();
    private ProgramPacketWriter? _programWriter;
    protected override DeviceCommandHandlers CreateCommandHandlers() => new DeviceCommandHandlers()
        .Add<QueryDevice>(QueryAsync)
        .Add<ConfigureLight>(async (command, ct) => { await RefreshFeatures(ct); await SetLight(command.Enabled, ct); })
        .Add<ControlStepCounter>((command, ct) => Write(V1Protocol.WithChecksum([0x35, 0x13, (byte)command.Action]), ct))
        .Add<ConfigureMotionStream>((command, ct) => SetMotion(command.Enabled, ct));

    private async Task QueryAsync(QueryDevice command, CancellationToken ct)
    {
        if (command.Query is DeviceQuery.All)
        {
            foreach (var type in new byte[] { 1, 2, 3, 4, 5, 6 }) await Write(V1Protocol.Query(type), ct);
            if (Profile.Light) await RefreshFeatures(ct);
            return;
        }
        if (command.Query is DeviceQuery.Firmware or DeviceQuery.Light)
        {
            if (!Profile.Light) throw new LocalizedOperationException("Error.DeviceFeatures");
            await RefreshFeatures(ct);
            return;
        }
        var query = command.Query switch
        {
            DeviceQuery.ChannelA => 1,
            DeviceQuery.ChannelB => 2,
            DeviceQuery.AuxiliaryMotor => 3,
            DeviceQuery.Battery => 4,
            DeviceQuery.Steps => 5,
            DeviceQuery.Motion => 6,
            _ => throw new LocalizedOperationException("Error.DeviceFeatures")
        };
        await Write(V1Protocol.Query((byte)query), ct);
    }
    public override async Task Initialize(CancellationToken ct)
    {
        await Stop(ct);
        foreach (var type in new byte[] { 4, 1, 2 }) await Write(V1Protocol.Query(type), ct);
        await Confirm(_zero.Task, ct);
    }
    public override async Task Stop(CancellationToken ct)
    {
        _programWriter?.Reset();
        _buffer.Reset();
        Exception? motorError = null;
        try
        {
            await Write(V1Protocol.AuxiliaryMotor(AuxiliaryMotorMode.Off), ct);
            _auxiliaryMotor = AuxiliaryMotorMode.Off;
        }
        catch (Exception error)
        {
            motorError = error;
        }
        try
        {
            await Write(Profile.Protocol == BluetoothProtocol.YokonexV1 ? V1Protocol.Stop() : V2Protocol.Stop(), ct);
        }
        catch (Exception error) when (motorError is not null)
        {
            throw new LocalizedAggregateException("Error.StopUnconfirmed", [motorError, error]);
        }
        if (motorError is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(motorError).Throw();
    }
    public override async Task Send(OutputFrame frame, CancellationToken ct)
    {
        var auxiliary = frame.Hardware?.AuxiliaryMotor ?? AuxiliaryMotorMode.Off;
        if (auxiliary != AuxiliaryMotorMode.Off && _motionEnabled) throw new LocalizedOperationException("Error.ModeDeviceMismatch");
        if (auxiliary != _auxiliaryMotor)
        {
            await Write(V1Protocol.AuxiliaryMotor(auxiliary), ct);
            _auxiliaryMotor = auxiliary;
        }
        IReadOnlyList<byte[]> packets = frame.NativeProgram?.Settings switch
        {
            { Kind: NativeProgramKind.ElectricalPreset } native => Profile.Protocol == BluetoothProtocol.YokonexV1 ? V1Protocol.Fixed(frame, native) : [V2Protocol.Fixed(frame, native)],
            { Kind: NativeProgramKind.ElectricalBuffered } => _buffer.Encode(frame, Link.MaximumWriteLength),
            _ => Profile.Protocol == BluetoothProtocol.YokonexV1 ? V1Protocol.Encode(frame) : [V2Protocol.Encode(frame)]
        };
        if (frame.NativeProgram?.Settings.Kind == NativeProgramKind.ElectricalPreset)
        {
            await (_programWriter ??= new(Write)).Send(packets, ct);
            return;
        }
        _programWriter?.Reset();
        for (var index = 0; index < packets.Count; index++)
        {
            if (index > 0) await Task.Delay(10, ct);
            await Write(packets[index], ct);
        }
    }
    public override void OnNotification(GattNotification notification)
    {
        if (notification.Address != Profile.Notify || V1Protocol.Parse(notification.Data) is not { } status) return;
        if (status.ErrorCode is > 0)
        {
            Publish(new(Id, telemetry: new(ProtocolErrorCode: status.ErrorCode)));
            Reject("Error.BluetoothRejected");
            return;
        }
        if (status.OutputAEnabled is false) _aOff = true;
        if (status.OutputBEnabled is false) _bOff = true;
        if (_aOff && _bOff) _zero.TrySetResult();
        if (status.HardwareVersion is > 0 && status.SoftwareVersion is > 0)
        {
            _hardware = status.HardwareVersion.Value;
            _software = status.SoftwareVersion.Value;
            _version.TrySetResult();
        }
        if (status.LightEnabled is { } light) _light.TrySetResult(light);
        Publish(new(Id, status.Battery, status.ContactA, status.ContactB, status.OutputAEnabled, status.OutputBEnabled,
            status.HardwareVersion, status.SoftwareVersion, status.LightEnabled, status.MotionAngle, status.Steps,
            telemetry: new(IntensityA: status.IntensityA, IntensityB: status.IntensityB, ModeA: status.ModeA,
                ModeB: status.ModeB, AuxiliaryMotor: status.AuxiliaryMotor,
                AccelerationAxes: status.AccelerationAxes, GyroscopeAxes: status.GyroscopeAxes)));
    }
    public override async Task SetMotion(bool enabled, CancellationToken ct)
    {
        if (enabled && _auxiliaryMotor != AuxiliaryMotorMode.Off)
        {
            await Write(V1Protocol.AuxiliaryMotor(AuxiliaryMotorMode.Off), ct);
            _auxiliaryMotor = AuxiliaryMotorMode.Off;
        }
        await Write(V1Protocol.MotionStreaming(enabled), ct);
        _motionEnabled = enabled;
    }
    public override async Task<ElectricalFeatureState> RefreshFeatures(CancellationToken ct)
    {
        if (!Profile.Light || HasFault) throw new LocalizedOperationException("Error.DeviceFeatures");
        if (!_version.Task.IsCompletedSuccessfully)
        {
            await Write(V2Protocol.QueryVersion(), ct);
            await Confirm(_version.Task, ct);
        }
        _light = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await Write(V2Protocol.QueryLight(), ct);
        try
        {
            return new(_hardware, _software, await _light.Task.WaitAsync(TimeSpan.FromSeconds(2), ct));
        }
        catch (TimeoutException error)
        {
            throw new LocalizedIOException("Error.LightTimeout", error);
        }
    }
    public override async Task<bool> SetLight(bool enabled, CancellationToken ct)
    {
        if (!Profile.Light || !_version.Task.IsCompletedSuccessfully || HasFault) throw new LocalizedOperationException("Error.DeviceFeatures");
        _light = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await Write(V2Protocol.SetLight(enabled), ct);
        await Write(V2Protocol.QueryLight(), ct);
        try
        {
            var actual = await _light.Task.WaitAsync(TimeSpan.FromSeconds(2), ct);
            if (actual != enabled) throw new LocalizedIOException("Error.LightTimeout");
            return actual;
        }
        catch (TimeoutException error)
        {
            throw new LocalizedIOException("Error.LightTimeout", error);
        }
    }
}
