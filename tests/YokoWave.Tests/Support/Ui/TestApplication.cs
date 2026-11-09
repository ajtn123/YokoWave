using Avalonia;
using Avalonia.Headless;

namespace YokoWave.Tests.Support.Ui;

public static class TestApplication
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App.App>().UseHarfBuzz().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
