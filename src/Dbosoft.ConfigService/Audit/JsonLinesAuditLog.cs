using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Dbosoft.ConfigService.Audit;

/// <summary>
/// Append-only JSON Lines audit log. Each entry is one JSON object on its
/// own line; the file is opened in append mode for every write so concurrent
/// reads see consistent boundaries. Single-process writer.
/// </summary>
public sealed class JsonLinesAuditLog : IAuditLog, IDisposable
{
    private readonly string _filePath;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public JsonLinesAuditLog(IOptions<ConfigServiceOptions> options)
    {
        var dir = options.Value.DataDirectory;
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "audit.jsonl");
    }

    public async Task AppendAsync(AuditEntry entry, CancellationToken ct = default)
    {
        var line = JsonSerializer.Serialize(entry, JsonOptions);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var fs = new FileStream(
                _filePath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 4096,
                useAsync: true);
            await using var writer = new StreamWriter(fs, new UTF8Encoding(false));
            await writer.WriteLineAsync(line.AsMemory(), ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async IAsyncEnumerable<AuditEntry> ReadAsync(
        string? keyFilter = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!File.Exists(_filePath))
        {
            yield break;
        }

        await using var fs = new FileStream(
            _filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 4096,
            useAsync: true);
        using var reader = new StreamReader(fs, Encoding.UTF8);

        while (!reader.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            AuditEntry? entry;
            try
            {
                entry = JsonSerializer.Deserialize<AuditEntry>(line, JsonOptions);
            }
            catch (JsonException)
            {
                continue;
            }

            if (entry == null)
            {
                continue;
            }

            if (keyFilter != null && !string.Equals(entry.Key, keyFilter, StringComparison.Ordinal))
            {
                continue;
            }

            yield return entry;
        }
    }

    public void Dispose() => _writeLock.Dispose();
}
