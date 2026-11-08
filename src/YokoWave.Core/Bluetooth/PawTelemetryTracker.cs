namespace YokoWave.Core.Bluetooth;

/// <summary>Event activity and parameters are independent for each of the 24 Paw events.</summary>
public sealed class PawTelemetryTracker
{
    private readonly object _gate = new();
    private readonly Dictionary<int, PawEventState> _events = [];
    public DeviceTelemetry? Parse(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return null;
        if (PawProtocol.ParseCalibration(data) is { } calibration) return new(Calibration: calibration);
        if (data.Length == 9 && data[0] == 0xd0)
            return new(Pressed: data[3] != 0, Acceleration: data[4], AngleX: (sbyte)data[5], AngleY: (sbyte)data[6], AngleZ: (sbyte)data[7],
                VoltageRaw: data[8], IndicatorColor: data[1], Sequence: data[2]);
        if (!(data.Length == 4 && data[0] is 0x5a or 0x5c || data.Length == 3 && data[0] == 0x5b) || data[2] is < 1 or > 24) return null;
        lock (_gate)
        {
            var previous = _events.GetValueOrDefault(data[2]);
            bool? active = data[0] == 0x5a ? true : data[0] == 0x5b ? false : previous?.Active;
            int? parameter = data[0] == 0x5b ? previous?.Parameter : data[3];
            _events[data[2]] = new(data[2], active, parameter);
            return new(EventId: data[2], Parameter: parameter, EventActive: active, IndicatorColor: data[1], PawEvents: _events.Values.OrderBy(state => state.EventId).ToArray());
        }
    }
}
