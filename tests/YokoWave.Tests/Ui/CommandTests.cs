using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using YokoWave.App.ViewModels;
using YokoWave.Tests.Support.Ui;

namespace YokoWave.Tests.Ui;

public sealed class CommandTests
{
    [AvaloniaFact]
    public async Task CommandsDispatchToUiRejectReentryAndRecoverWhenErrorReportingOrSubscribersThrow()
    {
        using var state = new UiTestState();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        int notifications = 0;
        var command = new AsyncCommand(async () =>
        {
            Assert.True(Dispatcher.UIThread.CheckAccess());
            calls++;
            entered.TrySetResult();
            await release.Task;
            throw new InvalidOperationException("execution");
        }, _ =>
        {
            reported.TrySetResult();
            throw new InvalidOperationException("reporting");
        });
        command.CanExecuteChanged += (_, _) => throw new InvalidOperationException("subscriber");
        command.CanExecuteChanged += (_, _) =>
        {
            Assert.True(Dispatcher.UIThread.CheckAccess());
            notifications++;
        };
        await Task.Run(() => command.Execute(null), TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(_deadline, TestContext.Current.CancellationToken);
        Assert.False(command.CanExecute(null));
        command.Execute(null);
        Assert.Equal(1, calls);
        release.TrySetResult();
        await reported.Task.WaitAsync(_deadline, TestContext.Current.CancellationToken);
        Assert.True(command.CanExecute(null));
        Assert.Equal(2, notifications);
    }

    private static readonly TimeSpan _deadline = TimeSpan.FromSeconds(3);
}
