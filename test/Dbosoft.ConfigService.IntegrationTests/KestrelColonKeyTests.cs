using System.Net;
using System.Net.Http.Json;
using System.Text;
using AwesomeAssertions;
using Dbosoft.ConfigService.Authentication;
using Dbosoft.ConfigService.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Dbosoft.ConfigService.IntegrationTests;

/// <summary>
/// The in-memory <c>TestServer</c> normalizes request URIs client-side, so it
/// cannot exercise the handler's <c>RawTarget</c> workaround (which exists so
/// that a key such as <c>Scim:BackendMode</c> requested as
/// <c>/kv/Scim%3ABackendMode</c> signs and verifies against the exact raw
/// target rather than the PathString-decoded <c>/kv/Scim:BackendMode</c>).
/// This test binds a real Kestrel socket so the encoded colon survives to the
/// server, giving that workaround its only end-to-end coverage. Revert the
/// RawTarget logic in the handler and this test fails with a 401.
/// </summary>
public sealed class KestrelColonKeyTests : IAsyncLifetime
{
    private const string AdminId = "kestrel-admin";
    private static readonly string AdminSecret =
        Convert.ToBase64String(Encoding.UTF8.GetBytes("kestrel-secret-key-32-bytes-lng!"));

    private readonly string _dataDirectory =
        Path.Combine(Path.GetTempPath(), "configservice-kestrel-" + Guid.NewGuid().ToString("N"));

    // Mirrors the cap the Host sets in Program.cs. Small enough that a test
    // can comfortably exceed it.
    private const long MaxBodyBytes = 1 * 1024 * 1024; // 1 MiB

    private WebApplication _app = null!;
    private Uri _baseAddress = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0"); // dynamic free port
        builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = MaxBodyBytes);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConfigService:DataDirectory"] = _dataDirectory,
            ["ConfigService:BootstrapAdminId"] = AdminId,
            ["ConfigService:BootstrapAdminSecret"] = AdminSecret,
        });

        builder.Services.AddConfigService(builder.Configuration);

        var app = builder.Build();
        await app.Services.GetRequiredService<ICredentialStore>()
            .EnsureBootstrapAdminAsync(AdminId, AdminSecret);

        app.UseAuthentication();
        app.UseAuthorization();
        app.MapConfigService();

        await app.StartAsync();
        _app = app;
        _baseAddress = new Uri(app.Urls.First());
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        if (Directory.Exists(_dataDirectory))
        {
            try
            {
                Directory.Delete(_dataDirectory, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup.
            }
        }
    }

    [Fact]
    public async Task Single_key_GET_with_percent_encoded_colon_authenticates_over_a_real_socket()
    {
        using var client = new HttpClient(new HmacClientHandler(AdminId, AdminSecret, new HttpClientHandler()))
        {
            BaseAddress = _baseAddress,
        };

        var put = await client.PutAsJsonAsync(
            "/admin/kv",
            new AdminPutKeyValueRequest { Key = "Scim:BackendMode", Value = "EntraId" });
        put.EnsureSuccessStatusCode();

        // The colon is percent-encoded in the path. Over a real socket the
        // server sees the raw target "/kv/Scim%3ABackendMode" and must sign
        // against that exact form.
        var get = await client.GetAsync("/kv/Scim%3ABackendMode");

        get.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await get.Content.ReadFromJsonAsync<KeyValueWireItem>();
        dto!.Value.Should().Be("EntraId");
    }

    [Fact]
    public async Task Oversized_body_is_rejected_before_the_auth_handler_buffers_it()
    {
        using var client = new HttpClient(new HmacClientHandler(AdminId, AdminSecret, new HttpClientHandler()))
        {
            BaseAddress = _baseAddress,
        };

        // A value comfortably larger than the 1 MiB cap. This must be rejected
        // by Kestrel as the body is read (413) rather than buffered whole into
        // memory by the auth handler's ReadBodyAsync. Only a real socket
        // enforces this — TestServer ignores Kestrel limits entirely.
        var huge = new string('x', (int)MaxBodyBytes + 1024);
        var resp = await client.PutAsJsonAsync(
            "/admin/kv",
            new AdminPutKeyValueRequest { Key = "too-big", Value = huge });

        resp.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
    }

    private sealed class KeyValueWireItem
    {
        [System.Text.Json.Serialization.JsonPropertyName("key")]
        public string Key { get; set; } = "";

        [System.Text.Json.Serialization.JsonPropertyName("value")]
        public string? Value { get; set; }
    }
}
