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
