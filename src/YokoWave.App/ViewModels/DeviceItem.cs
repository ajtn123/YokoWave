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
        get => _connected; set
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
        get => _activePipelineName; set
        {
            if (Set(ref _activePipelineName, value))
                Raise(nameof(Status));
        }
    }
    public string Status => !Device.IsSupported ? L10n.Get("Error.PawFirmware") : ActivePipelineName is { } name ? L10n.Format("Device.InMode", name) : L10n.Get(IsConnected ? "Device.Connected" : "Device.Available");
    public string ConnectionAction => L10n.Get(IsConnected ? "Device.Disconnect" : "Device.Connect");
    public double OutputA
    {
        get => _outputA; set
        {
            Set(ref _outputA, value);
            Raise(nameof(OutputALabel));
            Raise(nameof(OutputSummary));
        }
    }
    public double OutputB
    {
        get => _outputB; set
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
        foreach (var property in new[] { nameof(Kind), nameof(Status), nameof(ConnectionAction), nameof(OutputALabel), nameof(OutputBLabel), nameof(OutputSummary) })
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

public sealed class PresetItem(WaveformPreset preset) : ObservableObject
{
    public WaveformPreset Preset => IsBuiltIn ? preset with { Name = Name } : preset;
    public string Name => IsBuiltIn ? DefaultWaveformCatalog.Name(preset.Id) : preset.Name;
    public string Detail => preset.Shape == WaveShape.Sampled
        ? L10n.Format("Wave.SampledDetail", preset.Samples?.Length ?? 0, preset.PeriodSeconds)
        : L10n.Format("Wave.Detail", preset.PeriodSeconds, preset.Frequency);
    public string ShapeLabel => L10n.Get($"Shape.{preset.Shape}");
    public bool IsBuiltIn => DefaultWaveformCatalog.Contains(preset.Id);
    public string Origin => L10n.Get(IsBuiltIn ? "Wave.BuiltIn" : "Wave.CustomOrigin");
    public override string ToString() => Name;
    public void RefreshLocalization()
    {
        foreach (var property in new[] { nameof(Preset), nameof(Name), nameof(Detail), nameof(ShapeLabel), nameof(Origin) })
            Raise(property);
    }
}

public sealed class SessionItem(SessionRecord record) : ObservableObject
{
    public SessionRecord Record { get; } = record;
    public string Name => Record.PipelineName;
    public string Date => Record.StartedAt.ToLocalTime().ToString(L10n.Get("History.DateFormat"), L10n.Current.Culture);
    public string Duration => TimeSpan.FromSeconds(Record.DurationSeconds).ToString(Record.DurationSeconds >= 3600 ? @"h\:mm\:ss" : @"mm\:ss");
    public string Detail
    {
        get
        {
            string Peak(double? value) => value is { } number ? number.ToString("0.#", L10n.Current.Culture) + "%" : L10n.Get("History.UnknownOutput");
            var detail = L10n.Format("History.PipelineDetail", Record.DeviceCount, Peak(Record.PeakA), Peak(Record.PeakB), Peak(Record.PeakC), new StoredReason(Record.Reason));
            var waves = (Record.Contents ?? []).Where(c => c.WaveformName is not null).Select(c => c.WaveformId is { } id && DefaultWaveformCatalog.Contains(id)
                ? DefaultWaveformCatalog.Name(id) : c.WaveformName!).Distinct().ToArray();
            return waves.Length > 0 ? L10n.Format("History.PipelineWaves", detail, string.Join(", ", waves)) : detail;
        }
    }
    public void RefreshLocalization()
    {
        Raise(nameof(Name));
        Raise(nameof(Date));
        Raise(nameof(Detail));
    }
}

public sealed class ActivityItem(string time, LocalizedMessage message) : ObservableObject
{
    public string Time => time;
    public string Text => message.Value;
    public void RefreshLocalization() => Raise(nameof(Text));
}

public sealed record StoredReason(string Code)
{
    public override string ToString() => L10n.Current.TranslateStored(Code, "Reason.");
}

public sealed class ShapeItem(WaveShape shape) : ObservableObject
{
    public WaveShape Shape { get; } = shape;
    public string Label => L10n.Get($"Shape.{Shape}");
    public void RefreshLocalization() => Raise(nameof(Label));
}

public sealed record LanguageOption(string Code, string DisplayName);

public sealed class TransportOption(bool useBluetooth) : ObservableObject
{
    public bool UseBluetooth { get; } = useBluetooth;
    public string Label => L10n.Get(UseBluetooth ? "Mode.Bluetooth" : "Mode.Simulation");
    public void RefreshLocalization() => Raise(nameof(Label));
}
