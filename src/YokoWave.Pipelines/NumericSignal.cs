namespace YokoWave.Pipelines;

/// <summary>One scalar or four samples at 25 ms intervals. Expiry and simulated provenance survive processing.</summary>
public sealed record NumericSignal(IReadOnlyList<double> Samples, DateTimeOffset ValidUntil, bool Simulated = false, long Sequence = 0)
{
    public static NumericSignal Missing { get; } = new([], DateTimeOffset.MinValue);
    public static NumericSignal Constant(double value) => new([value], DateTimeOffset.MaxValue);
    public bool IsValid(DateTimeOffset now) => ValidUntil >= now && Samples.Count is 1 or 4 && Samples.All(double.IsFinite);
    public double At(int index) => Samples.Count == 1 ? Samples[0] : Samples[index];

    public static NumericSignal Combine(NumericSignal a, NumericSignal b, Func<double, double, double> operation) =>
        a.Samples.Count is not (1 or 4) || b.Samples.Count is not (1 or 4) ? Missing : new(
            Enumerable.Range(0, Math.Max(a.Samples.Count, b.Samples.Count)).Select(i => operation(a.At(i), b.At(i))).ToArray(),
            a.ValidUntil < b.ValidUntil ? a.ValidUntil : b.ValidUntil, a.Simulated || b.Simulated, Math.Max(a.Sequence, b.Sequence));
}

public enum PortDirection
{
    Input, Output
}
public enum PortValueKind
{
    Numeric, Intent
}
public sealed record NumericParameterSpec(string Unit, double Minimum, double Maximum, double DefaultValue, bool Integer = false)
{
    public bool Accepts(double value) => double.IsFinite(value) && value >= Minimum && value <= Maximum && (!Integer || value == Math.Truncate(value));
}

public sealed record NodePort(string Id, string NameKey, PortDirection Direction, NumericParameterSpec? Parameter = null, PortValueKind Kind = PortValueKind.Numeric)
{
    public static NodePort Input(string id, string key, double minimum, double maximum, double value = 0, string unit = "", bool integer = false) =>
        new(id, key, PortDirection.Input, new(unit, minimum, maximum, value, integer));
    public static NodePort Output(string id, string key, string unit = "") => new(id, key, PortDirection.Output,
        new(unit, double.MinValue, double.MaxValue, 0));
    public static NodePort Intent(string id, string key, PortDirection direction) => new(id, key, direction, Kind: PortValueKind.Intent);
}
