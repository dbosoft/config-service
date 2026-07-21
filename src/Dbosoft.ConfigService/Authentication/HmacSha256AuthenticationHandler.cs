using System.Globalization;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dbosoft.ConfigService.Authentication;

/// <summary>
/// ASP.NET Core authentication handler implementing the Azure App
/// Configuration HMAC-SHA256 scheme. Validates the Authorization header,
/// the body content hash, the request date, and the credential, then
/// produces a ClaimsPrincipal carrying the credential id and role.
/// </summary>
public sealed class HmacSha256AuthenticationHandler : AuthenticationHandler<HmacSchemeOptions>
{
    private readonly ICredentialStore _credentials;

    // A syntactically valid base64 key used to compute a throw-away signature
    // when the presented credential does not exist (or cannot be used). Doing
    // the same HMAC work in that case keeps timing and the response identical
    // whether or not the credential exists, so this endpoint can't be used to
    // enumerate valid credential ids.
    private static readonly string DummySecretBase64 = Convert.ToBase64String(new byte[32]);

    public HmacSha256AuthenticationHandler(
        IOptionsMonitor<HmacSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        ICredentialStore credentials)
        : base(options, logger, encoder)
    {
        _credentials = credentials;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("Authorization", out var authHeaderValues))
        {
            return AuthenticateResult.NoResult();
        }

        var authHeader = authHeaderValues.ToString();
        var prefix = HmacSignature.SchemeName + " ";
        if (!authHeader.StartsWith(prefix, StringComparison.Ordinal))
        {
            return AuthenticateResult.NoResult();
        }

        var parameters = HmacSignature.ParseAuthorizationParameters(authHeader[prefix.Length..]);
        if (parameters == null)
        {
            return Fail("Malformed Authorization header.", LogLevel.Information);
        }

        var signedHeaderNames = parameters.SignedHeaders.Split(';', StringSplitOptions.RemoveEmptyEntries);

        // Required: host, x-ms-content-sha256, plus a date header (either
        // x-ms-date or Date — the Microsoft Azure SDK uses Date by default).
        if (!signedHeaderNames.Contains("host", StringComparer.OrdinalIgnoreCase))
        {
            return Fail("SignedHeaders missing 'host'.", LogLevel.Information);
        }
        if (!signedHeaderNames.Contains("x-ms-content-sha256", StringComparer.OrdinalIgnoreCase))
        {
            return Fail("SignedHeaders missing 'x-ms-content-sha256'.", LogLevel.Information);
        }
        var hasXmsDate = signedHeaderNames.Contains("x-ms-date", StringComparer.OrdinalIgnoreCase);
        var hasDate = signedHeaderNames.Contains("date", StringComparer.OrdinalIgnoreCase);
        if (!hasXmsDate && !hasDate)
        {
            return Fail("SignedHeaders missing date header (expected 'x-ms-date' or 'date').", LogLevel.Information);
        }

        // Date validation. x-ms-date takes precedence over Date per spec.
        var dateString = Request.Headers.TryGetValue("x-ms-date", out var xms)
            ? xms.ToString()
            : Request.Headers.TryGetValue("Date", out var d) ? d.ToString() : null;

        if (string.IsNullOrEmpty(dateString))
        {
            return Fail("Missing date header.", LogLevel.Information);
        }

        if (!DateTimeOffset.TryParse(
                dateString,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var requestDate))
        {
            return Fail("Invalid date header.", LogLevel.Information);
        }

        if ((DateTimeOffset.UtcNow - requestDate).Duration() > Options.MaxClockSkew)
        {
            return Fail("Date header outside allowed clock skew.", LogLevel.Information);
        }

        // Body hash. Buffer so the downstream pipeline can re-read.
        if (!Request.Headers.TryGetValue("x-ms-content-sha256", out var clientHashValues))
        {
            return Fail("Missing x-ms-content-sha256 header.", LogLevel.Information);
        }

        var clientHash = clientHashValues.ToString();
        Request.EnableBuffering();
        var bodyBytes = await ReadBodyAsync(Request, Context.RequestAborted).ConfigureAwait(false);
        var actualHash = HmacSignature.ComputeContentHash(bodyBytes);
        if (!HmacSignature.SignaturesEqual(actualHash, clientHash))
        {
            return Fail("Body hash does not match x-ms-content-sha256 header.", LogLevel.Information);
        }

        var credential = await _credentials.GetByIdAsync(parameters.CredentialId, Context.RequestAborted)
            .ConfigureAwait(false);

