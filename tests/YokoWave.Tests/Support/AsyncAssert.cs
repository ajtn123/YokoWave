using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace YokoWave.Tests.Support;

internal static class AsyncAssert
{
    public static async Task EventuallyAsync(Func<bool> condition, TimeSpan? timeout = null, Action? pump = null,
        [CallerArgumentExpression(nameof(condition))] string? expression = null)
    {
        var deadline = timeout ?? TimeSpan.FromSeconds(3);
        var elapsed = Stopwatch.StartNew();
        do
        {
            pump?.Invoke();
            if (condition())
                return;
            await Task.Delay(10, TestContext.Current.CancellationToken);
        } while (elapsed.Elapsed < deadline);

        pump?.Invoke();
        Assert.True(condition(), $"Condition did not become true within {deadline}: {expression}");
    }
}
