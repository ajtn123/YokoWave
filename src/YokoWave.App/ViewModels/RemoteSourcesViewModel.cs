using System.Collections.ObjectModel;
using YokoWave.Core;
using YokoWave.Core.Bluetooth;
using YokoWave.Core.Localization;
using YokoWave.Remote;

namespace YokoWave.App.ViewModels;

public sealed class RemoteSourcesViewModel : ObservableObject
{
    private readonly DeviceController _controller;
    private readonly Func<Task> _scan;
    private readonly Func<Task> _save;
    private readonly Func<string, Task> _stopModes;
    private readonly Action<Exception> _report;
    private string _name = "";
    private string _url = "";
    private string _token = "";
    public string Name { get => _name; set => Set(ref _name, value); }
    public string Url { get => _url; set => Set(ref _url, value); }
    public string Token { get => _token; set => Set(ref _token, value); }
    public IReadOnlyList<SessionOption> Kinds { get; } = Enum.GetValues<RemoteConnectionKind>().Select(k => new SessionOption((int)k, "Remote.Kind." + k)).ToArray();
    public SessionOption? Kind { get; set; }
    public ObservableCollection<RemoteSourceItem> Sources { get; } = [];
    public AsyncCommand AddCommand { get; }
    public RemoteSourcesViewModel(DeviceController controller, Func<Task> scan, Func<Task> save, Func<string, Task> stopModes, Action<Exception> report)
    {
        _controller = controller;
        _scan = scan;
        _save = save;
        _stopModes = stopModes;
        _report = report;
        AddCommand = new(async () =>
        {
            var settings = new RemoteEndpointSettings(Guid.NewGuid(), Name, Url, (RemoteConnectionKind)(Kind?.Value ?? 0));
            settings.Validate();
            if (Sources.Count >= 16)
                throw new LocalizedOperationException("Error.RemoteSettings");
            var item = Add(settings);
            item.Token = Token;
            Token = "";
            await _save();
        }, report);
    }
    public void Load(IEnumerable<RemoteEndpointSettings> settings)
    {
        Sources.Clear();
        foreach (var endpoint in settings)
            Add(endpoint);
    }
    public List<RemoteEndpointSettings> ToSettings() => Sources.Select(item => item.Settings).ToList();
    public void RefreshLocalization()
    {
        foreach (var item in Sources)
            item.RefreshLocalization();
        foreach (var kind in Kinds)
            kind.RefreshLocalization();
    }
    private RemoteSourceItem Add(RemoteEndpointSettings settings)
    {
        var item = new RemoteSourceItem(settings, ConnectAsync, RemoveAsync, _report);
        Sources.Add(item);
        return item;
    }
    private async Task ConnectAsync(RemoteSourceItem item)
    {
        string key = $"remote:{item.Settings.Id:N}";
        if (item.IsConnected)
        {
            foreach (string id in _controller.SourceDeviceIds(key))
                await _stopModes(id);
            await _controller.RemoveSourceAsync(key);
            item.IsConnected = false;
        }
        else
        {
            IDeviceTransport source;
            if (item.Settings.Kind == RemoteConnectionKind.Gatt)
            {
                var adapter = new WebSocketGattAdapter(item.Settings, item.Token);
                source = new BluetoothDeviceTransport(adapter);
            }
            else
            {
                source = new WebSocketControlTransport(item.Settings, item.Token);
            }

            try
            {
                await source.ScanAsync();
                await _controller.AddSourceAsync(key, source);
                item.IsConnected = true;
            }
            catch (Exception error)
            {
                try
                {
                    await source.DisposeAsync();
                }
                catch (Exception cleanup)
                {
                    throw new LocalizedAggregateException("Error.Cleanup", [error, cleanup]);
                }
                throw;
            }
        }
        await _scan();
    }
    private async Task RemoveAsync(RemoteSourceItem item)
    {
        if (item.IsConnected)
            await ConnectAsync(item);
        Sources.Remove(item);
        await _save();
    }
}

public sealed class RemoteSourceItem : ObservableObject
{
    private bool _connected;
    public RemoteEndpointSettings Settings { get; }
    public string Name => Settings.Name;
    public string Url => Settings.Url;
    public string Token { get; set; } = "";
    public bool IsConnected
    {
        get => _connected;
        set
        {
            if (Set(ref _connected, value))
                RefreshLocalization();
        }
    }
    public string ConnectionAction => L10n.Get(IsConnected ? "Device.Disconnect" : "Device.Connect");
    public AsyncCommand ConnectCommand { get; }
    public AsyncCommand RemoveCommand { get; }
    public RemoteSourceItem(RemoteEndpointSettings settings, Func<RemoteSourceItem, Task> connect, Func<RemoteSourceItem, Task> remove, Action<Exception> report)
    {
        Settings = settings;
        ConnectCommand = new(() => connect(this), report);
        RemoveCommand = new(() => remove(this), report);
    }
    public void RefreshLocalization() => Raise(nameof(ConnectionAction));
}
