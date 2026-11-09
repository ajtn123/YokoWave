using System.Collections.ObjectModel;
using System.Windows.Input;

namespace YokoWave.App.ViewModels;

public class OrderedRowsEditorViewModel<TRow> : ObservableObject where TRow : class
{
    private TRow? _selected;
    public ObservableCollection<TRow> Rows { get; } = [];
    public TRow? Selected { get => _selected; set => Set(ref _selected, value); }
    public ICommand AddCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand MoveUpCommand { get; }
    public ICommand MoveDownCommand { get; }
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
        });
        RemoveCommand = new ActionCommand(() =>
        {
            if (!canEdit() || Rows.Count <= minimum || Selected is not { } row)
                return;
            Rows.Remove(row);
            Selected = Rows.FirstOrDefault();
            changed();
        });
        void Move(int offset)
        {
            if (!canEdit() || Selected is not { } row)
                return;
            var index = Rows.IndexOf(row);
            if (index < 0 || index + offset < 0 || index + offset >= Rows.Count)
                return;
            Rows.Move(index, index + offset);
            changed();
        }
        MoveUpCommand = new ActionCommand(() => Move(-1));
        MoveDownCommand = new ActionCommand(() => Move(1));
    }
}
