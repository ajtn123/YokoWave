using System.Collections.ObjectModel;
using YokoWave.Core.Localization;
using YokoWave.Pipelines;

namespace YokoWave.App.ViewModels.Pipelines;

public sealed class WaveformEditorNodeViewModel : NodeEditorViewModel, IWaveformNodeEditor
{
    private Guid _waveformId;
    public override string Summary => SelectedWaveform?.Name ?? L10n.Get("Pipeline.Error.Waveform");
    public override WaveformItem? Preview => SelectedWaveform;
    public ObservableCollection<WaveformItem> Waveforms => Editor.Waveforms;
    public WaveformItem? SelectedWaveform
    {
        get => Waveforms.FirstOrDefault(p => p.Asset.Id == _waveformId);
        set
        {
            if (value is not null && value.Asset.Id != _waveformId)
            {
                Change(() =>
                {
                    _waveformId = value.Asset.Id;
                    Raise();
                }, live: true);
                Editor.Session.Update(NodeControl.Create(Id, "waveform", value.Asset));
            }
        }
    }
    public WaveformEditorNodeViewModel(PipelineEditorViewModel editor, NodeDefinition definition) : base(editor, definition)
    {
        var config = (WaveformNode)editor.Registry.Get(Type).Read(definition.Configuration);
        _waveformId = config.WaveformId;
    }
    public override bool IsPortAvailable(NodePort port) => WaveformPorts.IsAvailable(port, SelectedWaveform?.Asset);
    protected override NodeDefinition BuildDefinition() => NodeDefinition.Create(Type, new WaveformNode(_waveformId), Id);
    public bool SelectWaveform(WaveformItem waveform)
    {
        SelectedWaveform = waveform;
        return true;
    }
}
