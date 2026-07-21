using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace Dbosoft.ConfigService.Authentication;

/// <summary>
/// JSON file-backed credential store. Same shape as
/// <see cref="Storage.JsonFileKeyValueStore"/>: in-memory cache, atomic file
/// rewrite on change, single-process owner.
/// </summary>
public sealed class JsonFileCredentialStore : ICredentialStore, IDisposable
{
    private readonly string _filePath;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly StoreFile _data;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public JsonFileCredentialStore(IOptions<ConfigServiceOptions> options)
    {
        var dir = options.Value.DataDirectory;
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "credentials.json");
        _data = LoadOrCreate(_filePath);
    }

    public async Task<Credential?> GetByIdAsync(string id, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return _data.Credentials.FirstOrDefault(c =>
                string.Equals(c.Id, id, StringComparison.Ordinal));
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<Credential>> ListAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return _data.Credentials.ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task EnsureBootstrapAdminAsync(string id, string secretBase64, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_data.Credentials.Count > 0)
            {
                return;
            }

            _data.Credentials.Add(new Credential
            {
                Id = id,
                SecretBase64 = secretBase64,
                Role = CredentialRole.Admin,
                CreatedAt = DateTimeOffset.UtcNow,
                Description = "Bootstrap admin credential",
            });

            try
            {
                await SaveLockedAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                // Roll back the in-memory add so memory and disk stay in sync.
                RevertToDiskLocked();
                throw;
            }
        }
        finally
        {
            _lock.Release();
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
            try
            {
                if (File.Exists(tmp))
                {
                    File.Delete(tmp);
                }
            }
            catch
            {
                // Best-effort cleanup.
            }

            throw;
        }
    }

    private void RevertToDiskLocked()
    {
        var reloaded = LoadOrCreate(_filePath);
        _data.Version = reloaded.Version;
        _data.Credentials.Clear();
        _data.Credentials.AddRange(reloaded.Credentials);
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

    public void Dispose() => _lock.Dispose();

    private sealed class StoreFile
    {
        public int Version { get; set; } = 1;
        public List<Credential> Credentials { get; set; } = new();
    }
}
