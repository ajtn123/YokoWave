using Avalonia.Threading;
using YokoWave.Core;
using YokoWave.Core.Localization;
using YokoWave.Remote.DgLab.V4;

namespace YokoWave.App.ViewModels;

public sealed class RemoteSourceItem : ObservableObject
{
    private readonly Func<Task> _refresh;
    private readonly Action<Exception> _report;
    private bool _connected;
    private DgLabV4Transport? _dglab;
    private Task _refreshing = Task.CompletedTask;
    public RemoteEndpointSettings Settings { get; }
    public string Name => Settings.Name;
    public string Url => Settings.Url;
    public string Token { get; set; } = "";
    public bool NeedsToken => Settings.Kind != RemoteConnectionKind.DgLabV4;
    public PairingCodeViewModel PairingCode { get; } = new();
    public bool HasStatus => _dglab is not null;
    public string Status => _dglab is null ? "" : !_dglab.IsConnected ? L10n.Get("Error.RemoteDisconnected")
        : _dglab.Error?.Message ?? (_dglab.Slots.Count == 0 ? L10n.Get("DgLab.Waiting") : L10n.Format("DgLab.DeviceCount", _dglab.Slots.Count));
    public bool IsConnected
    {
        get => _connected;
        set { if (Set(ref _connected, value)) RefreshLocalization(); }
    }
    public string ConnectionAction => L10n.Get(IsConnected ? "Device.Disconnect" : "Device.Connect");
    public AsyncCommand ConnectCommand { get; }
    public AsyncCommand RemoveCommand { get; }

    public RemoteSourceItem(RemoteEndpointSettings settings, Func<RemoteSourceItem, Task> connect, Func<RemoteSourceItem, Task> remove,
        Func<Task> refresh, Action<Exception> report)
    {
        Settings = settings;
        _refresh = refresh;
        _report = report;
        ConnectCommand = new(() => connect(this), report);
        RemoveCommand = new(() => remove(this), report);
    }

    public void Attach(DgLabV4Transport transport)
    {
        _dglab = transport;
        transport.DevicesChanged += OnDevicesChanged;
        PairingCode.Update(transport.PairingUrl, transport.ControllerId);
        Raise(nameof(Status));
        Raise(nameof(HasStatus));
    }

    public async Task DetachAsync()
    {
        if (_dglab is not null)
            _dglab.DevicesChanged -= OnDevicesChanged;
        _dglab = null;
        PairingCode.Update("");
        await _refreshing;
        Raise(nameof(Status));
        Raise(nameof(HasStatus));
    }

    public void RefreshLocalization()
    {
        Raise(nameof(ConnectionAction));
        Raise(nameof(Status));
    }

    public DgLabV4Slot? FindSlot(string deviceId) => _dglab?.FindSlot(deviceId);

    private void OnDevicesChanged() => Dispatcher.UIThread.Post(() =>
    {
        PairingCode.Update(_dglab?.PairingUrl ?? "", _dglab?.ControllerId ?? "");
        Raise(nameof(Status));
        _refreshing = RefreshAsync(_refreshing);
    });

    private async Task RefreshAsync(Task previous)
    {
        await previous;
        if (!IsConnected || _dglab is null)
            return;
        try
        {
            await _refresh();
        }
        catch (Exception error)
        {
            _report(error);
        }
    }
}
