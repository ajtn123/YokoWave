using System.Collections.ObjectModel;
using System.ComponentModel;
using YokoWave.Core;
using YokoWave.Core.Localization;

namespace YokoWave.App.ViewModels;

public sealed class HistoryViewModel : ObservableObject, IDisposable
{
    private readonly List<SessionRecord> _records = [];
    public ObservableCollection<SessionItem> Sessions { get; } = [];
    public ObservableCollection<ActivityItem> Activities { get; } = [];
    public void Log(LocalizedMessage message)
    {
        Activities.Insert(0, new(DateTime.Now.ToString("HH:mm:ss"), message));
        while (Activities.Count > 20)
            Activities.RemoveAt(Activities.Count - 1);
    }
    public SessionStatistics Statistics { get; private set; } = new();
    public long SessionCount => Statistics.SessionCount;
    public string TotalTime => L10n.Format("History.TotalFormat", Statistics.TotalDurationSeconds / 60);
    public IReadOnlyList<SessionRecord> Records => _records;
    public HistoryViewModel() => L10n.Current.PropertyChanged += OnLanguage;
    public void Load(IReadOnlyList<SessionRecord> records, SessionStatistics statistics)
    {
        _records.Clear();
        _records.AddRange(records);
        Sessions.Clear();
        foreach (var record in records.OrderByDescending(r => r.StartedAt))
            Sessions.Add(new(record));
        Statistics = statistics;
        Refresh();
    }
    public void Add(SessionRecord record)
    {
        _records.Insert(0, record);
        Sessions.Insert(0, new(record));
        Statistics = Statistics.Add(record);
        Refresh();
    }
    public void Retain(SessionJournal journal, SessionStatistics statistics)
    {
        Statistics = new(Math.Max(Statistics.SessionCount, statistics.SessionCount), Math.Max(Statistics.TotalDurationSeconds, statistics.TotalDurationSeconds));
        _records.RemoveAll(r => journal.IsDiscarded(r.Id));
        foreach (var item in Sessions.Where(s => journal.IsDiscarded(s.Record.Id)).ToArray())
            Sessions.Remove(item);
        Refresh();
    }
    private void Refresh()
    {
        Raise(nameof(SessionCount));
        Raise(nameof(TotalTime));
    }
    private void OnLanguage(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(JsonLocalizer.Language))
            return;
        foreach (var session in Sessions)
            session.RefreshLocalization();
        foreach (var activity in Activities)
            activity.RefreshLocalization();
        Refresh();
    }
    public void Dispose() => L10n.Current.PropertyChanged -= OnLanguage;
}
