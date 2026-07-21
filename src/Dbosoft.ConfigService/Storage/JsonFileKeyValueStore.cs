using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace Dbosoft.ConfigService.Storage;

/// <summary>
/// Single-file JSON-backed key-value store. Holds the working set in memory,
/// serializes the entire file on every successful write via a temp-file +
/// rename for atomicity. Suitable for small-to-medium configuration sets
/// (hundreds to a few thousand keys); not designed for high write volume.
/// </summary>
public sealed class JsonFileKeyValueStore : IKeyValueStore, IDisposable
{
    private readonly string _filePath;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly StoreFile _data;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public JsonFileKeyValueStore(IOptions<ConfigServiceOptions> options)
    {
        var dir = options.Value.DataDirectory;
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "values.json");
        _data = LoadOrCreate(_filePath);
    }

    public async Task<KeyValueEntry?> GetAsync(string key, string? label, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var match = _data.Entries.FirstOrDefault(e => Matches(e, key, label));
            return match == null ? null : Clone(match);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<KeyValueEntry>> ListAsync(
        string? keyFilter,
        string? labelFilter,
        CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            IEnumerable<KeyValueEntry> q = _data.Entries;

            if (!KeyValueFilter.IsMatchAll(keyFilter))
            {
                q = q.Where(e => KeyValueFilter.Matches(e.Key, keyFilter));
            }

            // Per the Azure App Configuration protocol the "no label"
            // sentinel is the literal NUL character (URL-encoded as %00).
            // The empty string is treated the same way for friendliness with
            // hand-built clients.
            if (labelFilter == "" || labelFilter == "\0")
            {
                q = q.Where(e => e.Label == null);
            }
            else if (!KeyValueFilter.IsMatchAll(labelFilter))
            {
                q = q.Where(e => KeyValueFilter.Matches(e.Label ?? "", labelFilter));
            }

            return q
                .OrderBy(e => e.Key, StringComparer.Ordinal)
                .ThenBy(e => e.Label ?? "", StringComparer.Ordinal)
                .Select(Clone)
                .ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<KeyValueEntry> SetAsync(
        string key,
        string? label,
        string? value,
        string? contentType,
        IDictionary<string, string>? tags,
        CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var existing = _data.Entries.FirstOrDefault(e => Matches(e, key, label));
            var now = DateTimeOffset.UtcNow;
            var etag = NextEtagLocked();

            if (existing == null)
            {
                existing = new KeyValueEntry
                {
                    Key = key,
                    Label = NormalizeLabelForStorage(label),
                };
                _data.Entries.Add(existing);
            }

            existing.Value = value;
            existing.ContentType = contentType;
            existing.Tags = tags == null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string>(tags);
            existing.ETag = etag;
            existing.LastModified = now;

            await SaveOrRevertLockedAsync(ct).ConfigureAwait(false);
            return Clone(existing);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<KeyValueEntry?> DeleteAsync(string key, string? label, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var existing = _data.Entries.FirstOrDefault(e => Matches(e, key, label));
            if (existing == null)
            {
                return null;
            }

            var snapshot = Clone(existing);
            _data.Entries.Remove(existing);
            await SaveOrRevertLockedAsync(ct).ConfigureAwait(false);
            return snapshot;
        }
        finally
        {
            _lock.Release();
        }
    }

    private string NextEtagLocked()
    {
        _data.ETagCounter++;
        return _data.ETagCounter.ToString("x16");
    }

    /// <summary>
    /// Persists the current in-memory state. If the write fails or is
    /// cancelled, the in-memory state is rolled back to the last successfully
    /// persisted state so memory and disk never diverge (callers have already
    /// mutated <c>_data</c> before this runs), then the original exception is
    /// rethrown.
    /// </summary>
    private async Task SaveOrRevertLockedAsync(CancellationToken ct)
    {
        try
        {
            await SaveLockedAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            RevertToDiskLocked();
            throw;
        }
    }

    private async Task SaveLockedAsync(CancellationToken ct)
    {
        var tmp = _filePath + ".tmp";
        try
        {
            await using (var fs = File.Create(tmp))
            {
                await JsonSerializer.SerializeAsync(fs, _data, JsonOptions, ct).ConfigureAwait(false);
            }

            File.Move(tmp, _filePath, overwrite: true);
        }
        catch
        {
            // Don't leave a partial temp file behind on failure/cancellation.
            try
            {
                if (File.Exists(tmp))
                {
                    File.Delete(tmp);
                }
            }
            catch
            {
                // Best-effort cleanup; the next successful save overwrites it.
            }

            throw;
        }
    }

    /// <summary>
    /// Restores the in-memory working set from the last-persisted file (the
    /// atomic temp-file+rename in <see cref="SaveLockedAsync"/> guarantees the
    /// real file is always a complete prior state, never a partial write).
    /// </summary>
    private void RevertToDiskLocked()
    {
        var reloaded = LoadOrCreate(_filePath);
        _data.Version = reloaded.Version;
        _data.ETagCounter = reloaded.ETagCounter;
        _data.Entries.Clear();
        _data.Entries.AddRange(reloaded.Entries);
    }

    private static StoreFile LoadOrCreate(string path)
    {
        if (!File.Exists(path))
        {
            return new StoreFile();
        }

        using var fs = File.OpenRead(path);
        var loaded = JsonSerializer.Deserialize<StoreFile>(fs, JsonOptions);
        return loaded ?? new StoreFile();
    }

    private static bool Matches(KeyValueEntry e, string key, string? label)
    {
        if (!string.Equals(e.Key, key, StringComparison.Ordinal))
        {
            return false;
        }

        var entryLabel = e.Label;
        var queryLabel = IsNoLabel(label) ? null : label;
        return string.Equals(entryLabel, queryLabel, StringComparison.Ordinal);
    }

    private static string? NormalizeLabelForStorage(string? label)
        => IsNoLabel(label) ? null : label;

    // The Azure "no label" sentinel is the literal NUL character (sent as
    // %00). The empty string is treated the same for friendliness with
    // hand-built clients. Applied consistently on the single-key path (Get /
    // Set / Delete) as well as in the list filter.
    private static bool IsNoLabel(string? label)
        => string.IsNullOrEmpty(label) || label == "\0";

    private static KeyValueEntry Clone(KeyValueEntry source) => new()
    {
        Key = source.Key,
        Label = source.Label,
        Value = source.Value,
        ContentType = source.ContentType,
        ETag = source.ETag,
        LastModified = source.LastModified,
        Tags = new Dictionary<string, string>(source.Tags),
    };

    public void Dispose() => _lock.Dispose();

    private sealed class StoreFile
    {
        public int Version { get; set; } = 1;
        public long ETagCounter { get; set; }
        public List<KeyValueEntry> Entries { get; set; } = new();
    }
}
