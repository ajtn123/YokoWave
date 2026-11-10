using YokoWave.Pipelines;
using YokoWave.Remote.DgLab.V4;

namespace YokoWave.App.ViewModels.Pipelines;

public sealed class DgLabRelayEditorViewModel : NodeEditorViewModel
{
    private DgLabV4RelaySettings _settings;
    public string Address { get => _settings.Address; set => Update(_settings with { Address = value }, nameof(Address)); }
    public int Port { get => _settings.Port; set => Update(_settings with { Port = value }, nameof(Port)); }
    public string Path { get => _settings.Path; set => Update(_settings with { Path = value }, nameof(Path)); }
    public int HeartbeatMilliseconds { get => _settings.HeartbeatMilliseconds; set => Update(_settings with { HeartbeatMilliseconds = value }, nameof(HeartbeatMilliseconds)); }
    public int IdleMilliseconds { get => _settings.IdleMilliseconds; set => Update(_settings with { IdleMilliseconds = value }, nameof(IdleMilliseconds)); }
    public int PingMilliseconds { get => _settings.PingMilliseconds; set => Update(_settings with { PingMilliseconds = value }, nameof(PingMilliseconds)); }
    public int MaximumMissedPongs { get => _settings.MaximumMissedPongs; set => Update(_settings with { MaximumMissedPongs = value }, nameof(MaximumMissedPongs)); }
    public string CertificatePath { get => _settings.CertificatePath ?? ""; set => Update(_settings with { CertificatePath = value.Length == 0 ? null : value }, nameof(CertificatePath)); }
    public string CertificatePassword
    {
        get => Editor.Services.Credentials.GetValueOrDefault(Id)?.CertificatePassword ?? "";
        set
        {
            if (CanEdit)
            {
                Editor.Services.Credentials[Id] = new("", value);
                Raise();
            }
        }
    }

    public DgLabRelayEditorViewModel(PipelineEditorViewModel editor, NodeDefinition definition) : base(editor, definition) =>
        _settings = ((DgLabV4RelayNode)editor.Registry.Get(Type).Read(definition.Configuration)).Settings;

    protected override NodeDefinition BuildDefinition() => NodeDefinition.Create(Type, new DgLabV4RelayNode(_settings), Id);
    private void Update(DgLabV4RelaySettings settings, string property)
    {
        if (_settings == settings)
            return;
        Change(() =>
        {
            _settings = settings;
            Raise(property);
        });
    }
}
