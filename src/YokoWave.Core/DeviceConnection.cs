namespace YokoWave.Core;

/// <summary>One connection's accepted status and last confirmed output. Mutated under its controller's device lock.</summary>
public sealed class DeviceConnection(DeviceDescriptor device)
{
    private long _motionSequence;
    public DeviceDescriptor Device { get; private set; } = device;
    public ElectrodeContact ContactA { get; private set; } = ElectrodeContact.Unknown;
    public ElectrodeContact ContactB { get; private set; } = ElectrodeContact.Unknown;
    public OutputFrame Output { get; internal set; } = OutputFrame.Zero;
    public byte? HardwareVersion { get; private set; }
    public byte? SoftwareVersion { get; private set; }
    public bool? LightEnabled { get; internal set; }
    public MotionReading? Motion { get; private set; }
    public int? Steps { get; private set; }
    public DeviceTelemetry? Telemetry { get; private set; }
    public DateTimeOffset? TelemetryCapturedAt { get; private set; }
    internal bool MotionStreaming { get; set; }
    internal void ApplyStatus(DeviceStatusUpdate update, DateTimeOffset receivedAt)
    {
        if (update.Battery is >= 0 and <= 100)
            Device = Device with { Battery = update.Battery };
        if (update.ContactA is { } a)
            ContactA = a;
        if (update.ContactB is { } b)
            ContactB = b;
        if (update.HardwareVersion is { } hardware)
            HardwareVersion = hardware;
        if (update.SoftwareVersion is { } software)
            SoftwareVersion = software;
        if (update.LightEnabled is { } light)
            LightEnabled = light;
        if (update.Steps is >= 0 and <= 65535)
            Steps = update.Steps;
        if (update.ChannelMask is >= 1 and <= 7)
            Device = Device with { ChannelMask = update.ChannelMask };
        if (update.Telemetry is { } telemetry)
        {
            Telemetry = (Telemetry ?? new()).Merge(telemetry);
            TelemetryCapturedAt = CapturedAt(receivedAt, update.TelemetryAge);
        }
        if (update.MotionAngle is { } angle && double.IsFinite(angle) && angle is >= 0 and <= 180)
            SetMotion(angle, CapturedAt(receivedAt, update.MotionAge), Steps);
    }

    internal void ApplyFeatures(ElectricalFeatureState state)
    {
        HardwareVersion = state.HardwareVersion;
        SoftwareVersion = state.SoftwareVersion;
        LightEnabled = state.LightEnabled;
    }

    internal void ResetMotion() => Motion = null;
    internal void SetMotion(double angle, DateTimeOffset capturedAt, int? steps = null) =>
        Motion = new(angle, ++_motionSequence, capturedAt, steps);

    private static DateTimeOffset CapturedAt(DateTimeOffset receivedAt, TimeSpan? age) =>
        age is { } elapsed && (elapsed < TimeSpan.Zero || elapsed > receivedAt - DateTimeOffset.MinValue)
            ? DateTimeOffset.MinValue : receivedAt - (age ?? TimeSpan.Zero);
}
