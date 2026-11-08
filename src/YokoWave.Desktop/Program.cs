using Avalonia;
using YokoWave.Desktop.Bluetooth;
using YokoWave.Core.Bluetooth;

namespace YokoWave.Desktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--bluetooth-probe"))
        {
            var audioIndex = Array.IndexOf(args, "--zero-audio");
            var probeAudioPath = audioIndex >= 0 && audioIndex + 1 < args.Length ? args[audioIndex + 1] : null;
            var profileIndex = Array.IndexOf(args, "--profile");
            var profile = profileIndex >= 0 && profileIndex + 1 < args.Length ? args[profileIndex + 1] : null;
            return ProbeAsync(args.Contains("--connect"), probeAudioPath, profile).GetAwaiter().GetResult();
        }
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

    private static async Task<int> ProbeAsync(bool connect, string? audioPath, string? profile)
    {
        try
        {
#if WINDOWS
            var adapter = new WindowsBluetoothAdapter { Diagnostic = Console.WriteLine };
#else
            var adapter = BluetoothAdapterProvider.Create();
#endif
            await using var transport = new BluetoothDeviceTransport(adapter);
            transport.StatusChanged += (_, update) => Console.WriteLine($"Status: battery={update.Battery}, A={update.ContactA}, B={update.ContactB}, outputA={update.OutputAEnabled}, outputB={update.OutputBEnabled}");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
            var devices = (await transport.ScanAsync(timeout.Token)).Where(device => profile is null || string.Equals(device.ProtocolId, profile, StringComparison.OrdinalIgnoreCase)).ToArray();
            Console.WriteLine($"Bluetooth devices found: {devices.Length}");
            foreach (var device in devices) Console.WriteLine($"{device.Name} {device.Id} profile={device.ProtocolId} RSSI={device.Signal}");
            if (connect && devices.Length != 1)
            {
                Console.Error.WriteLine("Connect probe requires exactly one matching device. Use the application to select a device.");
                return 2;
            }
            if (connect && devices.Length == 1)
            {
                await using var controller = new Core.DeviceController(transport);
                await controller.ConnectAsync(devices[0], timeout.Token);
                Console.WriteLine("Connected; protocol initialization completed. No nonzero output requested.");
                if (audioPath is not null)
                {
                    for (var cycle = 0; cycle < 5; cycle++)
                    {
                        await using var input = await new Audio.MiniAudioInputFactory().StartAsync(
                            new(Core.AudioSourceKind.MediaFile, audioPath, PlaybackVolume: 0), timeout.Token);
                        var engine = new Core.SessionEngine(controller) { ChannelA = 0, ChannelB = 0 };
                        try
                        {
                            await engine.StartAsync([devices[0].Id], Core.DefaultWaveformCatalog.Presets[0], TimeSpan.FromSeconds(2), new(Kind: Core.ModeKind.Audio));
                            for (var tick = 1; tick <= 5; tick++)
                            {
                                await Task.Delay(100, timeout.Token);
                                await engine.SetAudioFrameAsync(input.Latest);
                                await engine.AdvanceAsync(TimeSpan.FromMilliseconds(tick * 100));
                            }
                        }
                        finally { await engine.StopAsync(); }
                        Console.WriteLine($"Zero-strength audio session {cycle + 1}: started and stopped; no nonzero output sent.");
                    }
                }
                await Task.Delay(1500, timeout.Token);
                await controller.DisconnectAsync(devices[0].Id, timeout.Token);
            }
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            foreach (System.Collections.DictionaryEntry detail in error.Data) Console.Error.WriteLine($"{detail.Key}: {detail.Value}");
            return 1;
        }
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App.App>()
        .UsePlatformDetect()
        .LogToTrace();
}
