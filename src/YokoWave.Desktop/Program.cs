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
        if (args is ["--audio-probe", var audioPath]) return AudioProbeAsync(audioPath).GetAwaiter().GetResult();
        App.App.BluetoothAdapterFactory = BluetoothAdapterProvider.Create;
        App.App.AudioInputFactory = new Audio.MiniAudioInputFactory();
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static async Task<int> AudioProbeAsync(string path)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await using var input = await new Audio.MiniAudioInputFactory().StartAsync(
                new(Core.AudioSourceKind.MediaFile, path, PlaybackVolume: 0), timeout.Token);
            for (var attempt = 0; attempt < 100 && input.Latest is null; attempt++) await Task.Delay(50, timeout.Token);
            if (input.Latest is not { } frame || Math.Max(frame.LeftDb, frame.RightDb) <= -90)
                throw new InvalidOperationException("The audio probe received no non-silent PCM features.");
            Console.WriteLine($"Local audio decoded: L={frame.LeftDb:0.0} dBFS, R={frame.RightDb:0.0} dBFS; speaker volume=0.");
            input.SetPaused(true);
            if (input.Latest is not null) throw new InvalidOperationException("Pause retained an old audio frame.");
            input.SetPaused(false);
            for (var attempt = 0; attempt < 100 && input.Latest is null; attempt++) await Task.Delay(50, timeout.Token);
            if (input.Latest is not { } resumed || resumed.Sequence <= frame.Sequence || Math.Max(resumed.LeftDb, resumed.RightDb) <= -90)
                throw new InvalidOperationException("Resume did not supply a fresh non-silent audio frame.");
            Console.WriteLine("Audio pause/resume completed; no Bluetooth connection was opened.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
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
