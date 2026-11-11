using YokoWave.Core;
using YokoWave.Core.Bluetooth;
using YokoWave.Remote;
using YokoWave.Tests.Support.Bluetooth;

namespace YokoWave.Tests.Support.Remote;

internal sealed class RemoteTestHost : IAsyncDisposable
{
    public const string Token = "test-only-websocket-token-123456789";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    public required HardwareGattAdapter Adapter { get; init; }
    public required DeviceController Controller { get; init; }
    public required DeviceDescriptor[] Devices { get; init; }
    public required RemoteServer Server { get; init; }

    public static Task<RemoteTestHost> CreateAsync(params BluetoothProtocol[] protocols) => CreateWithMtuAsync(20, protocols);
    public static async Task<RemoteTestHost> CreateWithMtuAsync(int maximumWriteLength, params BluetoothProtocol[] protocols)
    {
        var adapter = new HardwareGattAdapter(protocols)
        {
            MaximumWriteLength = maximumWriteLength
        };
        var controller = new DeviceController(new BluetoothDeviceTransport(adapter));
        RemoteServer? server = null;
        try
        {
            var devices = (await controller.ScanAsync(Ct)).ToArray();
            foreach (var device in devices)
                await controller.ConnectAsync(device, Ct);
            server = new(new RemoteExportSession(Guid.NewGuid(), controller, devices.Select(device => device.Id).ToArray(),
                new(Port: 0, LeaseMilliseconds: 1000, ControlLimit: 40)), Token);
            await server.StartAsync(cancellationToken: Ct);
            return new() { Adapter = adapter, Controller = controller, Devices = devices, Server = server };
        }
        catch (Exception error)
        {
            var errors = await CloseAsync(server, controller);
            if (errors.Count > 0)
                throw new AggregateException([error, .. errors]);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        var errors = await CloseAsync(Server, Controller);
        if (errors.Count > 0)
            throw new AggregateException(errors);
    }

    private static async Task<List<Exception>> CloseAsync(RemoteServer? server, DeviceController controller)
    {
        List<Exception> errors = [];
        try
        {
            if (server is not null)
                await server.DisposeAsync();
        }
        catch (Exception error)
        {
            errors.Add(error);
        }
        try
        {
            await controller.DisposeAsync();
        }
        catch (Exception error)
        {
            errors.Add(error);
        }
        return errors;
    }
}
