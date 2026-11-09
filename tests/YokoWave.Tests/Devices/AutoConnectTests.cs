using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using YokoWave.App.ViewModels;
using YokoWave.Core;
using YokoWave.Tests.Support.Ui;

namespace YokoWave.Tests.Devices;

public sealed class AutoConnectTests
{
    [Fact]
    public async Task OnlyRememberedDevicesReconnectAndEveryConnectionStartsAtZero()
    {
        var transport = new RecordingTransport();
        await using var controller = new DeviceController(transport);
        List<string> prepared = [];
        List<string> connected = [];
        List<Exception> errors = [];
        var auto = new DeviceAutoConnector(controller, id =>
        {
            prepared.Add(id);
            return Task.CompletedTask;
        }, device => connected.Add(device.Id), errors.Add);
        auto.Load(false, ["known"]);
        await auto.ConnectAvailableAsync([Device("known"), Device("other")], Ct);
        Assert.Empty(controller.Connections);
        auto.Enabled = true;
        await auto.ConnectAvailableAsync([Device("known"), Device("other")], Ct);
        Assert.Equal("known", Assert.Single(controller.Connections).Key);
        Assert.Equal(OutputFrame.Zero, controller.Connections["known"].Output);
        Assert.False(auto.NeedsDiscovery);
        await controller.DisconnectAsync("known", Ct);
        Assert.True(auto.NeedsDiscovery);
        await auto.ConnectAvailableAsync([Device("known")], Ct);
        Assert.Equal(new[] { "known", "known" }, prepared);
        Assert.Equal(prepared, connected);
        auto.Forget("known");
        await controller.DisconnectAsync("known", Ct);
        await auto.ConnectAvailableAsync([Device("known")], Ct);
        Assert.Empty(controller.Connections);
        Assert.False(auto.NeedsDiscovery);
        Assert.Empty(errors);
        Assert.All(transport.Events.Where(e => e.StartsWith("send:")), e => Assert.EndsWith(":0:0", e));
    }

    [Fact]
    public async Task FailureDoesNotBlockOtherDevicesAndBackoffDoesNotRepeatErrorNotifications()
    {
        var clock = new ManualInputClock();
        var transport = new RecordingTransport
        {
            FailId = "bad"
        };
        await using var controller = new DeviceController(transport, clock);
        List<Exception> errors = [];
        var auto = new DeviceAutoConnector(controller, _ => Task.CompletedTask, _ => { }, errors.Add);
        auto.Load(true, ["bad", "good"]);
        var devices = new[]
        {
            Device("bad"),
            Device("good")
        };
        await auto.ConnectAvailableAsync(devices, Ct);
        Assert.Equal("good", Assert.Single(controller.Connections).Key);
        Assert.Single(errors);
        Assert.Single(transport.Events, e => e == "disconnect:bad");
        await auto.ConnectAvailableAsync(devices, Ct);
        Assert.Single(transport.Events, e => e == "disconnect:bad");
        clock.Advance(5);
        await auto.ConnectAvailableAsync(devices, Ct);
        Assert.Equal(2, transport.Events.Count(e => e == "disconnect:bad"));
        Assert.Single(errors);
        transport.FailId = null;
        clock.Advance(9);
        await auto.ConnectAvailableAsync(devices, Ct);
        Assert.False(controller.Connections.ContainsKey("bad"));
        clock.Advance(1);
        await auto.ConnectAvailableAsync(devices, Ct);
        Assert.Equal(2, controller.Connections.Count);
        Assert.All(controller.Connections.Values, connection => Assert.Equal(OutputFrame.Zero, connection.Output));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangedIntentDuringConnectionRollsBackTheLink(bool forget)
    {
        var transport = new PendingConnectionTransport();
        await using var controller = new DeviceController(transport);
        List<Exception> errors = [];
        var auto = new DeviceAutoConnector(controller, _ => Task.CompletedTask, _ => Assert.Fail("Cancelled connection was announced."), errors.Add);
        auto.Load(true, ["known"]);
        var connect = auto.ConnectAvailableAsync([Device("known")], Ct);
        await transport.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        if (forget)
            auto.Forget("known");
        else
            auto.Enabled = false;
        transport.Continue.SetResult();
        await connect;
        Assert.Empty(controller.Connections);
        Assert.True(transport.Disconnected);
        Assert.Empty(errors);
    }

    [Fact]
    public async Task CancellationDrainsAnInFlightConnectionWithoutNotifyingOrLeakingIt()
    {
        var transport = new PendingConnectionTransport();
        await using var controller = new DeviceController(transport);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var auto = new DeviceAutoConnector(controller, _ => Task.CompletedTask, _ => Assert.Fail("Cancelled connection was announced."), _ => Assert.Fail("Cancellation was reported as an error."));
        auto.Load(true, ["known"]);
        var connect = auto.ConnectAvailableAsync([Device("known")], cancel.Token);
        await transport.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        await cancel.CancelAsync();
        transport.Continue.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connect);
        Assert.Empty(controller.Connections);
        Assert.True(transport.Disconnected);
    }

