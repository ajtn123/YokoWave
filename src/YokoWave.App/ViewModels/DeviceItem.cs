using System.Windows.Input;
using YokoWave.Core;

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
    public string Status => IsConnected ? "已连接" : "可连接 · 模拟";
    public string ConnectionAction => IsConnected ? "断开" : "连接";
    public double OutputA { get => _outputA; set => Set(ref _outputA, value); }
    public double OutputB { get => _outputB; set => Set(ref _outputB, value); }
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
}

public sealed class PresetItem(WaveformPreset preset) : ObservableObject
{
    public WaveformPreset Preset { get; } = preset;
    public string Name => Preset.Name;
    public string Description => Preset.Description;
    public string Detail => $"{Preset.PeriodSeconds:0.#}s 周期   ·   {Preset.Frequency:0} Hz";
    public string ShapeLabel => Preset.Shape switch
    { WaveShape.Sine => "正弦", WaveShape.Triangle => "三角", WaveShape.Pulse => "脉冲", WaveShape.Ramp => "阶梯", _ => "自定义" };
    public bool IsBuiltIn => WaveformPreset.BuiltIn.Any(p => p.Id == Preset.Id);
    public string Origin => IsBuiltIn ? "内置波形" : "我的波形";
    public override string ToString() => Name;
}

public sealed record SessionItem(SessionRecord Record)
{
    public string Name => Record.WaveformName;
    public string Date => Record.StartedAt.ToLocalTime().ToString("MM.dd  HH:mm");
    public string Duration => TimeSpan.FromSeconds(Record.DurationSeconds).ToString(Record.DurationSeconds >= 3600 ? @"h\:mm\:ss" : @"mm\:ss");
    public string Detail => $"{Record.DeviceCount} 台设备 · A {Record.PeakA:0}% / B {Record.PeakB:0}% · {Record.Reason}";
}

public sealed record ActivityItem(string Time, string Text);
