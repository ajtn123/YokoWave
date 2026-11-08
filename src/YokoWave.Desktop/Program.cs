using Avalonia;
using YokoWave.Desktop.Bluetooth;
using YokoWave.Desktop.Diagnostics;

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
            return DiagnosticProbes.RunBluetoothAsync(args.Contains("--connect"), probeAudioPath, profile).GetAwaiter().GetResult();
        }
        if (args is ["--audio-probe", var audioPath])
            return DiagnosticProbes.RunAudioAsync(audioPath).GetAwaiter().GetResult();
        App.App.BluetoothAdapterFactory = BluetoothAdapterProvider.Create;
        App.App.AudioInputFactory = new Audio.MiniAudioInputFactory();
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App.App>()
        .UsePlatformDetect()
        .LogToTrace();
}
