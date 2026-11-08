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
    public override async Task Initialize(CancellationToken ct)
    {
        await Stop(ct);
        foreach (var type in new byte[] { 4, 1, 2 }) await Write(V1Protocol.Query(type), ct);
        await Confirm(_zero.Task, ct);
    }
    public override Task Stop(CancellationToken ct) => Write(Profile.Protocol == BluetoothProtocol.YokonexV1 ? V1Protocol.Stop() : V2Protocol.Stop(), ct);
    public override async Task Send(OutputFrame frame, CancellationToken ct)
    {
        var packets = Profile.Protocol == BluetoothProtocol.YokonexV1 ? V1Protocol.Encode(frame) : [V2Protocol.Encode(frame)];
        for (var index = 0; index < packets.Count; index++)
        {
            if (index > 0) await Task.Delay(10, ct);
            await Write(packets[index], ct);
        }
    }
    public override void OnNotification(GattNotification notification)
    {
        if (notification.Address != Profile.Notify || V1Protocol.Parse(notification.Data) is not { } status) return;
        if (status.ErrorCode is > 0) { Reject("Error.BluetoothRejected"); return; }
        if (status.OutputAEnabled is false) _aOff = true;
        if (status.OutputBEnabled is false) _bOff = true;
        if (_aOff && _bOff) _zero.TrySetResult();
        if (status.HardwareVersion is > 0 && status.SoftwareVersion is > 0)
        {
            _hardware = status.HardwareVersion.Value; _software = status.SoftwareVersion.Value; _version.TrySetResult();
        }
        if (status.LightEnabled is { } light) _light.TrySetResult(light);
        Publish(new(Id, status.Battery, status.ContactA, status.ContactB, status.OutputAEnabled, status.OutputBEnabled,
            status.HardwareVersion, status.SoftwareVersion, status.LightEnabled, status.MotionAngle, status.Steps));
    }
    public override Task SetMotion(bool enabled, CancellationToken ct) => Write(V1Protocol.MotionStreaming(enabled), ct);
    public override async Task<ElectricalFeatureState> RefreshFeatures(CancellationToken ct)
    {
        if (!Profile.Light || HasFault) throw new LocalizedOperationException("Error.DeviceFeatures");
        if (!_version.Task.IsCompletedSuccessfully) { await Write(V2Protocol.QueryVersion(), ct); await Confirm(_version.Task, ct); }
        _light = new(TaskCreationOptions.RunContinuationsAsynchronously); await Write(V2Protocol.QueryLight(), ct);
        try { return new(_hardware, _software, await _light.Task.WaitAsync(TimeSpan.FromSeconds(2), ct)); }
        catch (TimeoutException error) { throw new LocalizedIOException("Error.LightTimeout", error); }
    }
    public override async Task<bool> SetLight(bool enabled, CancellationToken ct)
    {
        if (!Profile.Light || !_version.Task.IsCompletedSuccessfully || HasFault) throw new LocalizedOperationException("Error.DeviceFeatures");
        _light = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await Write(V2Protocol.SetLight(enabled), ct); await Write(V2Protocol.QueryLight(), ct);
        try
        {
            var actual = await _light.Task.WaitAsync(TimeSpan.FromSeconds(2), ct);
            if (actual != enabled) throw new LocalizedIOException("Error.LightTimeout");
            return actual;
        }
        catch (TimeoutException error) { throw new LocalizedIOException("Error.LightTimeout", error); }
    }
}
