using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using Dbosoft.ConfigService.Authentication;
using Dbosoft.ConfigService.Endpoints;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Dbosoft.ConfigService.IntegrationTests;

/// <summary>
/// Exercises the security-critical failure branches of the HMAC handler and
/// the admin authorization policy against the real request pipeline: tampered
/// signature, tampered body, stale date, missing signed headers, revoked and
/// expired credentials, and a Reader credential calling an Admin-only
/// endpoint. Before these tests the only auth coverage was "no header => 401".
/// </summary>
public class NegativeAuthTests : IClassFixture<NegativeAuthTests.SeededFactory>
{
    private readonly SeededFactory _factory;

    public NegativeAuthTests(SeededFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Valid_admin_signature_succeeds()
    {
        var client = _factory.CreateSignedClient(_factory.AdminId, _factory.AdminSecret);
        var resp = await client.GetAsync("/kv");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Tampered_signature_returns_401()
    {
        var req = new HttpRequestMessage(HttpMethod.Get, Abs("/kv"));
        Sign(req, _factory.AdminId, _factory.AdminSecret, signatureOverride: "not-the-real-signature");

        var resp = await _factory.CreateClient().SendAsync(req);
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Tampered_body_content_hash_returns_401()
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(
            new AdminPutKeyValueRequest { Key = "x", Value = "y" });
        var req = new HttpRequestMessage(HttpMethod.Put, Abs("/admin/kv"))
        {
            Content = new ByteArrayContent(body),
        };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        // Sign with a content hash that does not match the actual body bytes.
        Sign(req, _factory.AdminId, _factory.AdminSecret, body: body,
            contentHashOverride: Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes("different"))));

        var resp = await _factory.CreateClient().SendAsync(req);
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Stale_date_outside_clock_skew_returns_401()
    {
        var req = new HttpRequestMessage(HttpMethod.Get, Abs("/kv"));
        Sign(req, _factory.AdminId, _factory.AdminSecret,
            dateOverride: DateTimeOffset.UtcNow.AddHours(-1).ToString("R"));

        var resp = await _factory.CreateClient().SendAsync(req);
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Missing_host_in_signed_headers_returns_401()
    {
        var req = new HttpRequestMessage(HttpMethod.Get, Abs("/kv"));
        Sign(req, _factory.AdminId, _factory.AdminSecret,
            signedHeaders: "x-ms-date;x-ms-content-sha256");

        var resp = await _factory.CreateClient().SendAsync(req);
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Revoked_credential_returns_401()
    {
        var client = _factory.CreateSignedClient(_factory.RevokedId, _factory.RevokedSecret);
        var resp = await client.GetAsync("/kv");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Expired_credential_returns_401()
    {
        var client = _factory.CreateSignedClient(_factory.ExpiredId, _factory.ExpiredSecret);
        var resp = await client.GetAsync("/kv");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Unknown_credential_returns_401_without_disclosing_the_reason()
    {
        var req = new HttpRequestMessage(HttpMethod.Get, Abs("/kv"));
        Sign(req, "no-such-credential", _factory.AdminSecret);

        var resp = await _factory.CreateClient().SendAsync(req);

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        // The challenge must not leak why it failed (credential-enumeration
        // oracle). A bare scheme name with no error_description is expected.
        var challenge = resp.Headers.WwwAuthenticate.ToString();
        challenge.Should().Be("HMAC-SHA256");
        challenge.Should().NotContain("error_description");
    }

    [Fact]
    public async Task Unknown_credential_and_bad_signature_responses_are_indistinguishable()
    {
        // Unknown credential id, otherwise well-formed request.
        var unknownReq = new HttpRequestMessage(HttpMethod.Get, Abs("/kv"));
        Sign(unknownReq, "no-such-credential", _factory.AdminSecret);
        var unknown = await _factory.CreateClient().SendAsync(unknownReq);

        // Known credential id, deliberately wrong signature.
        var badSigReq = new HttpRequestMessage(HttpMethod.Get, Abs("/kv"));
        Sign(badSigReq, _factory.AdminId, _factory.AdminSecret, signatureOverride: "not-the-real-signature");
        var badSig = await _factory.CreateClient().SendAsync(badSigReq);

        // The whole point of the oracle fix: an attacker must not be able to
        // tell "that credential exists but I signed wrong" from "no such
        // credential". Status and challenge header must match, and neither may
        // carry a distinguishing body.
        badSig.StatusCode.Should().Be(unknown.StatusCode);
        badSig.Headers.WwwAuthenticate.ToString().Should().Be(unknown.Headers.WwwAuthenticate.ToString());
        (await badSig.Content.ReadAsStringAsync()).Should().Be(await unknown.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Reader_credential_may_read_but_not_write()
    {
        var reader = _factory.CreateSignedClient(_factory.ReaderId, _factory.ReaderSecret);

        var read = await reader.GetAsync("/kv");
        read.StatusCode.Should().Be(HttpStatusCode.OK, "a Reader is authenticated for read endpoints");

        var write = await reader.PutAsJsonAsync(
            "/admin/kv", new AdminPutKeyValueRequest { Key = "nope", Value = "denied" });
        write.StatusCode.Should().Be(HttpStatusCode.Forbidden, "a Reader lacks the Admin role");
    }

    private Uri Abs(string relative) => new(_factory.Server.BaseAddress, relative);

    /// <summary>
    /// Signs <paramref name="req"/> with the Azure HMAC scheme, exposing knobs
    /// so individual tests can tamper with exactly one input.
    /// </summary>
    private static void Sign(
        HttpRequestMessage req,
        string credentialId,
        string secretBase64,
        byte[]? body = null,
        string? dateOverride = null,
        string? contentHashOverride = null,
        string? signatureOverride = null,
        string signedHeaders = "x-ms-date;host;x-ms-content-sha256")
    {
        body ??= Array.Empty<byte>();
        var date = dateOverride ?? DateTimeOffset.UtcNow.ToString("R");
        var contentHash = contentHashOverride ?? Convert.ToBase64String(SHA256.HashData(body));
        var host = req.RequestUri!.Authority;
        var pathAndQuery = req.RequestUri.PathAndQuery;
        var method = req.Method.Method.ToUpperInvariant();

        req.Headers.TryAddWithoutValidation("x-ms-date", date);
        req.Headers.TryAddWithoutValidation("x-ms-content-sha256", contentHash);

        var headerValues = signedHeaders.Split(';').Select(h => h switch
        {
            "x-ms-date" => date,
            "host" => host,
            "x-ms-content-sha256" => contentHash,
            _ => "",
        });
        var stringToSign = $"{method}\n{pathAndQuery}\n{string.Join(";", headerValues)}";

        string signature;
        if (signatureOverride != null)
        {
            signature = signatureOverride;
        }
        else
        {
            using var hmac = new HMACSHA256(Convert.FromBase64String(secretBase64));
            signature = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(stringToSign)));
        }

        req.Headers.Authorization = new AuthenticationHeaderValue(
            "HMAC-SHA256",
            $"Credential={credentialId}&SignedHeaders={signedHeaders}&Signature={signature}");
    }

    /// <summary>
    /// Web-app factory that pre-seeds <c>credentials.json</c> with admin,
    /// reader, revoked, and expired credentials before the host starts, so the
    /// store already holds them (the bootstrap path only fires on an empty
    /// store).
    /// </summary>
    public sealed class SeededFactory : WebApplicationFactory<Program>
    {
        public string AdminId => "admin";
        public string AdminSecret { get; } = Secret("admin-secret-key-32-bytes-long!!");
        public string ReaderId => "reader";
        public string ReaderSecret { get; } = Secret("reader-secret-key-32-bytes-lng!!");
        public string RevokedId => "revoked-admin";
        public string RevokedSecret { get; } = Secret("revoked-secret-key-32-byte-long!");
        public string ExpiredId => "expired-admin";
        public string ExpiredSecret { get; } = Secret("expired-secret-key-32-byte-long!");

        private readonly string _dataDirectory =
            Path.Combine(Path.GetTempPath(), "configservice-negauth-" + Guid.NewGuid().ToString("N"));

        public SeededFactory()
        {
            Directory.CreateDirectory(_dataDirectory);
            var now = DateTimeOffset.UtcNow;

            // Seed real Credential objects serialized with the same options the
            // store uses to read them (camelCase + string enums), so the
            // fixture tracks the production wire shape instead of relying on a
            // hand-rolled JSON layout that happens to match.
            var doc = new StoreFileShape
            {
                Credentials =
                {
                    new Credential { Id = AdminId, SecretBase64 = AdminSecret, Role = CredentialRole.Admin, CreatedAt = now },
                    new Credential { Id = ReaderId, SecretBase64 = ReaderSecret, Role = CredentialRole.Reader, CreatedAt = now },
                    new Credential { Id = RevokedId, SecretBase64 = RevokedSecret, Role = CredentialRole.Admin, CreatedAt = now, RevokedAt = now.AddMinutes(-5) },
                    new Credential { Id = ExpiredId, SecretBase64 = ExpiredSecret, Role = CredentialRole.Admin, CreatedAt = now, ExpiresAt = now.AddMinutes(-5) },
                },
            };

            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                Converters = { new JsonStringEnumConverter() },
            };
            File.WriteAllText(
                Path.Combine(_dataDirectory, "credentials.json"),
                JsonSerializer.Serialize(doc, options));
        }

        /// <summary>Mirrors the store's on-disk envelope (version + list).</summary>
        private sealed class StoreFileShape
        {
            public int Version { get; set; } = 1;
            public List<Credential> Credentials { get; init; } = new();
        }

        public HttpClient CreateSignedClient(string id, string secret)
        {
            var inner = Server.CreateHandler();
            return new HttpClient(new HmacClientHandler(id, secret, inner)) { BaseAddress = Server.BaseAddress };
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration(cfg =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConfigService:DataDirectory"] = _dataDirectory,
                });
            });
        }

        private static string Secret(string raw) => Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && Directory.Exists(_dataDirectory))
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
    }
}
