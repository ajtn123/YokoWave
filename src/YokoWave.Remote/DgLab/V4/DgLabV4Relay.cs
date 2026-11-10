using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using YokoWave.Core.Localization;

namespace YokoWave.Remote.DgLab.V4;

/// <summary>V4 routing only. Device operations and playback belong to the connected app, never the relay.</summary>
public sealed class DgLabV4Relay(DgLabV4RelaySettings settings, TimeProvider? clock = null) : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, RelayConnection> _connections = [];
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private WebApplication? _app;
    private X509Certificate2? _certificate;
    private Task _heartbeat = Task.CompletedTask;
    private int _disposed;
    public Uri? Url { get; private set; }
    public Exception? Error { get; private set; }

    public async Task StartAsync(string? certificatePassword = null, CancellationToken cancellationToken = default)
    {
        settings.Validate();
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (_app is not null)
            throw new LocalizedOperationException("Error.AlreadyRunning");
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Parse(settings.Address), settings.Port, listen =>
        {
            if (settings.CertificatePath is { Length: > 0 } path)
            {
                _certificate = X509CertificateLoader.LoadPkcs12FromFile(path, certificatePassword);
                listen.UseHttps(_certificate);
            }
        }));
        var app = builder.Build();
        app.UseWebSockets(new WebSocketOptions
        {
            KeepAliveInterval = TimeSpan.FromMilliseconds(settings.PingMilliseconds),
            KeepAliveTimeout = TimeSpan.FromMilliseconds((long)settings.PingMilliseconds * settings.MaximumMissedPongs)
        });
        app.Run(AcceptAsync);
        try
        {
            await app.StartAsync(cancellationToken).ConfigureAwait(false);
            _app = app;
            string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            Url = new UriBuilder(address) { Scheme = settings.CertificatePath is { Length: > 0 } ? "wss" : "ws", Path = settings.Path }.Uri;
            _heartbeat = HeartbeatAsync();
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
            finally
            {
                _certificate?.Dispose();
            }
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _lifetime.Cancel();
        RelayConnection[] connections;
        lock (_gate)
            connections = _connections.Values.ToArray();
        foreach (var connection in connections)
            connection.Wire.Abort();
        await _heartbeat.ConfigureAwait(false);
        try
        {
            if (_app is not null)
                await _app.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _certificate?.Dispose();
            _lifetime.Dispose();
        }
    }

    private async Task AcceptAsync(HttpContext context)
    {
        if (context.Request.Path != settings.Path)
        {
            context.Response.StatusCode = 404;
            return;
        }
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = 426;
            return;
        }
        string? controllerId = DgLabV4Protocol.TargetController(context.Request.QueryString.Value ?? "");
        using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        await using var wire = new JsonWebSocket(socket, tolerantInput: true);
        RelayConnection connection;
        bool attached;
        lock (_gate)
        {
            connection = new RelayConnection(NewId(), wire, string.IsNullOrEmpty(controllerId) ? null : controllerId, _clock.GetTimestamp());
            attached = _connections.Count < 256 && (connection.Controller is null ||
                _connections.TryGetValue(connection.Controller, out var controller) && controller.Controller is null);
            Send(connection, new { type = "hello", clientId = connection.Id });
            if (attached)
            {
                _connections.Add(connection.Id, connection);
                if (connection.Controller is { } target)
                {
                    var owner = _connections[target];
                    owner.Clients.Add(connection.Id);
                    Send(connection, new { type = "controller_attached", clientId = target });
                    Send(owner, new { type = "client_attached", clientId = connection.Id });
                }
            }
        }
        wire.Received += frame => Receive(connection, frame);
        wire.Start();
        if (!attached)
        {
            Send(connection, new { type = "error", code = "controller_not_found" });
            await wire.CloseAsync(4001, "controller_not_found").ConfigureAwait(false);
            return;
        }
        try
        {
            await wire.Completion.ConfigureAwait(false);
        }
        finally
        {
            await DetachAsync(connection).ConfigureAwait(false);
        }
    }

    private void Receive(RelayConnection connection, JsonElement frame)
    {
        lock (_gate)
        {
            if (!_connections.ContainsKey(connection.Id))
                return;
            if (!frame.TryGetProperty("type", out var kind) || kind.ValueKind != JsonValueKind.String)
                return;
            string? type = kind.GetString();
            if (type == "ping")
            {
                Send(connection, new { type = "pong", ts = _clock.GetUtcNow().ToUnixTimeMilliseconds() });
                return;
            }
            if (type != "message")
                return;
            var data = DgLabV4Protocol.Data(frame).Clone();
            if (connection.Controller is { } controllerId)
            {
                if (_connections.TryGetValue(controllerId, out var controller))
                    Send(controller, new { type = "message", clientId = connection.Id, data });
            }
            else if (!frame.TryGetProperty("clientId", out var field) || field.ValueKind != JsonValueKind.String)
            {
                Send(connection, new { type = "error", code = "bad_request" });
            }
            else if (field.GetString() is { } target && connection.Clients.Contains(target) && _connections.TryGetValue(target, out var client))
            {
                Send(client, new { type = "message", data });
            }
            else
            {
                Send(connection, new { type = "error", code = "client_not_found", clientId = field.GetString() });
            }
        }
    }

    private async Task DetachAsync(RelayConnection connection)
    {
        List<RelayConnection> removed = [];
        lock (_gate)
        {
            if (!_connections.Remove(connection.Id))
                return;
            if (connection.Controller is { } parent && _connections.TryGetValue(parent, out var controller))
            {
                controller.Clients.Remove(connection.Id);
                controller.IdleSince = _clock.GetTimestamp();
                Send(controller, new { type = "client_disconnected", clientId = connection.Id });
            }
            foreach (string id in connection.Clients)
            {
                if (_connections.Remove(id, out var client))
                {
                    Send(client, new { type = "controller_disconnected", clientId = connection.Id });
                    removed.Add(client);
                }
            }
        }
        foreach (var client in removed)
            await client.Wire.CloseAsync(4000, "controller_disconnected").ConfigureAwait(false);
    }

    private async Task HeartbeatAsync()
    {
        try
        {
            long heartbeatAt = _clock.GetTimestamp();
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(Math.Min(settings.HeartbeatMilliseconds, settings.IdleMilliseconds)), _clock);
            while (await timer.WaitForNextTickAsync(_lifetime.Token).ConfigureAwait(false))
            {
                List<RelayConnection> expired = [];
                bool heartbeatDue = _clock.GetElapsedTime(heartbeatAt) >= TimeSpan.FromMilliseconds(settings.HeartbeatMilliseconds);
                if (heartbeatDue)
                    heartbeatAt = _clock.GetTimestamp();
                lock (_gate)
                {
                    foreach (var connection in _connections.Values)
                    {
                        if (heartbeatDue)
                            Send(connection, new { type = "heartbeat" });
                        if (connection.Controller is null && connection.Clients.Count == 0 &&
                            _clock.GetElapsedTime(connection.IdleSince) >= TimeSpan.FromMilliseconds(settings.IdleMilliseconds))
                        {
                            Send(connection, new { type = "idle_timeout" });
                            expired.Add(connection);
                        }
                    }
                }
                foreach (var connection in expired)
                    await connection.Wire.CloseAsync(4002, "idle_timeout").ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            Error = error;
        }
    }

    private static void Send(RelayConnection connection, object frame)
    {
        try
        {
            connection.Wire.Send(frame);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException)
        {
            // A closing or backpressured peer must not terminate routing for other connections.
            connection.Wire.Abort(error);
        }
    }

    private string NewId()
    {
        lock (_gate)
        {
            string id;
            do
                id = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
            while (_connections.ContainsKey(id));
            return id;
        }
    }

    private sealed class RelayConnection(string id, JsonWebSocket wire, string? controller, long idleSince)
    {
        public string Id { get; } = id;
        public JsonWebSocket Wire { get; } = wire;
        public string? Controller { get; } = controller;
        public HashSet<string> Clients { get; } = [];
        public long IdleSince { get; set; } = idleSince;
    }
}
