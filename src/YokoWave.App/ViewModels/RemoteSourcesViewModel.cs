using System.Collections.ObjectModel;
using YokoWave.Core;
using YokoWave.Core.Bluetooth;
using YokoWave.Core.Localization;
using YokoWave.Remote;
using YokoWave.Remote.DgLab.V4;

namespace YokoWave.App.ViewModels;

public sealed class RemoteSourcesViewModel : ObservableObject, IAsyncDisposable
{
    private readonly DeviceController _controller;
    private readonly Func<Task> _scan;
    private readonly Func<Task> _save;
    private readonly Func<string, Task> _stopModes;
    private readonly Action<Exception> _report;
    private string _name = "";
    private string _url = "";
    private string _token = "";
    private string _advertisedHost = "";
    private SessionOption? _kind;
    public string Name { get => _name; set => Set(ref _name, value); }
    public string Url { get => _url; set => Set(ref _url, value); }
    public string Token { get => _token; set => Set(ref _token, value); }
    public string AdvertisedHost { get => _advertisedHost; set => Set(ref _advertisedHost, value); }
    public bool NeedsToken => Kind?.Value != (int)RemoteConnectionKind.DgLabV4;
    public IReadOnlyList<SessionOption> Kinds { get; } = Enum.GetValues<RemoteConnectionKind>().Select(k => new SessionOption((int)k, "Remote.Kind." + k)).ToArray();
    public SessionOption? Kind
    {
        get => _kind;
        set { if (Set(ref _kind, value)) Raise(nameof(NeedsToken)); }
    }
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
            var settings = new RemoteEndpointSettings(Guid.NewGuid(), Name, Url, (RemoteConnectionKind)(Kind?.Value ?? 0), AdvertisedHost.Length == 0 ? null : AdvertisedHost);
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
    public async ValueTask DisposeAsync()
    {
        foreach (var item in Sources)
        {
            item.IsConnected = false;
            await item.DetachAsync();
            item.PairingCode.Dispose();
        }
    }
    public void RefreshLocalization()
    {
        foreach (var item in Sources)
            item.RefreshLocalization();
        foreach (var kind in Kinds)
            kind.RefreshLocalization();
    }
    private RemoteSourceItem Add(RemoteEndpointSettings settings)
    {
        var item = new RemoteSourceItem(settings, ConnectAsync, RemoveAsync, _scan, _report);
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
            item.IsConnected = false;
            await item.DetachAsync();
            await _controller.RemoveSourceAsync(key);
        }
        else
        {
            IDeviceTransport source;
            if (item.Settings.Kind == RemoteConnectionKind.Gatt)
            {
                var adapter = new WebSocketGattAdapter(item.Settings, item.Token);
                source = new BluetoothDeviceTransport(adapter);
            }
            else if (item.Settings.Kind == RemoteConnectionKind.Control)
            {
                source = new WebSocketControlTransport(item.Settings, item.Token);
            }
            else
            {
                source = new DgLabV4Transport(item.Settings.Id, item.Settings.Url, advertisedHost: item.Settings.AdvertisedHost);
            }

            try
            {
                await source.ScanAsync();
                await _controller.AddSourceAsync(key, source);
                item.IsConnected = true;
                if (source is DgLabV4Transport dglab)
                    item.Attach(dglab);
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
        item.PairingCode.Dispose();
        await _save();
    }
}
