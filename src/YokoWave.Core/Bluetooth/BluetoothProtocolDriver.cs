using YokoWave.Core.Localization;

namespace YokoWave.Core.Bluetooth;

internal abstract class BluetoothProtocolDriver(string id, BluetoothDeviceProfile profile, IBluetoothConnection link,
    Action<DeviceStatusUpdate> publish, Action<Exception> fault)
{
    protected string Id { get; } = id;
    public BluetoothDeviceProfile Profile { get; } = profile;
    protected IBluetoothConnection Link { get; } = link;
    protected Action<DeviceStatusUpdate> Publish { get; } = publish;
    private Exception? _fault;
    public bool HasFault => Volatile.Read(ref _fault) is not null;
    protected void Reject(string key)
    {
        var error = new LocalizedOperationException(key);
        if (Interlocked.CompareExchange(ref _fault, error, null) is null) fault(error);
    }
    public void Check(OutputFrame frame)
    {
        if (Profile.Domain == OutputDomain.Input && (frame.ChannelA > 0 || frame.ChannelB > 0 || frame.ChannelC > 0 || frame.Pump is not null))
            throw new LocalizedOperationException("Error.SensorOnly");
        if (_fault is { } error && (frame.ChannelA > 0 || frame.ChannelB > 0 || frame.ChannelC > 0 || frame.Pump is { Direction: not PumpDirection.Off } or { Water: true })) throw error;
        if (Profile.Domain == OutputDomain.Motor && ((frame.ChannelA > 0 || frame.ChannelC > 0) && frame.FamilyA is WaveformFamily.Electrical or WaveformFamily.DgLab ||
            frame.ChannelB > 0 && frame.FamilyB is WaveformFamily.Electrical or WaveformFamily.DgLab))
            throw new LocalizedArgumentException("Error.WaveDeviceMismatch");
        if (Profile.Domain == OutputDomain.Electrical)
        {
            var dg = Profile.Protocol is BluetoothProtocol.CoyoteV2 or BluetoothProtocol.CoyoteV3;
            bool Invalid(WaveformFamily family) => family == WaveformFamily.Motor || (dg ? family == WaveformFamily.Electrical : family == WaveformFamily.DgLab);
            if (frame.ChannelA > 0 && Invalid(frame.FamilyA) || frame.ChannelB > 0 && Invalid(frame.FamilyB))
                throw new LocalizedArgumentException("Error.WaveDeviceMismatch");
        }
    }
    protected Task Write(byte[] packet, CancellationToken ct) => Link.WriteAsync(Profile.Write, packet, ct);
    protected async Task Battery(GattAddress address, CancellationToken ct)
    {
        if (await Link.ReadAsync(address, ct) is { } data) OnNotification(new(address, data));
    }
    protected static async Task Confirm(Task ready, CancellationToken ct, string key = "Error.BluetoothState")
    {
        try { await ready.WaitAsync(TimeSpan.FromSeconds(3), ct); }
        catch (TimeoutException error) { throw new LocalizedIOException(key, error); }
    }
    public abstract Task Initialize(CancellationToken ct);
    public abstract Task Send(OutputFrame frame, CancellationToken ct);
    public abstract Task Stop(CancellationToken ct);
    public virtual Task Shutdown(CancellationToken ct) => Stop(ct);
    public abstract void OnNotification(GattNotification notification);
    public virtual Task SetMotion(bool enabled, CancellationToken ct) => throw new LocalizedOperationException("Error.MotionUnavailable");
    public virtual Task<ElectricalFeatureState> RefreshFeatures(CancellationToken ct) => throw new LocalizedOperationException("Error.DeviceFeatures");
    public virtual Task<bool> SetLight(bool enabled, CancellationToken ct) => throw new LocalizedOperationException("Error.DeviceFeatures");
    public virtual Task ResetInput(CancellationToken ct) => throw new LocalizedOperationException("Error.DeviceFeatures");
}
