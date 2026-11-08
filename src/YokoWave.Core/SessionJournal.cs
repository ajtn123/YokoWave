using System.Text;
using System.Text.Json;
using YokoWave.Core.Localization;

namespace YokoWave.Core;

public sealed class SessionJournal(string directory)
{
    public const int MaxFileBytes = 128 * 1024;
    public string FilePath { get; } = Path.Combine(directory, "sessions.jsonl");
    public string PreviousFilePath { get; } = Path.Combine(directory, "sessions.1.jsonl");
    private List<SessionRecord> _current = [];
    private List<SessionRecord> _previous = [];
    private readonly HashSet<Guid> _seen = [];
    public IReadOnlyList<SessionRecord> Retained => _previous.Concat(_current).OrderByDescending(record => record.StartedAt).ToArray();
    public bool IsDiscarded(Guid id) => _seen.Contains(id) && !_current.Any(record => record.Id == id) && !_previous.Any(record => record.Id == id);

    internal async Task LoadAsync(JsonSerializerOptions options)
    {
        _previous = await ReadAsync(PreviousFilePath, options);
        _current = await ReadAsync(FilePath, options);
        _seen.Clear();
        foreach (var record in _previous.Concat(_current))
            if (!_seen.Add(record.Id)) throw new LocalizedArgumentException("Error.InvalidRecords");
    }

    private static async Task<List<SessionRecord>> ReadAsync(string path, JsonSerializerOptions options)
    {
        if (!File.Exists(path)) return [];
        if (new FileInfo(path).Length > MaxFileBytes) throw new LocalizedArgumentException("Error.SessionFileSize");
        List<SessionRecord> records = [];
        foreach (var line in await File.ReadAllLinesAsync(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var record = JsonSerializer.Deserialize<SessionRecord>(line, options) ?? throw new JsonException("Invalid session record.");
            ValidateRecord(record);
            records.Add(record);
        }
        return records;
    }

    internal async Task AppendAsync(IEnumerable<SessionRecord> records, JsonSerializerOptions options, Action<SessionRecord> onAppended)
    {
        foreach (var record in records.OrderBy(record => record.StartedAt))
        {
            if (_seen.Contains(record.Id)) continue;
            ValidateRecord(record);
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(record, options) + "\n");
            if (bytes.Length > MaxFileBytes) throw new LocalizedArgumentException("Error.SessionFileSize");
            if (File.Exists(FilePath) && new FileInfo(FilePath).Length + bytes.Length > MaxFileBytes)
            {
                File.Move(FilePath, PreviousFilePath, true);
                _previous = _current;
                _current = [];
            }
            await using (var stream = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, true))
                await stream.WriteAsync(bytes);
            _current.Add(record);
            _seen.Add(record.Id);
            onAppended(record);
        }
    }

    internal static void ValidateRecord(SessionRecord record)
    {
        if (record.Id == Guid.Empty || string.IsNullOrWhiteSpace(record.WaveformName) || record.Reason is null || !Enum.IsDefined(record.Kind) ||
            !double.IsFinite(record.DurationSeconds) || record.DurationSeconds < 0 || record.DurationSeconds > TimeSpan.MaxValue.TotalSeconds ||
            record.DeviceCount < 1 || !double.IsFinite(record.PeakA) || record.PeakA is < 0 or > 100 ||
            !double.IsFinite(record.PeakB) || record.PeakB is < 0 or > 100)
            throw new LocalizedArgumentException("Error.InvalidRecords");
    }
}
