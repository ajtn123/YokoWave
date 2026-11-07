namespace YokoWave.Core.Localization;

public interface ILocalizedError
{
    LocalizedMessage LocalizedMessage { get; }
}

public sealed class LocalizedArgumentException(string key) : ArgumentException, ILocalizedError
{
    public LocalizedMessage LocalizedMessage { get; } = new(key, []);
    public override string Message => LocalizedMessage.Value;
}

public sealed class LocalizedOperationException(string key) : InvalidOperationException, ILocalizedError
{
    public LocalizedMessage LocalizedMessage { get; } = new(key, []);
    public override string Message => LocalizedMessage.Value;
}

public sealed class LocalizedIOException(string key) : IOException, ILocalizedError
{
    public LocalizedMessage LocalizedMessage { get; } = new(key, []);
    public override string Message => LocalizedMessage.Value;
}

public sealed class LocalizedAggregateException(string key, IEnumerable<Exception> errors) : AggregateException(errors), ILocalizedError
{
    public LocalizedMessage LocalizedMessage { get; } = new(key, []);
    public override string Message => LocalizedMessage.Value;
}
