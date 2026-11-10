using System.Collections.Concurrent;
using YokoWave.Pipelines;

namespace YokoWave.Remote.DgLab.V4;

public sealed record DgLabV4NodeCommand(string Id, DgLabV4Operation? Operation = null, int? Channel = null);

/// <summary>Only evaluates queued commands. Responses are drained by the network participant on the next capture.</summary>
internal sealed class DgLabV4InputProcessor : ISignalProcessor, ISuspendableProcessor
{
    private DgLabV4Scheduler _scheduler = new();
    private bool _trigger;

    public void Evaluate(NodeEvaluationContext context)
    {
        var results = context.Inputs.Results.GetOrAdd(context.NodeId, _ => new ConcurrentQueue<NodeControl>());
        if (context.Inputs.Controls.TryGetValue(context.NodeId, out var controls))
        {
            while (controls.TryDequeue(out var control))
            {
                if (control.Action == "configure")
                {
                    _scheduler = new(control.Read<double>());
                    continue;
                }
                var command = control.Read<DgLabV4NodeCommand>();
                try
                {
                    if (control.Action == "operate")
                    {
                        _scheduler.Submit(command.Id, command.Operation!);
                    }
                    else if (control.Action == "clear")
                    {
                        _scheduler.Clear(command.Channel);
                        results.Enqueue(NodeControl.Create(context.NodeId, "cleared", command.Id));
                    }
                    else if (control.Action == "cancel")
                    {
                        _scheduler.Cancel();
                    }
                }
                catch (DgLabV4Exception error)
                {
                    results.Enqueue(NodeControl.Create(context.NodeId, "error", new { command.Id, error.Code }));
                }
            }
        }
        var frame = _scheduler.Evaluate(context.Elapsed);
        FrameSignalProcessor.Write(context, frame);
        foreach (var completion in _scheduler.DrainCompleted())
            results.Enqueue(NodeControl.Create(context.NodeId, "completed", completion));
        var trigger = context.Input("trigger");
        bool high = trigger.IsValid(context.Now) && trigger.At(0) > 0;
        var action = context.Input("action");
        if (high && !_trigger && action.IsValid(context.Now))
            results.Enqueue(NodeControl.Create(context.NodeId, "action", (int)action.At(0)));
        _trigger = high;
        context.Status(new(_scheduler.Count > 0 ? "Tracking" : "Waiting", Count: _scheduler.Count));
    }

    public void Suspend()
    {
        _scheduler.Cancel();
        _trigger = false;
    }
}
