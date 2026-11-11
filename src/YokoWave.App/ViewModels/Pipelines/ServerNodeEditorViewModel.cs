using System.Security.Cryptography;
using YokoWave.Core;
using YokoWave.Pipelines;
using YokoWave.Remote;

namespace YokoWave.App.ViewModels.Pipelines;

public sealed class ServerNodeEditorViewModel : NodeEditorViewModel
{
    private readonly string[] _deviceIds;
    private RemoteServerSettings _settings;
    private readonly bool _gatt;
    public bool HasControlLimit => Type == RemotePipelineNodes.ControlServer;
    public string Token => Editor.Services.Credentials[Id].Token;
    public string CertificatePassword
    {
        get => Editor.Services.Credentials[Id].CertificatePassword;
        set
        {
            if (CanEdit)
            {
                Editor.Services.Credentials[Id] = new(Token, value);
                Raise();
            }
        }
    }
    public string Address
    { get => _settings.Address; set => SetSettings(_settings with { Address = value }, nameof(Address)); }
    public int Port
    { get => _settings.Port; set => SetSettings(_settings with { Port = value }, nameof(Port)); }
    public int LeaseMilliseconds
    { get => _settings.LeaseMilliseconds; set => SetSettings(_settings with { LeaseMilliseconds = value }, nameof(LeaseMilliseconds)); }
    public double ControlLimit
    {
        get => _settings.ControlLimit;
        set
        {
            if (double.IsFinite(value))
            {
                SetSettings(_settings with
                {
                    ControlLimit = value
                }, nameof(ControlLimit));
            }
        }
    }
    public string CertificatePath
    { get => _settings.CertificatePath ?? ""; set => SetSettings(_settings with { CertificatePath = value.Length == 0 ? null : value }, nameof(CertificatePath)); }
    public ServerNodeEditorViewModel(PipelineEditorViewModel editor, NodeDefinition definition) : base(editor, definition)
    {
        _gatt = Type == RemotePipelineNodes.GattServer;
        object configuration = editor.Registry.Get(Type).Read(definition.Configuration);
        _deviceIds = configuration switch { GattServerNode gatt => gatt.DeviceIds, ControlServerNode control => [control.DeviceId], _ => [] };
        _settings = configuration switch { GattServerNode gatt => gatt.Settings, ControlServerNode control => control.Settings, EventServerNode events => events.Settings, _ => throw new ArgumentException(nameof(definition)) };
        editor.Services.Credentials.GetOrAdd(Id, _ => new(Convert.ToHexString(RandomNumberGenerator.GetBytes(32))));
    }
    protected override NodeDefinition BuildDefinition() => Type == RemoteEventNodes.Server ? NodeDefinition.Create(Type, new EventServerNode(_settings), Id)
        : _gatt ? NodeDefinition.Create(Type, new GattServerNode(_deviceIds, _settings), Id)
        : NodeDefinition.Create(Type, new ControlServerNode(_deviceIds[0], _settings), Id);
    private void SetSettings(RemoteServerSettings settings, string property)
    {
        settings.Validate();
        Change(() =>
        {
            _settings = settings;
            Raise(property);
        });
    }
}
