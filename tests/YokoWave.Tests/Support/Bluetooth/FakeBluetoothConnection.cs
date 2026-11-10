using YokoWave.Core.Bluetooth;

namespace YokoWave.Tests.Support.Bluetooth;

internal sealed class FakeBluetoothConnection : IBluetoothConnection
{
    public bool ReplyToFeatures { get; set; } = true;
    public bool LightEnabled { get; private set; } = true;
    public bool ReplyToQueries { get; init; }
    public Func<byte[], byte?>? Rejection { get; init; }
    public bool FailNextWrite { get; set; }
    public bool FailWrites { get; set; }
    public bool FailDispose { get; set; }
    public bool Closed { get; private set; }
    public List<byte[]> Writes { get; } = [];

    public event Action<byte[]>? NotificationReceived;
    public event Action? Disconnected;
    public void Notify(byte[] packet) => NotificationReceived?.Invoke(packet);
    public void LoseConnection() => Disconnected?.Invoke();
    public Task WriteAsync(byte[] packet, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (FailWrites)
            throw new IOException("Injected disconnected radio");
        if (FailNextWrite)
        {
            FailNextWrite = false;
            throw new IOException("Injected write failure");
        }

        Writes.Add(packet.ToArray());
        if (Rejection?.Invoke(packet) is { } code)
        {
            Notify(V1Protocol.WithChecksum([0x35, 0x71, 0x55, code]));
            return Task.CompletedTask;
        }
        if (ReplyToFeatures && packet.SequenceEqual(V2Protocol.QueryVersion()))
            Notify(V1Protocol.WithChecksum([0x36, 0x13, 1, 2]));
        if (ReplyToFeatures && packet[1] == 0x15)
            LightEnabled = packet[2] == 1;
        if (ReplyToQueries && packet[1] == 0x71)
        {
            if (packet[2] == 4)
                Notify(V1Protocol.WithChecksum([0x35, 0x71, 4, 82]));
            else if (packet[2] is 1 or 2)
                Notify(V1Protocol.WithChecksum([0x35, 0x71, packet[2], 0, 0, 0, 0, 0]));
            else if (packet[2] == 7 && ReplyToFeatures)
                Notify(V1Protocol.WithChecksum([0x35, 0x71, 7, (byte)(LightEnabled ? 1 : 0)]));
        }

        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Closed = true;
        if (FailDispose)
            throw new IOException("Injected native cleanup failure");
        return ValueTask.CompletedTask;
    }
}
