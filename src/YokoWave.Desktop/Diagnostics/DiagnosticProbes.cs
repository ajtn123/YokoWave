using System.Collections.Concurrent;
using YokoWave.Core.Bluetooth;
using YokoWave.Desktop.Bluetooth;

namespace YokoWave.Desktop.Diagnostics;

internal static class DiagnosticProbes
{
    public static async Task<int> RunAudioAsync(string path)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await using var input = await new Audio.MiniAudioInputFactory().StartAsync(
                new(Core.AudioSourceKind.MediaFile, path, PlaybackVolume: 0), timeout.Token);
            for (int attempt = 0; attempt < 100 && input.Latest is null; attempt++)
                await Task.Delay(50, timeout.Token);
            if (input.Latest is not { } frame || Math.Max(frame.LeftDb, frame.RightDb) <= -90)
                throw new InvalidOperationException("The audio probe received no non-silent PCM features.");
            Console.WriteLine($"Local audio decoded: L={frame.LeftDb:0.0} dBFS, R={frame.RightDb:0.0} dBFS; speaker volume=0.");
            input.SetPaused(true);
            if (input.Latest is not null)
                throw new InvalidOperationException("Pause retained an old audio frame.");
            input.SetPaused(false);
            for (int attempt = 0; attempt < 100 && input.Latest is null; attempt++)
                await Task.Delay(50, timeout.Token);
            if (input.Latest is not { } resumed || resumed.Sequence <= frame.Sequence || Math.Max(resumed.LeftDb, resumed.RightDb) <= -90)
                throw new InvalidOperationException("Resume did not supply a fresh non-silent audio frame.");
            Console.WriteLine("Audio pause/resume completed; no Bluetooth connection was opened.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
    public static async Task<int> RunBluetoothAsync(bool connect, string? audioPath, string? profile)
    {
        try
        {
#if WINDOWS
            var adapter = new WindowsBluetoothAdapter { Diagnostic = Console.WriteLine };
#else
            var adapter = BluetoothAdapterProvider.Create();
#endif
            await using var transport = new BluetoothDeviceTransport(adapter);
            transport.StatusChanged += (_, update) => Console.WriteLine($"Status: battery={update.Battery}, A={update.ContactA}, B={update.ContactB}, outputA={update.OutputAEnabled}, outputB={update.OutputBEnabled}, protocolError={update.Telemetry?.ProtocolErrorCode}");
            ConcurrentQueue<Exception> faults = new();
            transport.Faulted += (_, fault) =>
            {
                faults.Enqueue(fault.Error);
                Console.Error.WriteLine($"Bluetooth fault: {fault.Error}");
            };
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
            var devices = (await transport.ScanAsync(timeout.Token)).Where(device => profile is null || string.Equals(device.ProtocolId, profile, StringComparison.OrdinalIgnoreCase)).ToArray();
            Console.WriteLine($"Bluetooth devices found: {devices.Length}");
            foreach (var device in devices)
                Console.WriteLine($"{device.Name} {device.Id} profile={device.ProtocolId} RSSI={device.Signal}");
            if (connect && devices.Length != 1)
            {
                Console.Error.WriteLine("Connect probe requires exactly one matching device. Use the application to select a device.");
                return 2;
            }
            if (connect && devices.Length == 1)
            {
                await using var controller = new Core.DeviceController(transport);
                await controller.ConnectAsync(devices[0], timeout.Token);
                Console.WriteLine("Connected; protocol initialization completed. No nonzero output requested.");
                if (audioPath is not null)
                {
                    for (int cycle = 0; cycle < 5; cycle++)
                    {
                        var graph = Pipelines.PipelineTemplates.Create("audio", new("Audio probe", devices[0].Id, Limit: 0));
                        graph = graph with
                        {
                            Nodes = graph.Nodes.Select(node => node.Type == Pipelines.BuiltInNodes.Audio
                            ? Pipelines.NodeDefinition.Create(node.Type, new Pipelines.AudioNode(new(Core.AudioSourceKind.MediaFile, audioPath, PlaybackVolume: 0)), node.Id) : node).ToArray()
                        };
                        var plan = new Pipelines.PipelineCompiler(Pipelines.BuiltInNodes.CreateRegistry()).Compile(graph, []);
                        await using var session = new Pipelines.PipelineSession(controller, new(new Audio.MiniAudioInputFactory()));
                        await session.StartAsync(plan);
                        await Task.Delay(500, timeout.Token);
                        await session.StopAsync();
                        Console.WriteLine($"Zero-strength audio session {cycle + 1}: started and stopped; no nonzero output sent.");
                    }
                }
                await Task.Delay(1500, timeout.Token);
                await controller.DisconnectAsync(devices[0].Id, timeout.Token);
                if (!faults.IsEmpty)
                    throw new AggregateException(faults);
            }
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            foreach (System.Collections.DictionaryEntry detail in error.Data)
                Console.Error.WriteLine($"{detail.Key}: {detail.Value}");
            return 1;
        }
    }
}
