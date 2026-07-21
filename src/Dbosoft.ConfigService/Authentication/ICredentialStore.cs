namespace Dbosoft.ConfigService.Authentication;

/// <summary>
/// Persistence abstraction for HMAC credentials. Read on every authenticated
/// request, written rarely (rotation, revocation, bootstrap).
/// </summary>
public interface ICredentialStore
{
    Task<Credential?> GetByIdAsync(string id, CancellationToken ct = default);

    Task<IReadOnlyList<Credential>> ListAsync(CancellationToken ct = default);

    /// <summary>
    /// If the store is currently empty, atomically create the supplied admin
    /// credential. If the store already contains any credentials this is a
    /// no-op. Used by host startup to bootstrap a fresh deployment from
    /// environment variables.
    /// </summary>
    /// <remarks>
    /// The HMAC scheme requires the shared secret to recompute signatures, so
    /// the secret is persisted in plaintext in <c>credentials.json</c> (see
    /// <see cref="Credential"/>). Protect that file with filesystem
    /// permissions / a restricted volume; the bootstrap value is not
    /// obfuscated or encrypted at rest.
    /// </remarks>
    Task EnsureBootstrapAdminAsync(string id, string secretBase64, CancellationToken ct = default);
}
