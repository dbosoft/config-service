using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using Dbosoft.ConfigService.Endpoints;
using Xunit;

namespace Dbosoft.ConfigService.IntegrationTests;

public class KeyValueEndpointsTests : IClassFixture<ConfigServiceWebAppFactory>
{
    private readonly ConfigServiceWebAppFactory _factory;

    public KeyValueEndpointsTests(ConfigServiceWebAppFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task GET_returns_ETag_header_and_repeated_If_None_Match_returns_304()
    {
        var client = _factory.CreateAdminClient();

        await client.PutAsJsonAsync(
            "/admin/kv",
            new AdminPutKeyValueRequest { Key = "etag-test", Value = "v" });

        var first = await client.GetAsync("/kv/etag-test");
        first.EnsureSuccessStatusCode();
        var etag = first.Headers.ETag?.Tag;
        etag.Should().NotBeNullOrEmpty();

        var conditional = new HttpRequestMessage(HttpMethod.Get, "/kv/etag-test");
        conditional.Headers.TryAddWithoutValidation("If-None-Match", etag);
        var second = await client.SendAsync(conditional);
        second.StatusCode.Should().Be(HttpStatusCode.NotModified);
    }

    [Fact]
    public async Task Weak_If_None_Match_validator_is_accepted_and_returns_304()
    {
        var client = _factory.CreateAdminClient();

        await client.PutAsJsonAsync(
            "/admin/kv",
            new AdminPutKeyValueRequest { Key = "weak-etag-test", Value = "v" });

        var first = await client.GetAsync("/kv/weak-etag-test");
        first.EnsureSuccessStatusCode();
        var etag = first.Headers.ETag?.Tag;
        etag.Should().NotBeNullOrEmpty();

        // Present the ETag as a weak validator (W/ prefix); the server must
        // strip the prefix and still match.
        var conditional = new HttpRequestMessage(HttpMethod.Get, "/kv/weak-etag-test");
        conditional.Headers.TryAddWithoutValidation("If-None-Match", "W/" + etag);
        var second = await client.SendAsync(conditional);
        second.StatusCode.Should().Be(HttpStatusCode.NotModified);
    }

    [Fact]
    public async Task List_returns_items_envelope()
    {
        var client = _factory.CreateAdminClient();

        await client.PutAsJsonAsync(
            "/admin/kv",
            new AdminPutKeyValueRequest { Key = "list-a", Value = "1" });
        await client.PutAsJsonAsync(
            "/admin/kv",
            new AdminPutKeyValueRequest { Key = "list-b", Value = "2" });

        var resp = await client.GetAsync("/kv/?key=list-*");
        resp.EnsureSuccessStatusCode();

        var listed = await resp.Content.ReadFromJsonAsync<KeyValueListWireDto>();
        listed.Should().NotBeNull();
        listed!.Items.Should().HaveCount(2);
        listed.Items.Select(i => i.Key).Should().BeEquivalentTo(new[] { "list-a", "list-b" });
    }

    [Fact]
    public async Task GET_single_key_sends_the_vendor_kv_content_type()
    {
        var client = _factory.CreateAdminClient();
        await client.PutAsJsonAsync(
            "/admin/kv",
            new AdminPutKeyValueRequest { Key = "ct-test", Value = "v" });

        var resp = await client.GetAsync("/kv/ct-test");
        resp.EnsureSuccessStatusCode();

        resp.Content.Headers.ContentType!.MediaType
            .Should().Be("application/vnd.microsoft.appconfig.kv+json");
    }

    [Fact]
    public async Task List_sends_the_vendor_kvset_content_type()
    {
        var client = _factory.CreateAdminClient();
        await client.PutAsJsonAsync(
            "/admin/kv",
            new AdminPutKeyValueRequest { Key = "ctset-a", Value = "1" });

        var resp = await client.GetAsync("/kv?key=ctset-*");
        resp.EnsureSuccessStatusCode();

        resp.Content.Headers.ContentType!.MediaType
            .Should().Be("application/vnd.microsoft.appconfig.kvset+json");
    }

    [Fact]
    public async Task List_with_percent_encoded_NUL_label_returns_only_unlabeled()
    {
        var client = _factory.CreateAdminClient();
        await client.PutAsJsonAsync(
            "/admin/kv",
            new AdminPutKeyValueRequest { Key = "nul-1", Value = "no-label" });
        await client.PutAsJsonAsync(
            "/admin/kv",
            new AdminPutKeyValueRequest { Key = "nul-2", Label = "prod", Value = "prod-label" });

        // %00 is the Azure "no label" sentinel the Microsoft provider sends.
        var resp = await client.GetAsync("/kv/?key=nul-*&label=%00");
        resp.EnsureSuccessStatusCode();

        var listed = await resp.Content.ReadFromJsonAsync<KeyValueListWireDto>();
        listed!.Items.Should().ContainSingle(i => i.Key == "nul-1");
    }

    [Fact]
    public async Task List_with_empty_label_returns_only_unlabeled()
    {
        var client = _factory.CreateAdminClient();

        await client.PutAsJsonAsync(
            "/admin/kv",
            new AdminPutKeyValueRequest { Key = "labeltest-1", Value = "no-label" });
        await client.PutAsJsonAsync(
            "/admin/kv",
            new AdminPutKeyValueRequest { Key = "labeltest-2", Label = "prod", Value = "prod-label" });

        var resp = await client.GetAsync("/kv/?key=labeltest-*&label=");
        var listed = await resp.Content.ReadFromJsonAsync<KeyValueListWireDto>();

        listed!.Items.Should().HaveCount(1);
        listed.Items[0].Key.Should().Be("labeltest-1");
    }

    private sealed class KeyValueListWireDto
    {
        [System.Text.Json.Serialization.JsonPropertyName("items")]
        public List<KeyValueWireItem> Items { get; set; } = new();
    }

    private sealed class KeyValueWireItem
    {
        [System.Text.Json.Serialization.JsonPropertyName("key")]
        public string Key { get; set; } = "";

        [System.Text.Json.Serialization.JsonPropertyName("value")]
        public string? Value { get; set; }
    }
}
