using YokoWave.Core;
using YokoWave.Core.Localization;

namespace YokoWave.App.ViewModels;

public sealed class ShapeItem(WaveShape shape) : ObservableObject
{
    public WaveShape Shape { get; } = shape;
    public string Label => L10n.Get($"Shape.{Shape}");
    public void RefreshLocalization() => Raise(nameof(Label));
}
