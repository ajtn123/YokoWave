namespace YokoWave.Core.Bluetooth;

/// <summary>Fixed programs must not restart on every output tick. Cache only fully accepted sequences.</summary>
internal sealed class ProgramPacketWriter(Func<byte[], CancellationToken, Task> write)
{
    private byte[][]? _previous;
    public void Reset() => _previous = null;
    public async Task SendAsync(IReadOnlyList<byte[]> packets, CancellationToken cancellationToken)
    {
        if (_previous is { } previous && previous.Length == packets.Count &&
            previous.Select((packet, index) => packet.AsSpan().SequenceEqual(packets[index])).All(equal => equal))
        {
            return;
        }

        Reset();
        for (int index = 0; index < packets.Count; index++)
        {
            if (index > 0)
                await Task.Delay(10, cancellationToken).ConfigureAwait(false);
            await write(packets[index], cancellationToken).ConfigureAwait(false);
        }
        _previous = packets.Select(packet => packet.ToArray()).ToArray();
    }
}
