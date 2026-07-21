using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace Dbosoft.ConfigService.IntegrationTests;

/// <summary>
/// Test-side HTTP client handler that signs outgoing requests with the
/// Azure App Configuration HMAC-SHA256 scheme. Mirrors what the production
/// Microsoft provider does, used for tests that exercise our admin API
/// (which the Microsoft provider does not call).
/// </summary>
public sealed class HmacClientHandler : DelegatingHandler
{
    private readonly string _credentialId;
    private readonly string _secretBase64;

    public HmacClientHandler(string credentialId, string secretBase64, HttpMessageHandler inner)
        : base(inner)
    {
        _credentialId = credentialId;
        _secretBase64 = secretBase64;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var bodyBytes = request.Content == null
            ? Array.Empty<byte>()
            : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

        var contentHash = Convert.ToBase64String(SHA256.HashData(bodyBytes));
        var date = DateTimeOffset.UtcNow.ToString("R");
        var host = request.RequestUri!.Authority;
        var pathAndQuery = request.RequestUri.PathAndQuery;
        var method = request.Method.Method.ToUpperInvariant();

        request.Headers.TryAddWithoutValidation("x-ms-date", date);
        request.Headers.TryAddWithoutValidation("x-ms-content-sha256", contentHash);

        var stringToSign = $"{method}\n{pathAndQuery}\n{date};{host};{contentHash}";
        var key = Convert.FromBase64String(_secretBase64);
        using var hmac = new HMACSHA256(key);
        var signature = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(stringToSign)));

        var authValue =
            $"Credential={_credentialId}&SignedHeaders=x-ms-date;host;x-ms-content-sha256&Signature={signature}";
        request.Headers.Authorization = new AuthenticationHeaderValue("HMAC-SHA256", authValue);

        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
