using System.Security.Cryptography;
using Avalonia.Threading;
using YokoWave.Core;
using YokoWave.Core.Localization;
using YokoWave.Remote;

namespace YokoWave.App.ViewModels;

public sealed class RemoteServerEditorViewModel : ObservableObject
{
    private readonly Func<bool> _canEdit;
    private readonly DeviceController _controller;
    private readonly Guid _id;
    private readonly Action<Exception> _report;
    private RemoteServerSettings _settings;
    private RemoteServer? _server;
    private bool _running;
    public string Token { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    public string CertificatePassword { get; set; } = "";
    public string Address { get => _settings.Address; set => Change(_settings with { Address = value }, nameof(Address)); }
    public int Port { get => _settings.Port; set => Change(_settings with { Port = value }, nameof(Port)); }
    public int LeaseMilliseconds { get => _settings.LeaseMilliseconds; set => Change(_settings with { LeaseMilliseconds = value }, nameof(LeaseMilliseconds)); }
    public double ControlLimit { get => _settings.ControlLimit; set => Change(_settings with { ControlLimit = value }, nameof(ControlLimit)); }
    public string CertificatePath { get => _settings.CertificatePath ?? ""; set => Change(_settings with { CertificatePath = string.IsNullOrWhiteSpace(value) ? null : value }, nameof(CertificatePath)); }
    public RemoteServerSettings Settings => _settings;
    public bool IsRunning => _server?.Session.IsRunning == true;
    public TimeSpan Elapsed => _server?.Session.Elapsed ?? TimeSpan.Zero;
    public TimeSpan? Remaining => _server?.Session.Remaining;
    public TimeSpan? Duration => _server?.Session.Duration;
    public string Url => _server?.Url?.ToString() ?? "";
    public RemoteServerEditorViewModel(Guid id, DeviceController controller, RemoteServerSettings? settings, Func<bool> canEdit, Action<Exception> report)
    {
        _id = id;
        _controller = controller;
        _settings = settings ?? new();
        _canEdit = canEdit;
        _report = report;
    }
    private void Change(RemoteServerSettings value, string property)
    {
        if (!_canEdit() || _settings == value) return;
        _settings = value;
        Raise(property);
    }
    public async Task StartAsync(IReadOnlyList<string> targets, TimeSpan? duration)
    {
        if (_server is not null) await StopAsync();
        var session = new RemoteExportSession(_id, _controller, targets, _settings, duration);
        session.Faulted += error => Dispatcher.UIThread.Post(() => _report(error));
        var server = new RemoteServer(session, Token);
        _server = server;
        try { await server.StartAsync(CertificatePassword); }
        catch (Exception error)
        {
            _server = null;
            try { await server.DisposeAsync(); }
            catch (Exception cleanup) { throw new LocalizedAggregateException("Error.Cleanup", [error, cleanup]); }
            throw;
        }
        Refresh();
    }
    public async Task StopAsync()
    {
        List<Exception> errors = [];
        try { if (_server is not null) await _server.DisposeAsync(); }
        catch (Exception error) { errors.Add(error); }
        _server = null;
        try { await _controller.StopOwnedAsync(_id); }
        catch (Exception error) { errors.Add(error); }
        Refresh();
        if (errors.Count > 0) throw new LocalizedAggregateException("Error.StopUnconfirmed", errors);
    }
    public void Refresh()
    {
        if (_running != IsRunning)
        {
            _running = IsRunning;
            Raise(nameof(IsRunning));
        }
        Raise(nameof(Url));
    }
}
