using YokoWave.Core.Localization;

namespace YokoWave.Core.Bluetooth;

internal abstract class BluetoothProtocolDriver(string id, BluetoothDeviceProfile profile, IBluetoothConnection link,
    Action<DeviceStatusUpdate> publish, Action<Exception> fault)
{
    private readonly TaskCompletionSource _rejected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Exception? _fault;
    private DeviceCommandHandlers? _commands;

    protected string Id { get; } = id;
    public BluetoothDeviceProfile Profile { get; } = profile;
    protected IBluetoothConnection Link { get; } = link;
    protected Action<DeviceStatusUpdate> Publish { get; } = publish;
    public bool HasFault => Volatile.Read(ref _fault) is not null;
    public event Action<DeviceCommand>? CommandRequested;
    protected void RequestCommand(DeviceCommand command) => CommandRequested?.Invoke(command);
    protected virtual DeviceCommandHandlers CreateCommandHandlers() => new();
    public Task ExecuteCommandAsync(DeviceCommand command, CancellationToken cancellationToken)
    {
        command.Validate();
        if (!Profile.Supports(command.Capability))
            throw new LocalizedOperationException("Error.DeviceFeatures");
        if (command is ConfigureSensorStream { IndicatorColor: not 1 } && Profile.Protocol != BluetoothProtocol.Paw)
            throw new LocalizedArgumentException("Error.ProtocolSettings");
        if (command is QueryDevice query && !Profile.SupportedQueries.Contains(query.Query))
            throw new LocalizedOperationException("Error.DeviceFeatures");
        return (_commands ??= CreateCommandHandlers()).ExecuteAsync(command, cancellationToken);
    }
    protected void Reject(LocalizedMessage message)
    {
        var error = new LocalizedOperationException(message);
        if (Interlocked.CompareExchange(ref _fault, error, null) is null)
        {
            _rejected.TrySetResult();
            fault(error);
        }
    }
    public void ThrowIfFaulted()
    {
        if (Volatile.Read(ref _fault) is { } error)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }
    public void Check(OutputFrame frame)
    {
        frame.Hardware?.Validate();
        if (frame.NativeProgram is { } native)
        {
            native.ValidateOutput(frame);
            if (!Profile.Supports(native.Settings.RequiredCapability))
                throw new LocalizedOperationException("Error.ModeDeviceMismatch");
        }
        if (frame.Hardware is { } hardware && (hardware.AuxiliaryMotor != AuxiliaryMotorMode.Off && !Profile.Supports(DeviceCapability.AuxiliaryMotor) ||
            hardware.RelativeStrength && !Profile.Supports(DeviceCapability.RelativeStrength)))
        {
            throw new LocalizedOperationException("Error.ModeDeviceMismatch");
        }

        if (Profile.Domain == OutputDomain.Input && (frame.ChannelA > 0 || frame.ChannelB > 0 || frame.ChannelC > 0 || frame.Pump is not null))
            throw new LocalizedOperationException("Error.SensorOnly");
        if (frame.ChannelA > 0 || frame.ChannelB > 0 || frame.ChannelC > 0 ||
            frame.Hardware is { AuxiliaryMotor: not AuxiliaryMotorMode.Off } || frame.Pump is { Direction: not PumpDirection.Off } or { Water: true })
        {
            ThrowIfFaulted();
        }

        frame.ValidateSamples();
    }
    protected Task WriteAsync(byte[] packet, CancellationToken cancellationToken) => Link.WriteAsync(Profile.Write, packet, cancellationToken);
    protected async Task QueryBatteryAsync(GattAddress address, CancellationToken cancellationToken)
    {
        if (await Link.ReadAsync(address, cancellationToken).ConfigureAwait(false) is { } data)
            OnNotification(new(address, data));
    }
    protected async Task ConfirmAsync(Task ready, CancellationToken cancellationToken, string key = "Error.BluetoothState")
    {
        try
        {
            var completed = await Task.WhenAny(ready, _rejected.Task).WaitAsync(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
            ThrowIfFaulted();
            await completed.ConfigureAwait(false);
        }
        catch (TimeoutException error)
        {
            throw new LocalizedIOException(key, error);
        }
    }
    public abstract Task InitializeAsync(CancellationToken cancellationToken);
    public abstract Task SendAsync(OutputFrame frame, CancellationToken cancellationToken);
    public abstract Task StopAsync(CancellationToken cancellationToken);
    public virtual Task ShutdownAsync(CancellationToken cancellationToken) => StopAsync(cancellationToken);
    public abstract void OnNotification(GattNotification notification);
    public virtual Task SetMotionAsync(bool enabled, CancellationToken cancellationToken) => throw new LocalizedOperationException("Error.MotionUnavailable");
    public virtual Task<ElectricalFeatureState> RefreshFeaturesAsync(CancellationToken cancellationToken) => throw new LocalizedOperationException("Error.DeviceFeatures");
    public virtual Task<bool> SetLightAsync(bool enabled, CancellationToken cancellationToken) => throw new LocalizedOperationException("Error.DeviceFeatures");
    public virtual Task ResetInputAsync(CancellationToken cancellationToken) => throw new LocalizedOperationException("Error.DeviceFeatures");
}
