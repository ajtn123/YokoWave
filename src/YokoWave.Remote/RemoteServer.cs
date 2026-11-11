using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using YokoWave.Core.Localization;

namespace YokoWave.Remote;

/// <summary>Mode endpoints share a listener when their binding and certificate match.</summary>
public sealed class RemoteServer : IAsyncDisposable
{
    private static readonly SemaphoreSlim _hostingGate = new(1, 1);
    private static readonly Dictionary<string, Listener> _listeners = [];
    private readonly ConcurrentDictionary<RemotePeerSession, byte> _peers = new();
    private readonly string _token;
    private string? _listenerKey;
    private int _disposed;
    private int _started;

    public RemoteServerSession Session { get; }
    public Uri? Url { get; private set; }
    public RemoteServer(RemoteServerSession session, string token)
    {
        if (token.Length is < 24 or > 256)
            throw new LocalizedArgumentException("Error.RemoteAuthentication");
        Session = session;
        _token = token;
    }
    public async Task StartAsync(string? certificatePassword = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _started, 1) != 0)
            throw new LocalizedOperationException("Error.AlreadyRunning");
        try
        {
            await Session.StartAsync(cancellationToken).ConfigureAwait(false);
            await _hostingGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var settings = Session.Settings;
                string certificatePath = settings.CertificatePath ?? "";
                string key = settings.Port == 0 ? $"ephemeral:{Guid.NewGuid():N}" : $"{settings.Address}:{settings.Port}";
                if (!_listeners.TryGetValue(key, out var listener))
                {
                    var builder = WebApplication.CreateSlimBuilder();
                    builder.Logging.ClearProviders();
                    builder.WebHost.ConfigureKestrel(options =>
                    {
                        options.Limits.MaxConcurrentConnections = 32;
                        options.Limits.MaxConcurrentUpgradedConnections = 32;
                        options.Listen(IPAddress.Parse(settings.Address), settings.Port, listen =>
                        {
                            if (certificatePath.Length > 0)
                                listen.UseHttps(X509CertificateLoader.LoadPkcs12FromFile(certificatePath, certificatePassword));
                        });
                    });
                    var app = builder.Build();
                    listener = new(app, certificatePath);
                    app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(10) });
                    var captured = listener;
                    app.Run(context => AcceptAsync(captured, context));
                    try
                    {
                        await app.StartAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception error)
                    {
                        try
                        {
                            await app.DisposeAsync().ConfigureAwait(false);
                        }
                        catch (Exception cleanup)
                        {
                            throw new LocalizedAggregateException("Error.Cleanup", [error, cleanup]);
                        }
                        throw;
                    }
                    _listeners.Add(key, listener);
                }
                else if (listener.Certificate != certificatePath)
                {
                    throw new LocalizedOperationException("Error.RemoteBinding");
                }

                if (!listener.Routes.TryAdd(Session.Id, this))
                    throw new LocalizedOperationException("Error.RemoteExists");
                _listenerKey = key;
                var addresses = listener.App.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;
                var baseUri = new Uri(addresses.Addresses.First());
                Url = new UriBuilder(baseUri) { Scheme = certificatePath.Length == 0 ? "ws" : "wss", Path = $"/ws/{Session.Id:N}" }.Uri;
                _ = ObserveSessionAsync();
            }
            finally
            {
                _hostingGate.Release();
            }
        }
        catch (Exception error)
        {
            try
            {
                await DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception cleanup)
            {
                throw new LocalizedAggregateException("Error.Cleanup", [error, cleanup]);
            }
            throw;
        }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        Exception? failure = null;
        try
        {
            await Session.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            failure = error;
        }
        foreach (var peer in _peers.Keys)
            peer.Peer.Abort();
        await _hostingGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_listenerKey is { } key && _listeners.TryGetValue(key, out var listener))
            {
                listener.Routes.TryRemove(Session.Id, out _);
                if (listener.Routes.IsEmpty)
                {
                    _listeners.Remove(key);
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    try
                    {
                        await listener.App.StopAsync(timeout.Token).ConfigureAwait(false);
                    }
                    finally
                    {
                        await listener.App.DisposeAsync().ConfigureAwait(false);
                    }
                }
            }
        }
        catch (Exception error)
        {
            failure = failure is null ? error : new LocalizedAggregateException("Error.Cleanup", [failure, error]);
        }
        finally
        {
            _hostingGate.Release();
        }
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
    private async Task ObserveSessionAsync()
    {
        try
        {
            await Session.Completion.ConfigureAwait(false);
        }
        catch (Exception error)
        {
            Session.ReportFault(error);
        }
        if (Volatile.Read(ref _disposed) != 0)
            return;
        try
        {
            await DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            Session.ReportFault(error);
        }
    }
    private static async Task AcceptAsync(Listener listener, HttpContext context)
    {
        string[]? segments = context.Request.Path.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments is not ["ws", var id] || !Guid.TryParseExact(id, "N", out var modeId) || !listener.Routes.TryGetValue(modeId, out var server) || !server.Session.IsRunning)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        if (!context.WebSockets.IsWebSocketRequest || !context.WebSockets.WebSocketRequestedProtocols.Contains(RemoteProtocol.SubProtocol))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        var socket = await context.WebSockets.AcceptWebSocketAsync(RemoteProtocol.SubProtocol).ConfigureAwait(false);
        await using var peer = new RemotePeerSession(socket, server.Session, server._token);
        server._peers.TryAdd(peer, 0);
        try
        {
            await peer.RunAsync().ConfigureAwait(false);
        }
        finally
        {
            server._peers.TryRemove(peer, out _);
        }
    }

    private sealed class Listener(WebApplication app, string certificate)
    {
        public WebApplication App { get; } = app;
        public string Certificate { get; } = certificate;
        public ConcurrentDictionary<Guid, RemoteServer> Routes { get; } = new();
    }
}
