using System.Text.Json;
using YokoWave.Core.Localization;

namespace YokoWave.Pipelines;

internal sealed class JsonStateFile<T>(string path, Func<T, bool> validate)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _loadFailed;
    public async Task<T> LoadAsync(T empty)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!File.Exists(path))
                return empty;
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, true);
            var state = await JsonSerializer.DeserializeAsync<T>(stream, PipelineJson.Options).ConfigureAwait(false);
            if (state is null || !validate(state))
                throw new JsonException();
            return state;
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        {
            _loadFailed = true;
            throw new LocalizedIOException("Pipeline.Error.Storage", error);
        }
        finally
        {
            _gate.Release();
        }
    }
    public async Task SaveAsync(T state)
    {
        if (!validate(state))
            throw new LocalizedArgumentException("Pipeline.Error.Document");
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(state, PipelineJson.Options);
        await _gate.WaitAsync().ConfigureAwait(false);
        string? temporary = null;
        Exception? primary = null;
        try
        {
            if (_loadFailed)
                throw new LocalizedOperationException("Pipeline.Error.Storage");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllBytesAsync(temporary, bytes).ConfigureAwait(false);
            File.Move(temporary, path, true);
            temporary = null;
        }
        catch (Exception error)
        {
            primary = error;
            throw;
        }
        finally
        {
            try
            {
                if (temporary is not null && File.Exists(temporary))
                    File.Delete(temporary);
            }
            catch (Exception cleanup) when (primary is not null)
            {
                throw new LocalizedAggregateException("Error.Cleanup", [primary, cleanup]);
            }
            finally
            {
                _gate.Release();
            }
        }
    }
}
