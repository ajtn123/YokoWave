using System.Collections.Concurrent;
using Avalonia.Logging;

namespace YokoWave.Tests.Support.Ui;

internal sealed class BindingLogCapture : ILogSink, IDisposable
{
    private readonly ILogSink? _previous = Logger.Sink;

    public ConcurrentQueue<string> Errors { get; } = new();

    public BindingLogCapture() => Logger.Sink = this;

    public bool IsEnabled(LogEventLevel level, string area) => area == LogArea.Binding && level >= LogEventLevel.Warning;

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate)
    {
        if (IsEnabled(level, area))
            Errors.Enqueue(messageTemplate);
    }

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues) =>
        Log(level, area, source, $"{messageTemplate} [{string.Join(", ", propertyValues)}]");

    public void Dispose() => Logger.Sink = _previous;
}
