using YokoWave.Core;

namespace YokoWave.Pipelines;

internal sealed class WaveformProcessor(WaveformAsset waveform) : ISignalProcessor, IInteractiveProcessor
{
    private WaveformAsset _waveform = waveform;
    private bool _restart;
    private double _epoch;
    public void Update(NodeControl control)
    {
        if (control.Action != "waveform")
            return;
        var waveform = control.Read<WaveformAsset>();
        waveform.Validate();
        _waveform = waveform;
        _restart = true;
    }
    public void Evaluate(NodeEvaluationContext context)
    {
        if (_restart)
        {
            _epoch = context.Elapsed.TotalSeconds;
            _restart = false;
        }
        var samples = WaveformGenerator.Window(_waveform, context.Elapsed.TotalSeconds - _epoch);
        WaveformPorts.Write(context, _waveform, samples);
    }
}
