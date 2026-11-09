using YokoWave.Core;
using YokoWave.Core.Localization;
using YokoWave.Pipelines;

namespace YokoWave.App.ViewModels.Pipelines;

public sealed class TouchNodeEditorViewModel : NodeEditorViewModel
{
    private readonly TouchNode _settings;
    private double _frequency = 20;

    private double _width = 50;
    private bool _held;
    private string _recordingName = "";
    public double Frequency
    {
        get => _frequency;
        set
        {
            if (double.IsFinite(value) && Set(ref _frequency, Math.Clamp(value, 1, 100)))
                Send();
        }
    }
    public double PulseWidth
    {
        get => _width;
        set
        {
            if (double.IsFinite(value) && Set(ref _width, Math.Clamp(value, 0, 100)))
                Send();
        }
    }
    public bool IsHeld
    {
        get => _held;
        set
        {
            if (Set(ref _held, value))
                Send();
        }
    }
    public string RecordingName { get => _recordingName; set => Set(ref _recordingName, value); }
    public AsyncCommand SaveRecordingCommand { get; }
    public TouchNodeEditorViewModel(PipelineEditorViewModel editor, NodeDefinition definition) : base(editor, definition)
    {
        _settings = (TouchNode)editor.Registry.Get(Type).Read(definition.Configuration);
        SaveRecordingCommand = new(() =>
        {
            var recording = Editor.Session.NodeStatus.GetValueOrDefault(Id)?.Recording;
            if (recording is not { Count: >= 2 })
                throw new LocalizedOperationException("Error.TouchRecording");
            var waveform = new WaveformAsset(Guid.NewGuid(), string.IsNullOrWhiteSpace(RecordingName) ? L10n.Get("Touch.Title") : RecordingName,
                WaveShape.Sampled, recording.Count / 10d, 20, Samples: recording.ToArray());
            waveform.Validate();
            Editor.AddRecording(waveform);
            return Task.CompletedTask;
        }, editor.ReportError);
    }
    protected override NodeDefinition BuildDefinition() => NodeDefinition.Create(Type, _settings, Id);
    private void Send() => Editor.Session.Update(NodeControl.Create(Id, "touch", new TouchPoint(Frequency, PulseWidth / 100, IsHeld)));
}
