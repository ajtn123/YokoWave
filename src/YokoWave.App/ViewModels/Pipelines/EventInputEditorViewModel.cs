using YokoWave.Core.Localization;
using YokoWave.Pipelines;
using YokoWave.Remote;

namespace YokoWave.App.ViewModels.Pipelines;

public sealed record EventOption(RemoteEventDescriptor Descriptor)
{
    public string Label => $"{Descriptor.ClientId} · {Descriptor.Event.Name ?? Descriptor.Event.Id} · {L10n.Get("Remote.EventKind." + Descriptor.Event.Kind)}";
}

public sealed class EventInputEditorViewModel : NodeEditorViewModel
{
    private EventInputNode _configuration;
    private RemoteEventDescriptor[] _descriptors = [];
    private string _language = "";
    private NodeEditorViewModel? Source => Editor.Connections.FirstOrDefault(connection => connection.Target.Node.Id == Id && connection.Target.Port.Id == "events")?.Source.Node;
    public string SourceName => Source?.Title ?? L10n.Get("Remote.EventConnect");
    public IReadOnlyList<EventOption> Events { get; private set; } = [];
    public EventOption? SelectedEvent
    {
        get => Events.FirstOrDefault(item => item.Descriptor.ClientId == ClientId && item.Descriptor.Event.Id == EventId);
        set
        {
            if (value is not null && (value.Descriptor.ClientId != ClientId || value.Descriptor.Event.Id != EventId))
                SetConfiguration(_configuration with { ClientId = value.Descriptor.ClientId, EventId = value.Descriptor.Event.Id });
        }
    }
    public string ClientId { get => _configuration.ClientId; set { if (value != ClientId) SetConfiguration(_configuration with { ClientId = value }); } }
    public string EventId { get => _configuration.EventId; set { if (value != EventId) SetConfiguration(_configuration with { EventId = value }); } }
    public EventInputEditorViewModel(PipelineEditorViewModel editor, NodeDefinition definition) : base(editor, definition)
    {
        _configuration = (EventInputNode)editor.Registry.Get(Type).Read(definition.Configuration);
        RefreshCatalog();
    }
    protected override NodeDefinition BuildDefinition() => NodeDefinition.Create(Type, _configuration, Id);
    public override void RefreshLocalization()
    {
        base.RefreshLocalization();
        Raise(nameof(SourceName));
        RefreshCatalog();
    }
    private void SetConfiguration(EventInputNode value)
    {
        if (!Editor.Registry.Get(Type).Validate(value))
            return;
        Change(() => _configuration = value, live: true);
        Editor.Session.Update(NodeControl.Create(Id, "selectEvent", value));
        Raise(nameof(ClientId));
        Raise(nameof(EventId));
        Raise(nameof(SelectedEvent));
    }
    private void RefreshCatalog()
    {
        var descriptors = GetSourceId() is { } sourceId ? RemoteEventRuntime.For(Editor.Services).Source(sourceId).Snapshot().ToArray() : [];
        if (_descriptors.SequenceEqual(descriptors) && _language == L10n.Current.Language)
            return;
        _descriptors = descriptors;
        _language = L10n.Current.Language;
        Events = descriptors.Select(item => new EventOption(item)).ToArray();
        Raise(nameof(Events));
        Raise(nameof(SelectedEvent));
    }

    private Guid? GetSourceId()
    {
        if (Source?.Type != SubgraphExpansion.Type)
            return Source?.Id;
        var expanded = SubgraphExpansion.Expand(Editor.ToDocument());
        return expanded.Connections.FirstOrDefault(connection => connection.Target == new PortAddress(Id, "events"))?.Source.Node;
    }
}
