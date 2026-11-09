using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;

namespace YokoWave.App.Views.Components;

public sealed partial class CollectionActionsView : UserControl
{
    public static readonly StyledProperty<ICommand?> AddCommandProperty = AvaloniaProperty.Register<CollectionActionsView, ICommand?>(nameof(AddCommand));
    public static readonly StyledProperty<ICommand?> RemoveCommandProperty = AvaloniaProperty.Register<CollectionActionsView, ICommand?>(nameof(RemoveCommand));
    public static readonly StyledProperty<ICommand?> MoveUpCommandProperty = AvaloniaProperty.Register<CollectionActionsView, ICommand?>(nameof(MoveUpCommand));
    public static readonly StyledProperty<ICommand?> MoveDownCommandProperty = AvaloniaProperty.Register<CollectionActionsView, ICommand?>(nameof(MoveDownCommand));
    public static readonly StyledProperty<string?> AddDescriptionProperty = AvaloniaProperty.Register<CollectionActionsView, string?>(nameof(AddDescription));
    public static readonly StyledProperty<string?> RemoveDescriptionProperty = AvaloniaProperty.Register<CollectionActionsView, string?>(nameof(RemoveDescription));
    public ICommand? AddCommand { get => GetValue(AddCommandProperty); set => SetValue(AddCommandProperty, value); }
    public ICommand? RemoveCommand { get => GetValue(RemoveCommandProperty); set => SetValue(RemoveCommandProperty, value); }
    public ICommand? MoveUpCommand { get => GetValue(MoveUpCommandProperty); set => SetValue(MoveUpCommandProperty, value); }
    public ICommand? MoveDownCommand { get => GetValue(MoveDownCommandProperty); set => SetValue(MoveDownCommandProperty, value); }
    public string? AddDescription { get => GetValue(AddDescriptionProperty); set => SetValue(AddDescriptionProperty, value); }
    public string? RemoveDescription { get => GetValue(RemoveDescriptionProperty); set => SetValue(RemoveDescriptionProperty, value); }
    public CollectionActionsView() => InitializeComponent();
}
