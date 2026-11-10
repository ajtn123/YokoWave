using YokoWave.Core.Localization;

namespace YokoWave.Core.Bluetooth;

internal sealed class YokonexElectricalDriver(string id, BluetoothDeviceProfile profile, IBluetoothConnection link,
    Action<DeviceStatusUpdate> publish, Action<Exception> fault) : BluetoothProtocolDriver(id, profile, link, publish, fault)
{
    private readonly TaskCompletionSource _zero = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _version = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource<bool> _light = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _aOff;
    private bool _bOff;
    private byte _hardware;
    private byte _software;
    private AuxiliaryMotorMode _auxiliaryMotor;
    private bool _motionEnabled;
    private readonly BufferedFrequencyWriter _buffer = new();
    private ProgramPacketWriter? _programWriter;
    protected override DeviceCommandHandlers CreateCommandHandlers() => new DeviceCommandHandlers()
        .Add<QueryDevice>(QueryAsync)
        .Add<ConfigureLight>(async (command, cancellationToken) =>
        {
            await RefreshFeaturesAsync(cancellationToken).ConfigureAwait(false);
            await SetLightAsync(command.Enabled, cancellationToken).ConfigureAwait(false);
        })
        .Add<ControlStepCounter>((command, cancellationToken) => WriteAsync(V1Protocol.WithChecksum([0x35, 0x13, (byte)command.Action]), cancellationToken))
        .Add<ConfigureMotionStream>((command, cancellationToken) => SetMotionAsync(command.Enabled, cancellationToken));
    public override async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await StopAsync(cancellationToken).ConfigureAwait(false);
        ThrowIfFaulted();
        foreach (byte type in new byte[] { 4, 1, 2 })
        {
            await WriteAsync(V1Protocol.Query(type), cancellationToken).ConfigureAwait(false);
            ThrowIfFaulted();
        }
        await ConfirmAsync(_zero.Task, cancellationToken).ConfigureAwait(false);
    }
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _programWriter?.Reset();
        _buffer.Reset();
        Exception? motorError = null;
        try
        {
            if (_auxiliaryMotor != AuxiliaryMotorMode.Off)
            {
                await WriteAsync(V1Protocol.AuxiliaryMotor(AuxiliaryMotorMode.Off), cancellationToken).ConfigureAwait(false);
                _auxiliaryMotor = AuxiliaryMotorMode.Off;
            }
        }
        catch (Exception error)
        {
            motorError = error;
        }
        try
        {
            await WriteAsync(Profile.Protocol == BluetoothProtocol.YokonexV1 ? V1Protocol.Stop() : V2Protocol.Stop(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (motorError is not null)
        {
            throw new LocalizedAggregateException("Error.StopUnconfirmed", [motorError, error]);
        }
        if (motorError is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(motorError).Throw();
    }
    public override async Task SendAsync(OutputFrame frame, CancellationToken cancellationToken)
    {
        var auxiliary = frame.Hardware?.AuxiliaryMotor ?? AuxiliaryMotorMode.Off;
        if (auxiliary != AuxiliaryMotorMode.Off && _motionEnabled)
            throw new LocalizedOperationException("Error.ModeDeviceMismatch");
        if (auxiliary != _auxiliaryMotor)
        {
            await WriteAsync(V1Protocol.AuxiliaryMotor(auxiliary), cancellationToken).ConfigureAwait(false);
            _auxiliaryMotor = auxiliary;
        }
        var packets = frame.NativeProgram?.Settings switch
        {
            { Kind: NativeProgramKind.ElectricalPreset } native => Profile.Protocol == BluetoothProtocol.YokonexV1 ? V1Protocol.Fixed(frame, native) : [V2Protocol.Fixed(frame, native)],
            { Kind: NativeProgramKind.ElectricalBuffered } => _buffer.Encode(frame, Link.MaximumWriteLength),
            _ => Profile.Protocol == BluetoothProtocol.YokonexV1 ? V1Protocol.Encode(frame) : [V2Protocol.Encode(frame)]
        };
        if (frame.NativeProgram?.Settings.Kind == NativeProgramKind.ElectricalPreset)
        {
            await (_programWriter ??= new(WriteAsync)).SendAsync(packets, cancellationToken).ConfigureAwait(false);
            return;
        }
        _programWriter?.Reset();
        for (int index = 0; index < packets.Count; index++)
        {
            if (index > 0)
                await Task.Delay(10, cancellationToken).ConfigureAwait(false);
            await WriteAsync(packets[index], cancellationToken).ConfigureAwait(false);
        }
    }
    public override void OnNotification(GattNotification notification)
    {
        if (notification.Address != Profile.Notify || V1Protocol.Parse(notification.Data) is not { } status)
            return;
        if (status.ErrorCode is > 0)
        {
            Publish(new(Id, telemetry: new(ProtocolErrorCode: status.ErrorCode)));
            Reject(new("Error.BluetoothRejected", [status.ErrorCode.Value,
                new LocalizedMessage($"ProtocolError.{(status.ErrorCode is >= 1 and <= 5 ? status.ErrorCode.Value.ToString() : "Unknown")}", [])]));
            return;
        }
        if (status.OutputAEnabled is false)
            _aOff = true;
        if (status.OutputBEnabled is false)
            _bOff = true;
        if (_aOff && _bOff)
            _zero.TrySetResult();
        if (status.HardwareVersion is > 0 && status.SoftwareVersion is > 0)
        {
            _hardware = status.HardwareVersion.Value;
            _software = status.SoftwareVersion.Value;
            _version.TrySetResult();
        }
        if (status.LightEnabled is { } light)
            _light.TrySetResult(light);
        // A delayed off report must not cancel a newer request that still requires an explicit stop.
        if (status.AuxiliaryMotor is { } auxiliary && auxiliary != AuxiliaryMotorMode.Off)
            _auxiliaryMotor = auxiliary;
        Publish(new(Id, status.Battery, status.ContactA, status.ContactB, status.OutputAEnabled, status.OutputBEnabled,
            status.HardwareVersion, status.SoftwareVersion, status.LightEnabled, status.MotionAngle, status.Steps,
            telemetry: new(IntensityA: status.IntensityA, IntensityB: status.IntensityB, ModeA: status.ModeA,
                ModeB: status.ModeB, AuxiliaryMotor: status.AuxiliaryMotor,
                AccelerationAxes: status.AccelerationAxes, GyroscopeAxes: status.GyroscopeAxes)));
    }
    public override async Task SetMotionAsync(bool enabled, CancellationToken cancellationToken)
    {
        if (enabled && _auxiliaryMotor != AuxiliaryMotorMode.Off)
        {
            await WriteAsync(V1Protocol.AuxiliaryMotor(AuxiliaryMotorMode.Off), cancellationToken).ConfigureAwait(false);
            _auxiliaryMotor = AuxiliaryMotorMode.Off;
        }
        await WriteAsync(V1Protocol.MotionStreaming(enabled), cancellationToken).ConfigureAwait(false);
        _motionEnabled = enabled;
    }
    public override async Task<ElectricalFeatureState> RefreshFeaturesAsync(CancellationToken cancellationToken)
    {
        if (!Profile.Light || HasFault)
            throw new LocalizedOperationException("Error.DeviceFeatures");
        if (!_version.Task.IsCompletedSuccessfully)
        {
            await WriteAsync(V2Protocol.QueryVersion(), cancellationToken).ConfigureAwait(false);
            await ConfirmAsync(_version.Task, cancellationToken).ConfigureAwait(false);
        }
        _light = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await WriteAsync(V2Protocol.QueryLight(), cancellationToken).ConfigureAwait(false);
        try
        {
            return new(_hardware, _software, await _light.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false));
        }
        catch (TimeoutException error)
        {
            throw new LocalizedIOException("Error.LightTimeout", error);
        }
    }
    public override async Task<bool> SetLightAsync(bool enabled, CancellationToken cancellationToken)
    {
        if (!Profile.Light || !_version.Task.IsCompletedSuccessfully || HasFault)
            throw new LocalizedOperationException("Error.DeviceFeatures");
        _light = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await WriteAsync(V2Protocol.SetLight(enabled), cancellationToken).ConfigureAwait(false);
        await WriteAsync(V2Protocol.QueryLight(), cancellationToken).ConfigureAwait(false);
        try
        {
            bool actual = await _light.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            if (actual != enabled)
                throw new LocalizedIOException("Error.LightTimeout");
            return actual;
        }
        catch (TimeoutException error)
        {
            throw new LocalizedIOException("Error.LightTimeout", error);
        }
    }

    private async Task QueryAsync(QueryDevice command, CancellationToken cancellationToken)
    {
        if (command.Query is DeviceQuery.All)
        {
            foreach (byte type in new byte[] { 1, 2, 3, 4, 5, 6 })
                await WriteAsync(V1Protocol.Query(type), cancellationToken).ConfigureAwait(false);
            if (Profile.Light)
                await RefreshFeaturesAsync(cancellationToken).ConfigureAwait(false);
            return;
        }
        if (command.Query is DeviceQuery.Firmware or DeviceQuery.Light)
        {
            if (!Profile.Light)
                throw new LocalizedOperationException("Error.DeviceFeatures");
            await RefreshFeaturesAsync(cancellationToken).ConfigureAwait(false);
            return;
        }
        int query = command.Query switch
        {
            DeviceQuery.ChannelA => 1,
            DeviceQuery.ChannelB => 2,
            DeviceQuery.AuxiliaryMotor => 3,
            DeviceQuery.Battery => 4,
            DeviceQuery.Steps => 5,
            DeviceQuery.Motion => 6,
            _ => throw new LocalizedOperationException("Error.DeviceFeatures")
        };
        await WriteAsync(V1Protocol.Query((byte)query), cancellationToken).ConfigureAwait(false);
    }
}
