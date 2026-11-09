using System.Text;
using System.Text.Json;
using YokoWave.Core.Localization;

namespace YokoWave.Core;

public sealed class SessionJournal(string directory)
{
    public const int MaxFileBytes = 128 * 1024;
    private List<SessionRecord> _current = [];
    private List<SessionRecord> _previous = [];
    private readonly HashSet<Guid> _seen = [];

    public string FilePath { get; } = Path.Combine(directory, "sessions.jsonl");
    public string PreviousFilePath { get; } = Path.Combine(directory, "sessions.1.jsonl");
    public IReadOnlyList<SessionRecord> Retained => _previous.Concat(_current).OrderByDescending(record => record.StartedAt).ToArray();
    public bool IsDiscarded(Guid id) => _seen.Contains(id) && !_current.Any(record => record.Id == id) && !_previous.Any(record => record.Id == id);

    internal async Task LoadAsync(JsonSerializerOptions options)
    {
        _previous = await ReadAsync(PreviousFilePath, options).ConfigureAwait(false);
        _current = await ReadAsync(FilePath, options).ConfigureAwait(false);
        _seen.Clear();
        foreach (var record in _previous.Concat(_current))
        {
            if (!_seen.Add(record.Id))
                throw new LocalizedArgumentException("Error.InvalidRecords");
        }
    }

    internal async Task AppendAsync(IEnumerable<SessionRecord> records, JsonSerializerOptions options, Action<SessionRecord> onAppended)
    {
        foreach (var record in records.OrderBy(record => record.StartedAt))
        {
            if (_seen.Contains(record.Id))
                continue;
            ValidateRecord(record);
            byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(record, options) + "\n");
            if (bytes.Length > MaxFileBytes)
                throw new LocalizedArgumentException("Error.SessionFileSize");
            if (File.Exists(FilePath) && new FileInfo(FilePath).Length + bytes.Length > MaxFileBytes)
            {
                File.Move(FilePath, PreviousFilePath, true);
                _previous = _current;
                _current = [];
            }
            var stream = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, true);
            await using (stream.ConfigureAwait(false))
            {
                await stream.WriteAsync(bytes).ConfigureAwait(false);
            }
            _current.Add(record);
            _seen.Add(record.Id);
            onAppended(record);
        }
    }

    internal static void ValidateRecord(SessionRecord record)
    {
        static bool ValidPeak(double? peak) => peak is null || double.IsFinite(peak.Value) && peak is >= 0 and <= 100;
        if (record.Id == Guid.Empty || string.IsNullOrWhiteSpace(record.PipelineName) || record.Reason is null ||
            !double.IsFinite(record.DurationSeconds) || record.DurationSeconds < 0 || record.DurationSeconds > TimeSpan.MaxValue.TotalSeconds ||
            record.DeviceCount < 0 || !ValidPeak(record.PeakA) || !ValidPeak(record.PeakB) || !ValidPeak(record.PeakC))
        {
            throw new LocalizedArgumentException("Error.InvalidRecords");
        }
    }

    private static async Task<List<SessionRecord>> ReadAsync(string path, JsonSerializerOptions options)
    {
        if (!File.Exists(path))
            return [];
        if (new FileInfo(path).Length > MaxFileBytes)
            throw new LocalizedArgumentException("Error.SessionFileSize");
        List<SessionRecord> records = [];
        foreach (string line in await File.ReadAllLinesAsync(path).ConfigureAwait(false))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            var record = JsonSerializer.Deserialize<SessionRecord>(line, options) ?? throw new JsonException("Invalid session record.");
            ValidateRecord(record);
            records.Add(record);
        }
        return records;
    }
}
