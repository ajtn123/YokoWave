using System.Windows.Input;
using YokoWave.Core.Localization;
using YokoWave.Pipelines;

namespace YokoWave.App.ViewModels.Pipelines;

public sealed class PipelinePaletteItem(NodeRegistration registration, Action add) : ObservableObject
{
    public string Title => L10n.Get(registration.NameKey);
    public ICommand AddCommand { get; } = new ActionCommand(add);
    public void RefreshLocalization() => Raise(nameof(Title));
}
