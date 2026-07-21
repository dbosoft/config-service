using System.Text;
using AwesomeAssertions;
using Dbosoft.ConfigService.Authentication;
using Microsoft.Extensions.Options;
using Xunit;

namespace Dbosoft.ConfigService.Tests;

public class JsonFileCredentialStoreTests : IDisposable
{
    private readonly string _dataDir;
    private readonly JsonFileCredentialStore _store;

    public JsonFileCredentialStoreTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "configservice-cred-tests-" + Guid.NewGuid().ToString("N"));
        var opts = Options.Create(new ConfigServiceOptions { DataDirectory = _dataDir });
        _store = new JsonFileCredentialStore(opts);
    }

    public void Dispose()
    {
        _store.Dispose();
        if (Directory.Exists(_dataDir))
        {
            Directory.Delete(_dataDir, true);
        }
    }

    private static string SecretBase64(string seed)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(seed.PadRight(32, '!')));

    [Fact]
    public async Task EnsureBootstrapAdminAsync_creates_credential_when_store_is_empty()
    {
        await _store.EnsureBootstrapAdminAsync("admin", SecretBase64("seed"));

        var loaded = await _store.GetByIdAsync("admin");
        loaded.Should().NotBeNull();
        loaded!.Role.Should().Be(CredentialRole.Admin);
        loaded.IsRevoked.Should().BeFalse();
        loaded.IsExpired.Should().BeFalse();
    }

    [Fact]
    public async Task EnsureBootstrapAdminAsync_is_no_op_when_store_already_has_credentials()
    {
        await _store.EnsureBootstrapAdminAsync("first", SecretBase64("a"));
        await _store.EnsureBootstrapAdminAsync("second", SecretBase64("b"));

        (await _store.GetByIdAsync("first")).Should().NotBeNull();
        (await _store.GetByIdAsync("second")).Should().BeNull();
    }

    [Fact]
    public async Task Credentials_persist_across_store_instances()
    {
        await _store.EnsureBootstrapAdminAsync("admin", SecretBase64("seed"));

        var opts = Options.Create(new ConfigServiceOptions { DataDirectory = _dataDir });
        using var fresh = new JsonFileCredentialStore(opts);

        var loaded = await fresh.GetByIdAsync("admin");
        loaded.Should().NotBeNull();
        loaded!.Role.Should().Be(CredentialRole.Admin);
    }
}
