using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace YokoWave.App.ViewModels;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new(name));
}

public sealed class ActionCommand(Action execute) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => execute();
}

public sealed class AsyncCommand(Func<Task> execute, Action<Exception> onError) : ICommand
{
    private bool _executing;
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => !_executing;
    public async void Execute(object? parameter)
    {
        if (_executing) return;
        _executing = true;
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        try { await execute(); }
        catch (Exception e) { onError(e); }
        finally { _executing = false; CanExecuteChanged?.Invoke(this, EventArgs.Empty); }
    }
}
