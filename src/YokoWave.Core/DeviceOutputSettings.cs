using YokoWave.Core.Bluetooth;
using YokoWave.Core.Localization;

namespace YokoWave.Core;

public sealed record DeviceOutputSettings(string DeviceId, bool Gentle = false, HardwareOutputSettings? Hardware = null, DgBalanceSettings? Balances = null)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(DeviceId) || DeviceId.Length > 512)
            throw new LocalizedArgumentException("Error.InvalidSettings");
        Hardware?.Validate();
    }
    public bool Accepts(DeviceDescriptor device, bool motion = false) => Hardware?.Accepts(device, motion) != false &&
        (Balances is null || device.Profile?.Supports(DeviceCapability.Balance) == true) &&
        (Hardware is not { ChannelALimit: not 100 } and not { ChannelBLimit: not 100 } || device.Profile?.Supports(DeviceCapability.ChannelLimits) == true);
}

public sealed partial class DeviceController
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DeviceOutputSettings> _outputSettings = new(StringComparer.Ordinal);
    public IReadOnlyList<DeviceOutputSettings> OutputSettings => _outputSettings.Values.OrderBy(s => s.DeviceId, StringComparer.Ordinal).ToArray();
    public DeviceOutputSettings GetOutputSettings(string id) => _outputSettings.GetValueOrDefault(id) ?? new(id);
    public void LoadOutputSettings(IEnumerable<DeviceOutputSettings> settings)
    {
        _outputSettings.Clear();
        foreach (var value in settings)
        {
            value.Validate();
            _outputSettings[value.DeviceId] = value;
        }
    }
    public async Task SetOutputSettingsAsync(DeviceDescriptor device, DeviceOutputSettings settings, CancellationToken ct = default, Guid? owner = null)
    {
        settings.Validate();
        using (await _operations.AcquireAsync([device.Id], ct))
        {
            ThrowIfDisposed();
            if (DeviceOwner(device.Id) is { } current && current != owner)
                throw new LocalizedOperationException("Error.DeviceInUse");
            if (settings.DeviceId != device.Id || !settings.Accepts(device, _connections.TryGetValue(device.Id, out var connected) && connected.MotionStreaming))
                throw new LocalizedArgumentException("Error.ProtocolSettings");
            var previous = _outputSettings.GetValueOrDefault(device.Id);
            _outputSettings[device.Id] = settings;
            try
            {
                if (_connections.TryGetValue(device.Id, out var connection))
                    await WriteCoreAsync(connection, OutputFrame.Zero, ct);
            }
            catch
            {
                if (previous is null)
                    _outputSettings.TryRemove(device.Id, out _);
                else
                    _outputSettings[device.Id] = previous;
                throw;
            }
        }
        NotifyChanged();
    }
    private OutputFrame ApplyOutputSettings(DeviceDescriptor device, OutputFrame frame)
    {
        if (frame == OutputFrame.Zero)
            return frame;
        if (!_outputSettings.TryGetValue(device.Id, out var settings))
            return frame;
        if (!settings.Accepts(device))
            throw new LocalizedArgumentException("Error.ProtocolSettings");
        return frame with { Gentle = settings.Gentle, Hardware = settings.Hardware, DgBalances = settings.Balances };
    }
}
