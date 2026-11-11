using YokoWave.Remote;

namespace YokoWave.Tests.Support.Remote;

internal sealed class RemoteEventTestHost : IAsyncDisposable
{
    public RemoteEventHub Hub { get; } = new();
    public RemoteServer Server { get; }
    private RemoteEventTestHost(int leaseMilliseconds) => Server = new(new RemoteEventSession(Guid.NewGuid(), new(Port: 0, LeaseMilliseconds: leaseMilliseconds), Hub), RemoteTestHost.Token);
    public static async Task<RemoteEventTestHost> CreateAsync(int leaseMilliseconds = 3000)
    {
        var host = new RemoteEventTestHost(leaseMilliseconds);
        try
        {
            await host.Server.StartAsync(cancellationToken: TestContext.Current.CancellationToken);
            return host;
        }
        catch (Exception error)
        {
            try
            {
                await host.DisposeAsync();
            }
            catch (Exception cleanup)
            {
                throw new AggregateException(error, cleanup);
            }
            throw;
        }
    }
    public RemoteEventClient Client(string id, bool simulated = false) => new(Server.Url!.ToString(), RemoteTestHost.Token,
        new(id, [new("collision", RemoteEventKind.Trigger, "Collision", ValidityMilliseconds: 5000), new("score", RemoteEventKind.Number, "Score", 0, 100, 500)], simulated));
    public ValueTask DisposeAsync() => Server.DisposeAsync();
}
