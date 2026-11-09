using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia;
using Avalonia.Input;
using YokoWave.App.ViewModels.Pipelines;
using YokoWave.Core.Localization;

namespace YokoWave.App.Views.Pipelines;

public sealed partial class PipelineEditorView : UserControl
{
    private Point _menuLocation;
    private Point? _rightPress;
    private bool _rightDragged;
    public PipelineEditorView()
    {
        InitializeComponent();
        var menu = new ContextMenu();
        void PopulateMenu()
        {
            if (DataContext is not PipelineEditorViewModel editor)
                return;
            _menuLocation = Canvas.MouseLocation;
            var nodes = new MenuItem { Header = L10n.Get("Pipeline.AddNode"), IsEnabled = editor.CanEdit };
            var fit = new MenuItem { Header = L10n.Get("Pipeline.Fit") };
            fit.Click += FitGraph;
            nodes.ItemsSource = editor.Registry.Types.Select(type =>
            {
                var item = new MenuItem { Header = L10n.Get(type.NameKey) };
                item.Click += (_, _) =>
                {
                    try
                    { editor.AddNode(type.Type, _menuLocation); }
                    catch (Exception error) { editor.ReportError(error); }
                };
                return item;
            }).ToArray();
            menu.ItemsSource = new object[]
            {
                nodes, fit, new Separator(),
                new MenuItem { Header = L10n.Get("Pipeline.Undo"), Command = editor.UndoCommand, IsEnabled = editor.CanUndo },
                new MenuItem { Header = L10n.Get("Pipeline.Redo"), Command = editor.RedoCommand, IsEnabled = editor.CanRedo },
                new MenuItem { Header = L10n.Get("Pipeline.Copy"), Command = editor.CopyCommand },
                new MenuItem { Header = L10n.Get("Pipeline.Paste"), Command = editor.PasteCommand, IsEnabled = editor.CanEdit },
                new MenuItem { Header = L10n.Get("Pipeline.Delete"), Command = editor.DeleteSelectionCommand }
            };
        }
        menu.Opening += (_, _) => PopulateMenu();
        DataContextChanged += (_, _) => PopulateMenu();
        Canvas.ContextMenu = menu;
        Canvas.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (!e.GetCurrentPoint(Canvas).Properties.IsRightButtonPressed)
                return;
            _rightPress = e.GetPosition(Canvas);
            _rightDragged = false;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        Canvas.AddHandler(PointerMovedEvent, (_, e) =>
        {
            if (_rightPress is not { } start)
                return;
            var tolerance = Application.Current?.PlatformSettings?.GetTapSize(e.Pointer.Type) ?? new Size(4, 4);
            var distance = e.GetPosition(Canvas) - start;
            _rightDragged |= Math.Abs(distance.X) > tolerance.Width || Math.Abs(distance.Y) > tolerance.Height;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        Canvas.AddHandler(PointerReleasedEvent, (_, e) =>
        {
            if (e.InitialPressMouseButton != MouseButton.Right || _rightPress is null)
                return;
            _rightPress = null;
            if (_rightDragged || menu.IsOpen)
                return;
            PopulateMenu();
            menu.Open(Canvas);
            e.Handled = true;
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        PopulateMenu();
    }
    private void FitGraph(object? sender, RoutedEventArgs e) => Canvas.FitToScreen();
}
