using System.Windows.Input;
using YokoWave.Core;
using YokoWave.Core.Localization;

namespace YokoWave.App.ViewModels;

public sealed class DeviceItem : ObservableObject
{
    private bool _connected;
    private bool _selected;
    private string? _activePipelineName;
    private double _outputA;
    private double _outputB;
    private double _outputC;
    public DeviceDescriptor Device { get; private set; }
    public DeviceHardwareOptionsViewModel? HardwareOptions { get; init; }
    public string Name => Device.Name;
    public string Id => Device.Id;
    public string Kind => Device.KindLabel;
    public string Battery => Device.Battery is { } battery ? $"{battery}%" : "—";
    public string Signal => Device.Signal is { } signal ? $"{signal} dBm" : "—";
    public string Number { get; }
    public string Symbol => Device.Kind switch
    {
        DeviceKind.DualChannel => "∿",
        DeviceKind.Vibration => "≋",
        DeviceKind.Pump => "◉",
        DeviceKind.Sensor => "⌖",
        _ => "↕"
    };
    public bool IsConnected
    {
        get => _connected;
        set
        {
            if (Set(ref _connected, value))
            {
                Raise(nameof(Status));
                Raise(nameof(ConnectionAction));
                Raise(nameof(HasOutput));
            }
        }
    }
    public bool IsSelected { get => _selected; set => Set(ref _selected, value); }
    public string? ActivePipelineName
    {
        get => _activePipelineName;
        set
        {
            if (Set(ref _activePipelineName, value))
                Raise(nameof(Status));
        }
    }
    public string Status => !Device.IsSupported ? L10n.Get("Error.PawFirmware") : ActivePipelineName is { } name ? L10n.Format("Device.InMode", name) : L10n.Get(IsConnected ? "Device.Connected" : "Device.Available");
    public string ConnectionAction => L10n.Get(IsConnected ? "Device.Disconnect" : "Device.Connect");
    public double OutputA
    {
        get => _outputA;
        set
        {
            Set(ref _outputA, value);
            Raise(nameof(OutputALabel));
            Raise(nameof(OutputSummary));
        }
    }
    public double OutputB
    {
        get => _outputB;
        set
        {
            Set(ref _outputB, value);
            Raise(nameof(OutputBLabel));
            Raise(nameof(OutputSummary));
        }
    }
    public string OutputALabel => L10n.Format("Device.Output", OutputA);
    public string OutputBLabel => L10n.Format("Device.Output", OutputB);
    public double OutputC
    {
        get => _outputC;
        set { if (Set(ref _outputC, value)) Raise(nameof(OutputSummary)); }
    }
    public bool HasOutput => IsConnected && !Device.IsInput && Device.Kind != DeviceKind.Pump;
    public string OutputSummary => L10n.Format("Device.OutputSummary", string.Join(" · ", Enumerable.Range(0, 3).Where(Device.HasChannel)
        .Select(channel => L10n.Format("Device.ChannelOutput", L10n.Get("Channel." + (char)('A' + channel)), new[] { OutputA, OutputB, OutputC }[channel]))));
    public ICommand SelectCommand { get; }
    public ICommand ConnectCommand { get; }
    public DeviceItem(DeviceDescriptor device, int number, Func<DeviceItem, Task> select,
        Func<DeviceItem, Task> connect, Action<Exception> onError)
    {
        Device = device;
        Number = number.ToString("00");
        SelectCommand = new AsyncCommand(() => select(this), onError);
        ConnectCommand = new AsyncCommand(() => connect(this), onError, () => Device.IsSupported);
    }

    public void RefreshLocalization()
    {
        foreach (string? property in new[] { nameof(Kind), nameof(Status), nameof(ConnectionAction), nameof(OutputALabel), nameof(OutputBLabel), nameof(OutputSummary) })
            Raise(property);
    }

    public void UpdateReadings(DeviceDescriptor device)
    {
        if (Device == device)
            return;
        Device = device;
        Raise(nameof(Battery));
        Raise(nameof(Signal));
    }
}
