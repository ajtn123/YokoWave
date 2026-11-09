using YokoWave.Core;
using YokoWave.Core.Localization;

namespace YokoWave.Pipelines;

/// <summary>Dispatches evaluated frames and records only successfully sent output.</summary>
internal sealed class PipelineOutputDispatcher(DeviceController controller, Guid owner)
{
    private readonly Dictionary<string, PumpOutput?> _sentPumps = [];
    public double PeakA { get; private set; }
    public double PeakB { get; private set; }
    public double PeakC { get; private set; }

    public void ResetPumpCommands() => _sentPumps.Clear();

    public async Task SubmitAsync(PipelineFrame frame)
    {
        foreach (var command in frame.Commands ?? [])
            await controller.ExecuteCommandAsync(command.DeviceId, command.Command, owner).ConfigureAwait(false);

        var pending = frame.Outputs.Where(pair => ShouldSend(pair.Key, pair.Value)).ToArray();
        var results = await Task.WhenAll(pending.Select(pair => SendAsync(pair.Key, pair.Value))).ConfigureAwait(false);
        List<Exception> errors = [];
        for (var i = 0; i < pending.Length; i++)
        {
            if (results[i] is { } error)
            {
                errors.Add(error);
                continue;
            }
            var (id, requested) = pending[i];
            RecordOutput(id, controller.Connections.GetValueOrDefault(id)?.Output ?? requested);
        }
        if (errors.Count == 1)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(errors[0]).Throw();
        if (errors.Count > 1)
            throw new LocalizedAggregateException("Reason.PipelineError", errors);
    }

    private bool ShouldSend(string id, OutputFrame frame) => frame.Pump is not { } pump ||
        !_sentPumps.TryGetValue(id, out var sent) || sent != pump ||
        pump is { Direction: not PumpDirection.Off, PeristalticSeconds: 0 } or { Water: true, WaterSeconds: 0 };

    private async Task<Exception?> SendAsync(string id, OutputFrame frame)
    {
        try
        {
            await controller.SendOwnedAsync(owner, id, frame).ConfigureAwait(false);
            return null;
        }
        catch (Exception error)
        {
            return error;
        }
    }

    private void RecordOutput(string id, OutputFrame output)
    {
        _sentPumps[id] = output.Pump;
        PeakA = Math.Max(PeakA, output.ChannelA);
        PeakB = Math.Max(PeakB, output.ChannelB);
        PeakC = Math.Max(PeakC, output.ChannelC);
    }
}
