namespace YokoWave.Core.Bluetooth;

/// <summary>Fixed programs must not restart on every output tick. Cache only fully accepted sequences.</summary>
internal sealed class ProgramPacketWriter(Func<byte[], CancellationToken, Task> write)
{
    private byte[][]? _previous;
    public void Reset() => _previous = null;
    public async Task Send(IReadOnlyList<byte[]> packets, CancellationToken ct)
    {
        if (_previous is { } previous && previous.Length == packets.Count &&
            previous.Select((packet, index) => packet.AsSpan().SequenceEqual(packets[index])).All(equal => equal)) return;
        Reset();
        for (var index = 0; index < packets.Count; index++)
        {
            if (index > 0) await Task.Delay(10, ct);
            await write(packets[index], ct);
        }
        _previous = packets.Select(packet => packet.ToArray()).ToArray();
    }
}