    [AvaloniaFact]
    public async Task PreferencesSurviveShutdownAndStartupConnectsWithoutStartingPipelines()
    {
        using var state = new UiTestState();
        using var directory = new TestFolder();
        string id;
        await using (var vm = new MainViewModel(new DeviceController(new SimulationTransport()), new(directory.Path)))
        {
            await vm.InitializeAsync();
            var device = vm.Devices.Devices.First(d => d.Device.Kind == DeviceKind.DualChannel);
            id = device.Id;
            await vm.Devices.ToggleConnectionAsync(device);
            vm.Devices.AutoConnect = true;
            await vm.SaveAsync();
        }

        var saved = await new WorkspaceStore(directory.Path).LoadAsync();
        Assert.True(saved.Settings.AutoConnect);
        Assert.Equal(id, Assert.Single(saved.Settings.AutoConnectDevices!));
        var controller = new DeviceController(new SimulationTransport());
        await using (var vm = new MainViewModel(controller, new(directory.Path)))
        {
            await vm.InitializeAsync();
            Dispatcher.UIThread.RunJobs();
            Assert.True(vm.Devices.AutoConnect);
            Assert.Equal(id, Assert.Single(controller.Connections).Key);
            Assert.Equal(OutputFrame.Zero, controller.Connections[id].Output);
            Assert.All(vm.Pipelines.Pipelines, p => Assert.False(p.Session.HoldsResources));
            var device = vm.Devices.Devices.Single(d => d.Id == id);
            await vm.Devices.ToggleConnectionAsync(device);
            await vm.Devices.ScanAsync();
            Assert.Empty(controller.Connections);
            Assert.Empty(vm.Devices.AutoConnectDevices);
        }

        saved = await new WorkspaceStore(directory.Path).LoadAsync();
        Assert.True(saved.Settings.AutoConnect);
        Assert.Empty(saved.Settings.AutoConnectDevices!);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DeviceDescriptor Device(string id) => new(id, id, DeviceKind.DualChannel, 80, -40);
    private sealed class PendingConnectionTransport : IDeviceTransport
    {
        public string DisplayName => "pending";
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disconnected { get; private set; }

        public Task<IReadOnlyList<DeviceDescriptor>> ScanAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<DeviceDescriptor>>([]);
        public async Task ConnectAsync(DeviceDescriptor device, CancellationToken cancellationToken = default)
        {
            Started.SetResult();
            await Continue.Task;
        }

        public Task SendAsync(string deviceId, OutputFrame frame, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(OutputFrame.Zero, frame);
            return Task.CompletedTask;
        }

        public Task DisconnectAsync(string deviceId, CancellationToken cancellationToken = default)
        {
            Disconnected = true;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
