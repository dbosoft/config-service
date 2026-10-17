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

    /// <summary>
    /// Keeps a Reader credential with this id and secret, adding it or replacing its secret. The
    /// platform declares it on every start, so a secret rotated where the platform keeps it takes
    /// effect at the next start. An Admin credential of the same id is refused, never downgraded.
    /// </summary>
    Task SetDeclaredReaderAsync(string id, string secretBase64, CancellationToken ct = default);
}
