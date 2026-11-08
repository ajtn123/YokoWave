using System.Windows.Input;
using YokoWave.Core;
using YokoWave.Core.Localization;

namespace YokoWave.App.ViewModels;

public sealed class DeviceItem : ObservableObject
{
    private bool _connected;
    private bool _selected;
    private string? _activeModeName;
    private double _outputA;
    private double _outputB;
    private bool _canAssign;
    public DeviceDescriptor Device { get; private set; }
    public string Name => Device.Name;
    public string Id => Device.Id;
    public string Kind => Device.KindLabel;
    public string Battery => Device.Battery is { } battery ? $"{battery}%" : "—";
    public string Signal => Device.Signal is { } signal ? $"{signal} dBm" : "—";
    public string Number { get; }
    public string Symbol => Device.Kind switch
    { DeviceKind.DualChannel => "∿", DeviceKind.Vibration => "≋", DeviceKind.Pump => "◉", DeviceKind.Sensor => "⌖", _ => "↕" };
    public bool IsConnected { get => _connected; set { if (Set(ref _connected, value)) { Raise(nameof(Status)); Raise(nameof(ConnectionAction)); } } }
    public bool IsSelected { get => _selected; set => Set(ref _selected, value); }
    public string? ActiveModeName { get => _activeModeName; set { if (Set(ref _activeModeName, value)) Raise(nameof(Status)); } }
    public string Status => !Device.IsSupported ? L10n.Get("Error.PawFirmware") : ActiveModeName is { } name ? L10n.Format("Device.InMode", name) : L10n.Get(IsConnected ? "Device.Connected" : "Device.Available");
    public bool IsOutput => !Device.IsInput;
    public string ConnectionAction => L10n.Get(IsConnected ? "Device.Disconnect" : "Device.Connect");
    public double OutputA { get => _outputA; set { Set(ref _outputA, value); Raise(nameof(OutputALabel)); } }
    public double OutputB { get => _outputB; set { Set(ref _outputB, value); Raise(nameof(OutputBLabel)); } }
    public string OutputALabel => L10n.Format("Device.Output", OutputA);
    public string OutputBLabel => L10n.Format("Device.Output", OutputB);
    public ICommand SelectCommand { get; }
    public ICommand ConnectCommand { get; }
    public AsyncCommand AssignCommand { get; }
    public bool CanAssign { get => _canAssign; internal set { if (Set(ref _canAssign, value)) AssignCommand.NotifyCanExecuteChanged(); } }

    public DeviceItem(DeviceDescriptor device, int number, Func<DeviceItem, Task> select,
        Func<DeviceItem, Task> connect, Action<Exception> onError, Action<DeviceItem>? assign = null)
    {
        Device = device;
        Number = number.ToString("00");
        SelectCommand = new AsyncCommand(() => select(this), onError);
        ConnectCommand = new AsyncCommand(() => connect(this), onError, () => Device.IsSupported);
        AssignCommand = new(() => { assign?.Invoke(this); return Task.CompletedTask; }, onError, () => CanAssign && assign is not null);
    }

    public void RefreshLocalization()
    {
        foreach (var property in new[] { nameof(Kind), nameof(Status), nameof(ConnectionAction), nameof(OutputALabel), nameof(OutputBLabel) }) Raise(property);
    }

    public void UpdateReadings(DeviceDescriptor device)
    {
        if (Device == device) return;
        Device = device;
        Raise(nameof(Battery)); Raise(nameof(Signal));
    }
}

public sealed class PresetItem(WaveformPreset preset) : ObservableObject
{
    public WaveformPreset Preset => IsBuiltIn ? preset with { Name = Name } : preset;
    public string Name => IsBuiltIn ? DefaultWaveformCatalog.Name(preset.Id) : preset.Name;
    public string Detail => preset.Shape == WaveShape.Sampled
        ? L10n.Format("Wave.SampledDetail", preset.Samples?.Length ?? 0, preset.PeriodSeconds, L10n.Get($"Wave.Family{preset.Family}"))
        : L10n.Format("Wave.Detail", preset.PeriodSeconds, preset.Frequency);
    public string ShapeLabel => L10n.Get($"Shape.{preset.Shape}");
    public bool IsBuiltIn => DefaultWaveformCatalog.Contains(preset.Id);
    public string Origin => L10n.Get(IsBuiltIn ? "Wave.BuiltIn" : "Wave.CustomOrigin");
    public override string ToString() => Name;
    public void RefreshLocalization()
    {
        foreach (var property in new[] { nameof(Preset), nameof(Name), nameof(Detail), nameof(ShapeLabel), nameof(Origin) }) Raise(property);
    }
}

public sealed class SessionItem(SessionRecord record) : ObservableObject
{
    public SessionRecord Record { get; } = record;
    public string Name
    {
        get
        {
            if (Record.Kind is ModeKind.Audio or ModeKind.Restraint or ModeKind.Pump) return L10n.Get($"ModeKind.{Record.Kind}");
            if (Record.TouchControl) return L10n.Get("Touch.Title");
            var a = Record.WaveformId is { } id && DefaultWaveformCatalog.Contains(id) ? DefaultWaveformCatalog.Name(id) : Record.WaveformName;
            if (Record.ChannelBWaveformName is not { } name) return a;
            var b = Record.ChannelBWaveformId is { } bId && DefaultWaveformCatalog.Contains(bId) ? DefaultWaveformCatalog.Name(bId) : name;
            return L10n.Format("History.ChannelWaves", a, b);
        }
    }
    public string Date => Record.StartedAt.ToLocalTime().ToString(L10n.Get("History.DateFormat"), L10n.Current.Culture);
    public string Duration => TimeSpan.FromSeconds(Record.DurationSeconds).ToString(Record.DurationSeconds >= 3600 ? @"h\:mm\:ss" : @"mm\:ss");
    public string Detail
    {
        get
        {
            var detail = L10n.Format("History.Detail", Record.DeviceCount, Record.PeakA, Record.PeakB, new StoredReason(Record.Reason));
            if (Record.PeakC > 0) detail = L10n.Format("History.WithChannelC", detail, Record.PeakC);
            return Record.ModeName is { } mode ? L10n.Format("History.ModeDetail", mode, detail) : detail;
        }
    }
    public void RefreshLocalization()
    {
        Raise(nameof(Name)); Raise(nameof(Date)); Raise(nameof(Detail));
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
