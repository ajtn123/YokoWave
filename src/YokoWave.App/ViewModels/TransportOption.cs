using YokoWave.Core.Localization;

namespace YokoWave.App.ViewModels;

public sealed class TransportOption(bool useBluetooth) : ObservableObject
{
    public bool UseBluetooth { get; } = useBluetooth;
    public string Label => L10n.Get(UseBluetooth ? "Mode.Bluetooth" : "Mode.Simulation");
    public void RefreshLocalization() => Raise(nameof(Label));
}
