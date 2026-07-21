using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using Dbosoft.ConfigService.Endpoints;
using Xunit;

namespace Dbosoft.ConfigService.IntegrationTests;

public class AdminEndpointsTests : IClassFixture<ConfigServiceWebAppFactory>
{
    private readonly ConfigServiceWebAppFactory _factory;

    public AdminEndpointsTests(ConfigServiceWebAppFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task PUT_creates_a_value_that_subsequent_GET_returns()
    {
        var client = _factory.CreateAdminClient();

        var put = await client.PutAsJsonAsync(
            "/admin/kv",
            new AdminPutKeyValueRequest { Key = "Scim:BackendMode", Value = "EntraId" });
        put.EnsureSuccessStatusCode();

        // Use the LIST endpoint with a key filter rather than a per-key
        // GET — TestServer's URI normalization decodes "%3A" before the
        // server sees it, so the per-key URL form requires real Kestrel.
        // The list call is what the Microsoft provider actually uses.
        var get = await client.GetAsync("/kv?key=Scim%3ABackendMode");
        get.EnsureSuccessStatusCode();

        var listed = await get.Content.ReadFromJsonAsync<KeyValueListWireDto>();
        listed.Should().NotBeNull();
        listed!.Items.Should().ContainSingle(i => i.Key == "Scim:BackendMode" && i.Value == "EntraId");
    }

    [Fact]
    public async Task DELETE_removes_a_value()
    {
        var client = _factory.CreateAdminClient();

        await client.PutAsJsonAsync(
            "/admin/kv",
            new AdminPutKeyValueRequest { Key = "to-delete", Value = "doomed" });

        var del = new HttpRequestMessage(HttpMethod.Delete, "/admin/kv")
        {
            Content = JsonContent.Create(new AdminDeleteKeyValueRequest { Key = "to-delete" }),
        };
        var delResp = await client.SendAsync(del);
        delResp.EnsureSuccessStatusCode();

        var get = await client.GetAsync("/kv/to-delete");
        get.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Audit_log_records_PUT_with_old_and_new_value()
    {
        var client = _factory.CreateAdminClient();

        await client.PutAsJsonAsync(
            "/admin/kv",
            new AdminPutKeyValueRequest { Key = "audit-test", Value = "first" });
        await client.PutAsJsonAsync(
            "/admin/kv",
            new AdminPutKeyValueRequest { Key = "audit-test", Value = "second" });

        var audit = await client.GetFromJsonAsync<List<AuditWireDto>>("/admin/audit?key=audit-test");

        audit.Should().NotBeNull();
        audit!.Should().HaveCount(2);
        audit[0].NewValue.Should().Be("first");
        audit[0].OldValue.Should().BeNull();
        audit[1].NewValue.Should().Be("second");
        audit[1].OldValue.Should().Be("first");
    }

    [Fact]
    public async Task Unauthenticated_request_returns_401()
    {
        using var unauthClient = _factory.CreateClient();

        var resp = await unauthClient.GetAsync("/kv/anything");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private sealed class KeyValueWireDto
    {
        [System.Text.Json.Serialization.JsonPropertyName("key")]
        public string Key { get; set; } = "";

        [System.Text.Json.Serialization.JsonPropertyName("value")]
        public string? Value { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("etag")]
        public string? ETag { get; set; }
    }

    private sealed class KeyValueListWireDto
    {
        [System.Text.Json.Serialization.JsonPropertyName("items")]
        public List<KeyValueWireDto> Items { get; set; } = new();
    }

    private sealed class AuditWireDto
    {
        public string Action { get; set; } = "";
        public string Key { get; set; } = "";
        public string? OldValue { get; set; }
        public string? NewValue { get; set; }
    }
}
