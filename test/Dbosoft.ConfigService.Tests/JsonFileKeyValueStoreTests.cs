using AwesomeAssertions;
using Dbosoft.ConfigService.Storage;
using Microsoft.Extensions.Options;
using Xunit;

namespace Dbosoft.ConfigService.Tests;

public class JsonFileKeyValueStoreTests : IDisposable
{
    private readonly string _dataDir;
    private readonly JsonFileKeyValueStore _store;

    public JsonFileKeyValueStoreTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "configservice-tests-" + Guid.NewGuid().ToString("N"));
        var opts = Options.Create(new ConfigServiceOptions { DataDirectory = _dataDir });
        _store = new JsonFileKeyValueStore(opts);
    }

    public void Dispose()
    {
        _store.Dispose();
        if (Directory.Exists(_dataDir))
        {
            Directory.Delete(_dataDir, true);
        }
    }

    [Fact]
    public async Task Set_then_Get_returns_the_stored_value()
    {
        await _store.SetAsync("Scim:BackendMode", null, "EntraId", null, null);

        var entry = await _store.GetAsync("Scim:BackendMode", null);

        entry.Should().NotBeNull();
        entry!.Value.Should().Be("EntraId");
        entry.ETag.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Get_returns_null_when_key_does_not_exist()
        => (await _store.GetAsync("missing", null)).Should().BeNull();

    [Fact]
    public async Task Set_assigns_a_new_ETag_on_each_write()
    {
        var first = await _store.SetAsync("k", null, "v1", null, null);
        var second = await _store.SetAsync("k", null, "v2", null, null);

        second.ETag.Should().NotBe(first.ETag);
    }

    [Fact]
    public async Task Label_null_and_empty_string_address_the_same_entry()
    {
        await _store.SetAsync("k", null, "via-null", null, null);

        var viaEmpty = await _store.GetAsync("k", "");

        viaEmpty.Should().NotBeNull();
        viaEmpty!.Value.Should().Be("via-null");
    }

    [Fact]
    public async Task Different_labels_are_distinct_entries()
    {
        await _store.SetAsync("k", null, "no-label", null, null);
        await _store.SetAsync("k", "prod", "prod-label", null, null);

        (await _store.GetAsync("k", null))!.Value.Should().Be("no-label");
        (await _store.GetAsync("k", "prod"))!.Value.Should().Be("prod-label");
    }

    [Fact]
    public async Task List_with_null_filter_returns_all()
    {
        await _store.SetAsync("a", null, "1", null, null);
        await _store.SetAsync("b", null, "2", null, null);
        await _store.SetAsync("c", "prod", "3", null, null);

        var all = await _store.ListAsync(null, null);

        all.Should().HaveCount(3);
    }

    [Fact]
    public async Task List_with_empty_label_returns_only_unlabeled()
    {
        await _store.SetAsync("a", null, "1", null, null);
        await _store.SetAsync("b", "prod", "2", null, null);

        var unlabeled = await _store.ListAsync(null, "");

        unlabeled.Should().HaveCount(1);
        unlabeled[0].Key.Should().Be("a");
    }

    [Fact]
    public async Task List_with_label_filter_returns_only_matching_label()
    {
        await _store.SetAsync("a", null, "1", null, null);
        await _store.SetAsync("b", "prod", "2", null, null);
        await _store.SetAsync("c", "dev", "3", null, null);

        var prod = await _store.ListAsync(null, "prod");

        prod.Should().HaveCount(1);
        prod[0].Key.Should().Be("b");
    }

    [Fact]
    public async Task List_with_key_prefix_returns_matching()
    {
        await _store.SetAsync("Scim:BackendMode", null, "EntraId", null, null);
        await _store.SetAsync("Scim:Tenant", null, "abc", null, null);
        await _store.SetAsync("Other:Key", null, "x", null, null);

        var scim = await _store.ListAsync("Scim:*", null);

        scim.Should().HaveCount(2);
        scim.Select(e => e.Key).Should().BeEquivalentTo(new[] { "Scim:BackendMode", "Scim:Tenant" });
    }

    [Fact]
    public async Task Delete_removes_the_entry_and_returns_the_snapshot()
    {
        await _store.SetAsync("k", null, "v", null, null);

        var deleted = await _store.DeleteAsync("k", null);

        deleted.Should().NotBeNull();
        deleted!.Value.Should().Be("v");
        (await _store.GetAsync("k", null)).Should().BeNull();
    }

    [Fact]
    public async Task Delete_returns_null_when_nothing_matches()
        => (await _store.DeleteAsync("missing", null)).Should().BeNull();

    [Fact]
    public async Task Values_persist_across_store_instances()
    {
        await _store.SetAsync("k", null, "persistent", null, null);

        var opts = Options.Create(new ConfigServiceOptions { DataDirectory = _dataDir });
        using var fresh = new JsonFileKeyValueStore(opts);

        var entry = await fresh.GetAsync("k", null);
        entry.Should().NotBeNull();
        entry!.Value.Should().Be("persistent");
    }
}
