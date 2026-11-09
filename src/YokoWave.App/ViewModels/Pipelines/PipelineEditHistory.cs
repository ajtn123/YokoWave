using YokoWave.Pipelines;

namespace YokoWave.App.ViewModels.Pipelines;

/// <summary>Bounded document snapshots. The editor decides when an edit or restoration is permitted.</summary>
internal sealed class PipelineEditHistory
{
    private const int Capacity = 100;
    private readonly List<PipelineDocument> _undo = [];
    private readonly List<PipelineDocument> _redo = [];

    public bool CanUndo(Func<PipelineDocument, bool> accepts) => _undo.Count > 0 && accepts(_undo[^1]);
    public bool CanRedo(Func<PipelineDocument, bool> accepts) => _redo.Count > 0 && accepts(_redo[^1]);

    public void Record(PipelineDocument before)
    {
        _undo.Add(before);
        if (_undo.Count > Capacity)
            _undo.RemoveAt(0);
        _redo.Clear();
    }

    public PipelineDocument? Undo(PipelineDocument current, Func<PipelineDocument, bool> accepts) => Transfer(_undo, _redo, current, accepts);
    public PipelineDocument? Redo(PipelineDocument current, Func<PipelineDocument, bool> accepts) => Transfer(_redo, _undo, current, accepts);

    private static PipelineDocument? Transfer(List<PipelineDocument> from, List<PipelineDocument> to, PipelineDocument current, Func<PipelineDocument, bool> accepts)
    {
        if (from.Count == 0 || !accepts(from[^1]))
            return null;
        var previous = from[^1];
        from.RemoveAt(from.Count - 1);
        to.Add(current);
        return previous;
    }
}
