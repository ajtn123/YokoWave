using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace YokoWave.Tests.Support.Remote;

/// <summary>Runs wire-level scenarios that a conforming relay cannot produce, such as a delayed close frame.</summary>
internal sealed class ScriptedWebSocketServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly CancellationTokenSource _lifetime = new();
    private Task _script = Task.CompletedTask;
    public Uri Url { get; private set; } = null!;

    private ScriptedWebSocketServer(Func<WebSocket, CancellationToken, Task> script)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        _app = builder.Build();
        _app.UseWebSockets();
        _app.Run(async context =>
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            _script = script(socket, _lifetime.Token);
            try
            {
                await _script;
            }
            finally
            {
                socket.Abort();
            }
        });
    }

    public static async Task<ScriptedWebSocketServer> CreateAsync(Func<WebSocket, CancellationToken, Task> script)
    {
        var server = new ScriptedWebSocketServer(script);
        try
        {
            await server._app.StartAsync(TestContext.Current.CancellationToken);
            string address = server._app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            server.Url = new UriBuilder(address) { Scheme = "ws" }.Uri;
            return server;
        }
        catch (Exception error)
        {
            try
            {
                await server.DisposeAsync();
            }
            catch (Exception cleanup)
            {
                throw new AggregateException(error, cleanup);
            }
            throw;
        }
    }

    public static Task SendAsync(WebSocket socket, object frame, CancellationToken cancellationToken) =>
        socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(frame), WebSocketMessageType.Text, true, cancellationToken);

    public static async Task<JsonElement> ReceiveAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        byte[] buffer = new byte[8192];
        using var bytes = new MemoryStream();
        ValueWebSocketReceiveResult frame;
        do
        {
            frame = await socket.ReceiveAsync(buffer.AsMemory(), timeout.Token);
            Assert.Equal(WebSocketMessageType.Text, frame.MessageType);
            bytes.Write(buffer, 0, frame.Count);
            Assert.True(bytes.Length <= 1024 * 1024);
        } while (!frame.EndOfMessage);
        using var document = JsonDocument.Parse(bytes.ToArray());
        return document.RootElement.Clone();
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        List<Exception> errors = [];
        try
        {
            await _script;
        }
        catch (OperationCanceledException error) when (error.CancellationToken == _lifetime.Token && _lifetime.IsCancellationRequested)
        {
            // Disposal releases a deliberately held connection; a script's own timeout must still fail the test.
        }
        catch (Exception error)
        {
            errors.Add(error);
        }
        try
        {
            await _app.DisposeAsync();
        }
        catch (Exception error)
        {
            errors.Add(error);
        }
        finally
        {
            _lifetime.Dispose();
        }
        if (errors.Count > 0)
            throw new AggregateException(errors);
    }
}
