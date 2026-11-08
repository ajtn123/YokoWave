namespace YokoWave.Core.Bluetooth;

/// <summary>Schedules 100 ms native frequency entries, splitting only at entry boundaries to fit the GATT payload.</summary>
internal sealed class BufferedFrequencyWriter
{
    private long _throughA = -1, _throughB = -1;
    private int _strengthA = -1, _strengthB = -1;
    public void Reset()
    {
        _throughA = _throughB = -1;
        _strengthA = _strengthB = -1;
    }
    public IReadOnlyList<byte[]> Encode(OutputFrame frame, int maximumPacketLength)
    {
        var output = frame.NativeProgram!;
        var settings = output.Settings;
        List<byte[]> packets = [];
        EncodeChannel(1, settings.BufferA!, V1Protocol.Parameters(frame, 0).Strength, output, maximumPacketLength, ref _throughA, ref _strengthA, packets);
        EncodeChannel(2, settings.BufferB!, V1Protocol.Parameters(frame, 1).Strength, output, maximumPacketLength, ref _throughB, ref _strengthB, packets);
        return packets;
    }
    private static void EncodeChannel(int channel, IReadOnlyList<FrequencyPulse> samples, int strength, NativeProgramOutput output,
        int maximumPacketLength, ref long through, ref int previousStrength, List<byte[]> packets)
    {
        var frameIndex = (long)Math.Floor(output.ElapsedSeconds * 10);
        if (frameIndex < through && strength == previousStrength) return;
        var capacity = Math.Min(100, (maximumPacketLength - 7) / 2);
        if (capacity < 1) throw new Localization.LocalizedOperationException("Error.BluetoothPacketSize");
        var index = (int)(frameIndex % samples.Count);
        var count = Math.Min(capacity, samples.Count - index);
        if (output.RemainingSeconds is { } remaining) count = Math.Min(count, Math.Max(0, (int)Math.Floor(remaining * 10)));
        if (!output.Settings.Repeat && frameIndex >= samples.Count || count == 0 || strength == 0)
        {
            if (previousStrength != 0) packets.Add(V2Protocol.Buffered(channel, 0, [new()]));
            previousStrength = 0;
            through = frameIndex + 1;
            return;
        }
        packets.Add(V2Protocol.Buffered(channel, strength, samples.Skip(index).Take(count).ToArray()));
        through = frameIndex + count;
        previousStrength = strength;
    }
}
