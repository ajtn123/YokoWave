using System.Diagnostics;
using System.Windows.Input;
using Avalonia.Threading;

namespace YokoWave.App.ViewModels;

public sealed class AsyncCommand(Func<Task> execute, Action<Exception> onError, Func<bool>? canExecute = null) : ICommand
{
    private int _executing;
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => Volatile.Read(ref _executing) == 0 && (canExecute?.Invoke() ?? true);
    public void NotifyCanExecuteChanged()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(NotifyCanExecuteChanged);
            return;
        }
        if (CanExecuteChanged is not { } handlers)
            return;
        foreach (EventHandler handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, EventArgs.Empty);
            }
            catch (Exception error)
            {
                Trace.TraceError("Command state subscriber failed: {0}", error);
            }
        }
    }
    public async void Execute(object? parameter)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => Execute(parameter));
            return;
        }
        try
        {
            await ExecuteAsync(parameter);
        }
        catch (Exception error)
        {
            try
            {
                onError(error);
            }
            catch (Exception reportError)
            {
                Trace.TraceError("Command error reporting failed: {0}", new AggregateException(error, reportError));
            }
        }
    }
    public async Task ExecuteAsync(object? parameter = null)
    {
        if (!CanExecute(parameter) || Interlocked.CompareExchange(ref _executing, 1, 0) != 0)
            return;
        NotifyCanExecuteChanged();
        try
        {
            await execute();
        }
        finally
        {
            Interlocked.Exchange(ref _executing, 0);
            NotifyCanExecuteChanged();
        }
    }
}
