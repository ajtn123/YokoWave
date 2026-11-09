using System.Windows.Input;

namespace YokoWave.App.ViewModels.Pipelines;

public sealed class PipelineParameterCommand<T>(Action<T> action) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => parameter is T;
    public void Execute(object? parameter)
    {
        if (parameter is T value)
            action(value);
    }
}
