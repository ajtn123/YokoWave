using YokoWave.Core.Localization;

namespace YokoWave.App.ViewModels;

public sealed class SessionOption(int value, string key, params object?[] arguments) : ObservableObject
{
    public int Value { get; } = value;
    public string Label => arguments.Length == 0 ? L10n.Get(key) : L10n.Format(key, arguments);
    public override string ToString() => Label;
    public void RefreshLocalization() => Raise(nameof(Label));
}
