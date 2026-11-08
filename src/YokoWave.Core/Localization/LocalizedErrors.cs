namespace YokoWave.Core.Localization;

public interface ILocalizedError
{
    LocalizedMessage LocalizedMessage { get; }
}

public sealed class LocalizedArgumentException(string key, Exception? innerException = null)
    : ArgumentException(L10n.Get(key), innerException), ILocalizedError
{
    public LocalizedMessage LocalizedMessage { get; } = new(key, []);
    public override string Message => LocalizedMessage.Value;
}

public sealed class LocalizedOperationException(string key, Exception? innerException = null)
    : InvalidOperationException(L10n.Get(key), innerException), ILocalizedError
{
    public LocalizedMessage LocalizedMessage { get; } = new(key, []);
    public override string Message => LocalizedMessage.Value;
}

public sealed class LocalizedIOException(string key, Exception? innerException = null)
    : IOException(L10n.Get(key), innerException), ILocalizedError
{
    public LocalizedMessage LocalizedMessage { get; } = new(key, []);
    public override string Message => LocalizedMessage.Value;
}

public sealed class LocalizedAggregateException(string key, IEnumerable<Exception> errors)
    : AggregateException(L10n.Get(key), errors), ILocalizedError
{
    public LocalizedMessage LocalizedMessage { get; } = new(key, []);
    public override string Message => LocalizedMessage.Value;
}
