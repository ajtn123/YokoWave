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

public sealed class LocalizedOperationException : InvalidOperationException, ILocalizedError
{
    public LocalizedMessage LocalizedMessage { get; }
    public override string Message => LocalizedMessage.Value;

    public LocalizedOperationException(string key, Exception? innerException = null) : this(new LocalizedMessage(key, []), innerException) { }
    public LocalizedOperationException(LocalizedMessage message, Exception? innerException = null) : base(message.Value, innerException)
    {
        LocalizedMessage = message;
    }
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
