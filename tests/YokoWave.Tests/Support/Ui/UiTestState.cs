using Avalonia;
using Avalonia.Styling;
using Avalonia.Threading;
using YokoWave.Core.Localization;

namespace YokoWave.Tests.Support.Ui;

/// <summary>Restores application globals so UI tests do not depend on class discovery order.</summary>
internal sealed class UiTestState : IDisposable
{
    private readonly Application _application = Application.Current ?? throw new InvalidOperationException("The headless application is not initialized.");
    private readonly string _language = L10n.Current.Language;
    private readonly ThemeVariant? _theme;
    private readonly Dictionary<object, object?> _resources;

    public UiTestState()
    {
        _theme = _application.RequestedThemeVariant;
        _resources = _application.Resources.ToDictionary(entry => entry.Key, entry => entry.Value);
    }

    public void Dispose()
    {
        foreach (object? key in _application.Resources.Keys.Where(key => !_resources.ContainsKey(key)).ToArray())
            _application.Resources.Remove(key);
        foreach (var (key, value) in _resources)
        {
            if (!Equals(_application.Resources[key], value))
                _application.Resources[key] = value;
        }
        _application.RequestedThemeVariant = _theme;
        L10n.Current.SetLanguage(_language);
        Dispatcher.UIThread.RunJobs();
    }
}
