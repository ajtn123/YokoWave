namespace YokoWave.Core.Bluetooth;

public sealed record PawParameterDynamics(int IncreaseTimeCode = 0, int CancelDecrease = 255, int DecreaseTimeCode = 0, int TriggerIncrease = 255)
{
    public void Validate()
    {
        ProtocolGuard.Byte(IncreaseTimeCode, 0, 120);
        ProtocolGuard.Byte(CancelDecrease);
        ProtocolGuard.Byte(DecreaseTimeCode, 0, 120);
        ProtocolGuard.Byte(TriggerIncrease);
    }
}

public abstract record PawTrigger
{
    public abstract void Validate();
}
public sealed record PawNoTrigger : PawTrigger
{
    public override void Validate() { }
}
public sealed record PawLiveData : PawTrigger
{
    public override void Validate() { }
}
public sealed record PawRandomReaction(int EventId = 1, int MinimumSeconds = 30, int MaximumSeconds = 50,
    int ReactionSeconds = 10, PawParameterDynamics? Dynamics = null) : PawTrigger
{
    public override void Validate()
    {
        ProtocolGuard.Byte(EventId, 1, 24);
        ProtocolGuard.Word(MinimumSeconds, 1);
        ProtocolGuard.Word(MaximumSeconds, MinimumSeconds);
        ProtocolGuard.Word(ReactionSeconds, 1);
        (Dynamics ?? new()).Validate();
    }
}
public sealed record PawProbabilityEvent(int EventId, int Probability)
{
    public void Validate()
    {
        ProtocolGuard.Byte(EventId, 1, 24);
        ProtocolGuard.Byte(Probability, 0, 200);
    }
}
public sealed record PawProbability(IReadOnlyList<PawProbabilityEvent> Events, int CooldownSeconds = 0) : PawTrigger
{
    public override void Validate()
    {
        ProtocolGuard.Require(Events is { Count: >= 1 and <= 6 });
        foreach (var entry in Events)
        {
            ProtocolGuard.Require(entry is not null);
            entry!.Validate();
        }
        ProtocolGuard.Require(Events.Select(entry => entry.EventId).Distinct().Count() == Events.Count);
        ProtocolGuard.Word(CooldownSeconds);
    }
    public IReadOnlyList<PawProbabilityEvent> EffectiveEvents()
    {
        Validate();
        var remaining = 200;
        return Events.Select(entry =>
        {
            var probability = Math.Min(entry.Probability, remaining);
            remaining -= probability;
            return entry with { Probability = probability };
        }).ToArray();
    }
}
public sealed record PawVoltage(int EventId = 1, bool HighImpedance = false, int MinimumRaw = 0, int MaximumRaw = 255, int MappingRange = 0) : PawTrigger
{
    public override void Validate()
    {
        ProtocolGuard.Byte(EventId, 1, 24);
        ProtocolGuard.Byte(MinimumRaw);
        ProtocolGuard.Byte(MaximumRaw, MinimumRaw);
        ProtocolGuard.Byte(MappingRange);
    }
}
public sealed record PawAxisRange(int Minimum = -128, int Maximum = 127)
{
    public void Validate()
    {
        ProtocolGuard.Require(Minimum >= -128 && Maximum <= 127 && Minimum <= Maximum);
    }
}
public sealed record PawMotionTrigger(int EventId = 0, bool UseAngles = false, bool AccelerationAbove = true,
    int AccelerationThreshold = 100, int TriggerDebounce = 0, int CancelDebounce = 0, int MappingRange = 100,
    PawAxisRange? X = null, PawAxisRange? Y = null, PawAxisRange? Z = null)
{
    public void Validate()
    {
        ProtocolGuard.Byte(EventId, 0, 24);
        ProtocolGuard.Word(AccelerationThreshold, 0, 400);
        ProtocolGuard.Byte(TriggerDebounce, 0, 7);
        ProtocolGuard.Byte(CancelDebounce, 0, 7);
        ProtocolGuard.Byte(MappingRange);
        (X ?? new()).Validate();
        (Y ?? new()).Validate();
        (Z ?? new()).Validate();
    }
}
public sealed record PawButtonMotion(int ButtonEventId = 1, bool TriggerOnRelease = false, PawParameterDynamics? Dynamics = null,
    PawMotionTrigger? Motion = null) : PawTrigger
{
    public override void Validate()
    {
        ProtocolGuard.Byte(ButtonEventId, 0, 24);
        (Dynamics ?? new()).Validate();
        (Motion ?? new()).Validate();
        ProtocolGuard.Require(ButtonEventId > 0 || Motion is { EventId: > 0 });
    }
}
public sealed record ConfigurePaw(PawTrigger Trigger, int IndicatorColor = 1) : DeviceCommand(DeviceCapability.PawTriggers)
{
    public override void Validate()
    {
        ProtocolGuard.Byte(IndicatorColor, 1, 6);
        ProtocolGuard.Require(Trigger is not null);
        Trigger!.Validate();
    }
}

public sealed record PawCalibration(short XMinimum, short XMaximum, short YMinimum, short YMaximum, short ZMinimum, short ZMaximum)
{
    private static int Convert(short raw) => Math.Clamp(raw / 2, -128, 127);
    public PawAxisRange X => new(Convert(XMinimum), Convert(XMaximum));
    public PawAxisRange Y => new(Convert(YMinimum), Convert(YMaximum));
    public PawAxisRange Z => new(Convert(ZMinimum), Convert(ZMaximum));
}
