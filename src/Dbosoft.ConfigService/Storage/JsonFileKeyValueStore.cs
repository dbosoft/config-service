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

            if (!IsAnyFilter(keyFilter))
            {
                q = q.Where(e => MatchesFilter(e.Key, keyFilter!));
            }

            // Per the Azure App Configuration protocol the "no label"
            // sentinel is the literal NUL character (URL-encoded as %00).
            // The empty string is treated the same way for friendliness with
            // hand-built clients.
            if (labelFilter == "" || labelFilter == "\0")
            {
                q = q.Where(e => e.Label == null);
            }
            else if (!IsAnyFilter(labelFilter))
            {
                q = q.Where(e => MatchesFilter(e.Label ?? "", labelFilter!));
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

            await SaveLockedAsync(ct).ConfigureAwait(false);
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
            await SaveLockedAsync(ct).ConfigureAwait(false);
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

    private async Task SaveLockedAsync(CancellationToken ct)
    {
        var tmp = _filePath + ".tmp";
        await using (var fs = File.Create(tmp))
        {
            await JsonSerializer.SerializeAsync(fs, _data, JsonOptions, ct).ConfigureAwait(false);
        }
        File.Move(tmp, _filePath, overwrite: true);
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
        var queryLabel = string.IsNullOrEmpty(label) ? null : label;
        return string.Equals(entryLabel, queryLabel, StringComparison.Ordinal);
    }

    private static string? NormalizeLabelForStorage(string? label)
        => string.IsNullOrEmpty(label) ? null : label;

    private static bool IsAnyFilter(string? filter)
        => filter == null || filter == "*";

    private static KeyValueEntry Clone(KeyValueEntry source) => new()
    {
        Key = source.Key,
        Label = source.Label,
        Value = source.Value,
        ContentType = source.ContentType,
        ETag = source.ETag,
        LastModified = source.LastModified,
        Locked = source.Locked,
        Tags = new Dictionary<string, string>(source.Tags),
    };

    private static bool MatchesFilter(string value, string filter)
    {
        if (filter.EndsWith('*'))
        {
            var prefix = filter[..^1];
            return value.StartsWith(prefix, StringComparison.Ordinal);
        }
        return string.Equals(value, filter, StringComparison.Ordinal);
    }

    public void Dispose() => _lock.Dispose();

    private sealed class StoreFile
    {
        public int Version { get; set; } = 1;
        public long ETagCounter { get; set; }
        public List<KeyValueEntry> Entries { get; set; } = new();
    }
}
