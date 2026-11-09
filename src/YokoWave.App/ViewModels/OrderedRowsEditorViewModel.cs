using System.Collections.ObjectModel;

namespace YokoWave.App.ViewModels;

public class OrderedRowsEditorViewModel<TRow> : ObservableObject where TRow : class
{
    private TRow? _selected;
    public ObservableCollection<TRow> Rows { get; } = [];
    public TRow? Selected
    {
        get => _selected;
        set
        {
            if (Set(ref _selected, value))
                RefreshCommands();
        }
    }
    public ActionCommand AddCommand { get; }
    public ActionCommand RemoveCommand { get; }
    public ActionCommand MoveUpCommand { get; }
    public ActionCommand MoveDownCommand { get; }
    protected OrderedRowsEditorViewModel(IEnumerable<TRow> values, Func<TRow> create, int minimum, int maximum, Func<bool> canEdit, Action changed)
    {
        foreach (var value in values)
            Rows.Add(value);
        AddCommand = new ActionCommand(() =>
        {
            if (!canEdit() || Rows.Count >= maximum)
                return;
            Selected = create();
            Rows.Add(Selected);
            changed();
        }, () => canEdit() && Rows.Count < maximum);
        RemoveCommand = new ActionCommand(() =>
        {
            if (!canEdit() || Rows.Count <= minimum || Selected is not { } row)
                return;
            Rows.Remove(row);
            Selected = Rows.FirstOrDefault();
            changed();
        }, () => canEdit() && Rows.Count > minimum && Selected is { } row && Rows.Contains(row));
        bool CanMove(int offset) => canEdit() && Selected is { } row && Rows.IndexOf(row) is var index &&
            index >= 0 && index + offset >= 0 && index + offset < Rows.Count;
        void Move(int offset)
        {
            if (!canEdit() || Selected is not { } row)
                return;
            var index = Rows.IndexOf(row);
            if (index < 0 || index + offset < 0 || index + offset >= Rows.Count)
                return;
            Rows.Move(index, index + offset);
            Selected = row;
            changed();
        }
        MoveUpCommand = new ActionCommand(() => Move(-1), () => CanMove(-1));
        MoveDownCommand = new ActionCommand(() => Move(1), () => CanMove(1));
        Rows.CollectionChanged += (_, _) => RefreshCommands();
    }
    private void RefreshCommands()
    {
        AddCommand.NotifyCanExecuteChanged();
        RemoveCommand.NotifyCanExecuteChanged();
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
    }
}
