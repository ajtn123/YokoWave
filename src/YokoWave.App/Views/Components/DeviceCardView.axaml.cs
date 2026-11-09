using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;

namespace YokoWave.App.Views.Components;

public partial class DeviceCardView : UserControl
{
    public static readonly StyledProperty<ICommand?> AssignmentCommandProperty = AvaloniaProperty.Register<DeviceCardView, ICommand?>(nameof(AssignmentCommand));
    public static readonly StyledProperty<string?> AssignmentLabelProperty = AvaloniaProperty.Register<DeviceCardView, string?>(nameof(AssignmentLabel));
    public static readonly StyledProperty<bool> ShowAssignmentActionProperty = AvaloniaProperty.Register<DeviceCardView, bool>(nameof(ShowAssignmentAction));
    public static readonly StyledProperty<bool> ShowConnectionActionProperty = AvaloniaProperty.Register<DeviceCardView, bool>(nameof(ShowConnectionAction), true);
    public ICommand? AssignmentCommand { get => GetValue(AssignmentCommandProperty); set => SetValue(AssignmentCommandProperty, value); }
    public string? AssignmentLabel { get => GetValue(AssignmentLabelProperty); set => SetValue(AssignmentLabelProperty, value); }
    public bool ShowAssignmentAction { get => GetValue(ShowAssignmentActionProperty); set => SetValue(ShowAssignmentActionProperty, value); }
    public bool ShowConnectionAction { get => GetValue(ShowConnectionActionProperty); set => SetValue(ShowConnectionActionProperty, value); }
    public DeviceCardView() => InitializeComponent();
}
