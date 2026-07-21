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
            return Fail("Malformed Authorization header.");
        }

        var signedHeaderNames = parameters.SignedHeaders.Split(';', StringSplitOptions.RemoveEmptyEntries);

        // Required: host, x-ms-content-sha256, plus a date header (either
        // x-ms-date or Date — the Microsoft Azure SDK uses Date by default).
        if (!signedHeaderNames.Contains("host", StringComparer.OrdinalIgnoreCase))
        {
            return Fail("SignedHeaders missing 'host'.");
        }
        if (!signedHeaderNames.Contains("x-ms-content-sha256", StringComparer.OrdinalIgnoreCase))
        {
            return Fail("SignedHeaders missing 'x-ms-content-sha256'.");
        }
        var hasXmsDate = signedHeaderNames.Contains("x-ms-date", StringComparer.OrdinalIgnoreCase);
        var hasDate = signedHeaderNames.Contains("date", StringComparer.OrdinalIgnoreCase);
        if (!hasXmsDate && !hasDate)
        {
            return Fail("SignedHeaders missing date header (expected 'x-ms-date' or 'date').");
        }

        // Date validation. x-ms-date takes precedence over Date per spec.
        var dateString = Request.Headers.TryGetValue("x-ms-date", out var xms)
            ? xms.ToString()
            : Request.Headers.TryGetValue("Date", out var d) ? d.ToString() : null;

        if (string.IsNullOrEmpty(dateString))
        {
            return Fail("Missing date header.");
        }

        if (!DateTimeOffset.TryParse(
                dateString,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var requestDate))
        {
            return Fail("Invalid date header.");
        }

        if ((DateTimeOffset.UtcNow - requestDate).Duration() > Options.MaxClockSkew)
        {
            return Fail("Date header outside allowed clock skew.");
        }

        // Body hash. Buffer so the downstream pipeline can re-read.
        if (!Request.Headers.TryGetValue("x-ms-content-sha256", out var clientHashValues))
        {
            return Fail("Missing x-ms-content-sha256 header.");
        }

        var clientHash = clientHashValues.ToString();
        Request.EnableBuffering();
        var bodyBytes = await ReadBodyAsync(Request, Context.RequestAborted).ConfigureAwait(false);
        var actualHash = HmacSignature.ComputeContentHash(bodyBytes);
        if (!HmacSignature.SignaturesEqual(actualHash, clientHash))
        {
            return Fail("Body hash does not match x-ms-content-sha256 header.");
        }

        var credential = await _credentials.GetByIdAsync(parameters.CredentialId, Context.RequestAborted)
            .ConfigureAwait(false);
        if (credential == null || credential.IsRevoked || credential.IsExpired)
        {
            return Fail("Unknown, revoked, or expired credential.");
        }

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
                return Fail($"Signed header '{name}' missing from request.");
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

        string expectedSignature;
        try
        {
            expectedSignature = HmacSignature.ComputeSignature(stringToSign, credential.SecretBase64);
        }
        catch (FormatException)
        {
            return Fail("Stored credential secret is not valid base64.");
        }

        if (!HmacSignature.SignaturesEqual(expectedSignature, parameters.Signature))
        {
            return Fail("Invalid signature.");
        }

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, credential.Id),
            new Claim(ClaimTypes.Name, credential.Id),
            new Claim(ClaimTypes.Role, credential.Role.ToString()),
        };
        var identity = new ClaimsIdentity(claims, HmacSignature.SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, HmacSignature.SchemeName);
        return AuthenticateResult.Success(ticket);
    }

    private const string FailureReasonItemKey = "Dbosoft.ConfigService.HmacFailureReason";

    private AuthenticateResult Fail(string reason)
    {
        Context.Items[FailureReasonItemKey] = reason;
        return AuthenticateResult.Fail(reason);
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        var reason = Context.Items.TryGetValue(FailureReasonItemKey, out var r) ? r as string : null;
        Response.Headers.WWWAuthenticate = string.IsNullOrEmpty(reason)
            ? HmacSignature.SchemeName
            : $"{HmacSignature.SchemeName} error_description=\"{reason.Replace('"', '\'')}\"";
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
