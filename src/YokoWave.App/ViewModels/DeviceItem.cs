using System.Windows.Input;
using YokoWave.Core;
using YokoWave.Core.Localization;

namespace YokoWave.App.ViewModels;

public sealed class DeviceItem : ObservableObject
{
    private bool _connected;
    private bool _selected;
    private bool _included = true;
    private bool _canEditRouting = true;
    private double _outputA;
    private double _outputB;
    public DeviceDescriptor Device { get; }
    public string Name => Device.Name;
    public string Id => Device.Id;
    public string Kind => Device.KindLabel;
    public string Battery => $"{Device.Battery}%";
    public string Signal => $"{Device.Signal} dBm";
    public string Number { get; }
    public string Symbol => Device.Kind switch
    { DeviceKind.DualChannel => "∿", DeviceKind.Vibration => "≋", DeviceKind.Pump => "◉", _ => "↕" };
    public bool IsConnected { get => _connected; set { if (Set(ref _connected, value)) { Raise(nameof(Status)); Raise(nameof(ConnectionAction)); } } }
    public bool IsSelected { get => _selected; set => Set(ref _selected, value); }
    public bool IsIncluded { get => _included; set => Set(ref _included, value); }
    public bool CanEditRouting { get => _canEditRouting; set => Set(ref _canEditRouting, value); }
    public string Status => L10n.Get(IsConnected ? "Device.Connected" : "Device.Available");
    public string ConnectionAction => L10n.Get(IsConnected ? "Device.Disconnect" : "Device.Connect");
    public double OutputA { get => _outputA; set { Set(ref _outputA, value); Raise(nameof(OutputALabel)); } }
    public double OutputB { get => _outputB; set { Set(ref _outputB, value); Raise(nameof(OutputBLabel)); } }
    public string OutputALabel => L10n.Format("Device.Output", OutputA);
    public string OutputBLabel => L10n.Format("Device.Output", OutputB);
    public ICommand SelectCommand { get; }
    public ICommand ConnectCommand { get; }

    public DeviceItem(DeviceDescriptor device, int number, Func<DeviceItem, Task> select,
        Func<DeviceItem, Task> connect, Action<Exception> onError)
    {
        Device = device;
        Number = number.ToString("00");
        SelectCommand = new AsyncCommand(() => select(this), onError);
        ConnectCommand = new AsyncCommand(() => connect(this), onError);
    }

    public void RefreshLocalization()
    {
        foreach (var property in new[] { nameof(Kind), nameof(Status), nameof(ConnectionAction), nameof(OutputALabel), nameof(OutputBLabel) }) Raise(property);
    }
}

public sealed class PresetItem(WaveformPreset preset) : ObservableObject
{
    public WaveformPreset Preset => IsBuiltIn ? preset with { Name = Name, Description = Description } : preset;
    public string Name => IsBuiltIn ? L10n.Get($"Wave.{preset.Shape}.Name") : preset.Name;
    public string Description => IsBuiltIn ? L10n.Get($"Wave.{preset.Shape}.Description") : preset.Description;
    public string Detail => L10n.Format("Wave.Detail", preset.PeriodSeconds, preset.Frequency);
    public string ShapeLabel => L10n.Get($"Shape.{preset.Shape}");
    public bool IsBuiltIn => WaveformPreset.BuiltIn.Any(p => p.Id == preset.Id);
    public string Origin => L10n.Get(IsBuiltIn ? "Wave.BuiltIn" : "Wave.CustomOrigin");
    public override string ToString() => Name;
    public void RefreshLocalization()
    {
        foreach (var property in new[] { nameof(Preset), nameof(Name), nameof(Description), nameof(Detail), nameof(ShapeLabel), nameof(Origin) }) Raise(property);
    }
}

public sealed class SessionItem(SessionRecord record) : ObservableObject
{
    public SessionRecord Record { get; } = record;
    public string Name => WaveformPreset.BuiltIn.FirstOrDefault(p => p.Id == Record.WaveformId) is { } builtIn
        ? L10n.Get($"Wave.{builtIn.Shape}.Name") : Record.WaveformName;
    public string Date => Record.StartedAt.ToLocalTime().ToString(L10n.Get("History.DateFormat"), L10n.Current.Culture);
    public string Duration => TimeSpan.FromSeconds(Record.DurationSeconds).ToString(Record.DurationSeconds >= 3600 ? @"h\:mm\:ss" : @"mm\:ss");
    public string Detail => L10n.Format("History.Detail", Record.DeviceCount, Record.PeakA, Record.PeakB, new StoredReason(Record.Reason));
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
