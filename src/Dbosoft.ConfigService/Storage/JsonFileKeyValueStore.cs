using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace Dbosoft.ConfigService.Storage;

/// <summary>
/// Single-file JSON-backed key-value store. Holds the working set in memory and
/// writes the entire file on every successful write. Suitable for small-to-medium
/// configuration sets (hundreds to a few thousand keys); not designed for high
/// write volume.
///
/// <para>
/// A write keeps a copy of the last good file (<c>values.json.bak</c>), writes the
/// new state to a temp file flushed through to the disk, and renames it over the
/// file. The rename replaces the file in one step where the file system offers
/// that; where it does not, or the machine stops between the steps, the next start
/// finds the file missing or unreadable and takes the copy — the state before
/// the write that did not finish.
/// </para>
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

    public async Task<(IReadOnlyList<KeyValueEntry> Before, IReadOnlyList<KeyValueEntry> After)> ReplaceAsync(
        string? label,
        IReadOnlyList<KeyValueEntry> entries,
        CancellationToken ct = default)
    {
        var normalized = NormalizeLabelForStorage(label);
        var duplicate = entries.GroupBy(e => e.Key, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException($"The key '{duplicate.Key}' is given more than once.", nameof(entries));
        }

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var before = _data.Entries.Where(e => e.Label == normalized).ToList();
            var now = DateTimeOffset.UtcNow;
            var after = new List<KeyValueEntry>();
            foreach (var entry in entries)
            {
                var current = before.FirstOrDefault(e => string.Equals(e.Key, entry.Key, StringComparison.Ordinal));
                // An entry written unchanged keeps its ETag, so a reader's If-None-Match still holds.
                var unchanged = current is not null
                                && current.Value == entry.Value
                                && current.ContentType == entry.ContentType
                                && current.Tags.Count == entry.Tags.Count
                                && !current.Tags.Except(entry.Tags).Any();
                after.Add(unchanged
                    ? Clone(current!)
                    : new KeyValueEntry
                    {
                        Key = entry.Key,
                        Label = normalized,
                        Value = entry.Value,
                        ContentType = entry.ContentType,
                        Tags = new Dictionary<string, string>(entry.Tags),
                        ETag = NextEtagLocked(),
                        LastModified = now,
                    });
            }

            _data.Entries.RemoveAll(e => e.Label == normalized);
            _data.Entries.AddRange(after);
            await SaveOrRevertLockedAsync(ct).ConfigureAwait(false);
            return (before.Select(Clone).ToList(), after.Select(Clone).ToList());
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
            if (File.Exists(_filePath))
            {
                File.Copy(_filePath, BackupPath(_filePath), overwrite: true);
            }

            await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(fs, _data, JsonOptions, ct).ConfigureAwait(false);
                // Through the operating system's cache to the disk, before the rename makes it the file.
                fs.Flush(flushToDisk: true);
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
        var backup = BackupPath(path);
        if (!File.Exists(path))
        {
            // Missing while a copy exists: a write stopped between giving up the file and the rename.
            return File.Exists(backup) ? Read(backup) : new StoreFile();
        }

        try
        {
            return Read(path);
        }
        catch (JsonException) when (File.Exists(backup))
        {
            // A file system without an atomic rename can leave a partly written file behind.
            return Read(backup);
        }
    }

    private static StoreFile Read(string path)
    {
        using var fs = File.OpenRead(path);
        return JsonSerializer.Deserialize<StoreFile>(fs, JsonOptions) ?? new StoreFile();
    }

    private static string BackupPath(string path) => path + ".bak";

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
