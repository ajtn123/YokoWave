using Avalonia;
using YokoWave.Desktop.Bluetooth;
using YokoWave.Core.Bluetooth;

namespace YokoWave.Desktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--bluetooth-probe")) return ProbeAsync(args.Contains("--connect")).GetAwaiter().GetResult();
        App.App.BluetoothAdapterFactory = BluetoothAdapterProvider.Create;
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static async Task<int> ProbeAsync(bool connect)
    {
        try
        {
#if WINDOWS
            var adapter = new WindowsBluetoothAdapter { Diagnostic = Console.WriteLine };
#else
            var adapter = BluetoothAdapterProvider.Create();
#endif
            await using var transport = new ElectricalBluetoothTransport(adapter);
            transport.StatusChanged += (_, update) => Console.WriteLine($"Status: battery={update.Battery}, A={update.ContactA}, B={update.ContactB}, outputA={update.OutputAEnabled}, outputB={update.OutputBEnabled}");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
            var devices = await transport.ScanAsync(timeout.Token);
            Console.WriteLine($"Electrical devices found: {devices.Count}");
            foreach (var device in devices) Console.WriteLine($"{device.Name} {device.Id} RSSI={device.Signal}");
            if (connect && devices.Count != 1)
            {
                Console.Error.WriteLine("Connect probe requires exactly one matching device. Use the application to select a device.");
                return 2;
            }
            if (connect && devices.Count == 1)
            {
                await transport.ConnectAsync(devices[0], timeout.Token);
                Console.WriteLine("Connected; device confirmed both outputs disabled.");
                await Task.Delay(1500, timeout.Token);
                await transport.DisconnectAsync(devices[0].Id, timeout.Token);
            }
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.Message);
            foreach (System.Collections.DictionaryEntry detail in error.Data) Console.Error.WriteLine($"{detail.Key}: {detail.Value}");
            return 1;
        }
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App.App>()
        .UsePlatformDetect()
        .LogToTrace();
}