        // Reconstruct string-to-sign from the headers as actually present.
        var headerValues = new List<string>(signedHeaderNames.Length);
        foreach (var name in signedHeaderNames)
        {
            string? value;
            if (string.Equals(name, "host", StringComparison.OrdinalIgnoreCase))
            {
                value = Request.Host.Value;
            }
            else if (Request.Headers.TryGetValue(name, out var hv))
            {
                value = hv.ToString();
            }
            else
            {
                return Fail($"Signed header '{name}' missing from request.", LogLevel.Information);
            }

            headerValues.Add(value ?? "");
        }

        // Use the raw HTTP request target so the server reproduces the exact
        // byte sequence the client signed. Request.GetEncodedPathAndQuery()
        // round-trips through PathString which decodes percent-encoded sub-
        // delims like "%3A" back into ":", breaking signature parity for any
        // key that contains characters the framework decides are "safe".
        var rawTarget = Context.Features.Get<IHttpRequestFeature>()?.RawTarget;
        var pathAndQuery = string.IsNullOrEmpty(rawTarget)
            ? Request.GetEncodedPathAndQuery()
            : rawTarget;
        var stringToSign = HmacSignature.BuildStringToSign(Request.Method, pathAndQuery, headerValues);

        // Compute the expected signature even when the credential is unknown,
        // revoked, or expired (using a dummy key in that case) so the work done
        // and the response returned are identical regardless of whether the
        // credential exists. The specific reason is logged server-side (see
        // Fail) but never returned to the caller (see HandleChallengeAsync),
        // so the two failure modes are indistinguishable from the outside.
        var credentialUsable = credential is { IsRevoked: false, IsExpired: false };
        var secretBase64 = credentialUsable ? credential!.SecretBase64 : DummySecretBase64;

        string expectedSignature;
        try
        {
            expectedSignature = HmacSignature.ComputeSignature(stringToSign, secretBase64);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentNullException)
        {
            // Stored secret is missing or not valid base64. Still spend
            // comparable work so this looks identical to any other failure
            // from the outside.
            expectedSignature = HmacSignature.ComputeSignature(stringToSign, DummySecretBase64);
            credentialUsable = false;
        }

        var signatureMatches = HmacSignature.SignaturesEqual(expectedSignature, parameters.Signature);
        if (!credentialUsable || !signatureMatches)
        {
            return Fail(credential is null
                ? "Unknown credential id."
                : !credentialUsable
                    ? "Credential is revoked, expired, or has an invalid stored secret."
                    : "Signature mismatch.");
        }

        var authenticated = credential!;
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, authenticated.Id),
            new Claim(ClaimTypes.Name, authenticated.Id),
            new Claim(ClaimTypes.Role, authenticated.Role.ToString()),
        };
        var identity = new ClaimsIdentity(claims, HmacSignature.SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, HmacSignature.SchemeName);
        return AuthenticateResult.Success(ticket);
    }

    private AuthenticateResult Fail(string reason, LogLevel level = LogLevel.Warning)
    {
        // Log the specific reason for operators (this is the only place a
        // failed authentication is recorded) but never surface it to the
        // caller. Routine malformed/stale requests are logged at Information;
        // Warning is reserved for the security-relevant credential and
        // signature failures so a flood of the former can't drown out the
        // signal that someone is probing credentials or forging signatures.
        Logger.Log(
            level,
            "HMAC authentication failed: {Reason} (remoteIp={RemoteIp})",
            reason,
            Context.Connection.RemoteIpAddress?.ToString() ?? "unknown");
        return AuthenticateResult.Fail(reason);
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        // Deliberately generic: the specific failure reason is logged
        // server-side but never returned, so the endpoint can't be used to
        // enumerate credential ids or otherwise probe why a request failed.
        Response.Headers.WWWAuthenticate = HmacSignature.SchemeName;
        return Task.CompletedTask;
    }

    private static async Task<byte[]> ReadBodyAsync(HttpRequest request, CancellationToken ct)
    {
        // Caller has already called EnableBuffering(). Rewind, copy out the
        // bytes, then rewind again so endpoint binding can re-read the body.
        // Bodies in this service are small (single key-value PUTs); buffering
        // fully into memory is fine.
        if (request.Body.CanSeek)
        {
            request.Body.Position = 0;
        }

        using var ms = new MemoryStream();
        await request.Body.CopyToAsync(ms, ct).ConfigureAwait(false);

        if (request.Body.CanSeek)
        {
            request.Body.Position = 0;
        }

        return ms.ToArray();
    }

}
