using System.Security.Cryptography;
using System.Text;

namespace Dbosoft.ConfigService.Authentication;

/// <summary>
/// Pure functions implementing the Azure App Configuration HMAC-SHA256
/// signature scheme. Kept side-effect-free so they are trivial to unit test.
/// </summary>
public static class HmacSignature
{
    public const string SchemeName = "HMAC-SHA256";

    /// <summary>
    /// Parses the parameters portion of an Authorization header value (i.e.
    /// the part after the scheme name and space). Returns <c>null</c> if any
    /// of the three required parameters are missing.
    /// </summary>
    public static AuthorizationParameters? ParseAuthorizationParameters(string parameterString)
    {
        string? credential = null;
        string? signedHeaders = null;
        string? signature = null;

        foreach (var part in parameterString.Split('&'))
        {
            var eq = part.IndexOf('=');
            if (eq < 0)
            {
                continue;
            }

            var name = part[..eq].Trim();
            var value = part[(eq + 1)..].Trim();

            switch (name)
            {
                case "Credential":
                    credential = value;
                    break;
                case "SignedHeaders":
                    signedHeaders = value;
                    break;
                case "Signature":
                    signature = value;
                    break;
            }
        }

        if (credential == null || signedHeaders == null || signature == null)
        {
            return null;
        }

        return new AuthorizationParameters(credential, signedHeaders, signature);
    }

    /// <summary>
    /// Constructs the canonical string-to-sign:
    /// <c>{METHOD}\n{path_and_query}\n{header_values_joined_by_semicolons}</c>.
    /// </summary>
    public static string BuildStringToSign(
        string httpMethod,
        string pathAndQuery,
        IEnumerable<string> signedHeaderValues)
    {
        return string.Concat(
            httpMethod.ToUpperInvariant(),
            "\n",
            pathAndQuery,
            "\n",
            string.Join(";", signedHeaderValues));
    }

    /// <summary>
    /// Computes the base64-encoded HMAC-SHA256 signature of
    /// <paramref name="stringToSign"/> using
    /// <paramref name="secretBase64"/> as the key (the key is base64-decoded
    /// before use, matching the Azure spec).
    /// </summary>
    public static string ComputeSignature(string stringToSign, string secretBase64)
    {
        var key = Convert.FromBase64String(secretBase64);
        using var hmac = new HMACSHA256(key);
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(stringToSign));
        return Convert.ToBase64String(hash);
    }

    /// <summary>
    /// Computes the base64-encoded SHA-256 hash of the supplied request body
    /// bytes. The hash of the empty body is the well-known constant
    /// <c>47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU=</c>.
    /// </summary>
    public static string ComputeContentHash(ReadOnlySpan<byte> body)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(body, hash);
        return Convert.ToBase64String(hash);
    }

    /// <summary>
    /// Constant-time comparison of two base64-encoded signatures.
    /// </summary>
    public static bool SignaturesEqual(string a, string b)
    {
        var aBytes = Encoding.ASCII.GetBytes(a);
        var bBytes = Encoding.ASCII.GetBytes(b);
        return CryptographicOperations.FixedTimeEquals(aBytes, bBytes);
    }

    public sealed record AuthorizationParameters(
        string CredentialId,
        string SignedHeaders,
        string Signature);
}
