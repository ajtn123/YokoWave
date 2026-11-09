using YokoWave.Core.Localization;

namespace YokoWave.App.ViewModels;

public sealed class ActivityItem(string time, LocalizedMessage message) : ObservableObject
{
    public string Time => time;
    public string Text => message.Value;
    public void RefreshLocalization() => Raise(nameof(Text));
}
