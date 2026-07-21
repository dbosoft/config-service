namespace Dbosoft.ConfigService.Authentication;

/// <summary>
/// HMAC credential record. Stored in plaintext on disk because the HMAC
/// validation requires the secret to recompute signatures; protect the
/// credentials file with filesystem permissions.
/// </summary>
public class Credential
{
    public string Id { get; set; } = "";

    /// <summary>Base64-encoded HMAC key.</summary>
    public string SecretBase64 { get; set; } = "";

    public CredentialRole Role { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? ExpiresAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    public string? Description { get; set; }

    public bool IsRevoked => RevokedAt.HasValue && RevokedAt.Value <= DateTimeOffset.UtcNow;

    public bool IsExpired => ExpiresAt.HasValue && ExpiresAt.Value <= DateTimeOffset.UtcNow;
}

public enum CredentialRole
{
    Reader,
    Admin,
}
