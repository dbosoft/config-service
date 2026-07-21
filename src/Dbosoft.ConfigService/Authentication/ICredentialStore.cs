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
    /// environment variables without leaving secrets on disk in plaintext.
    /// </summary>
    Task EnsureBootstrapAdminAsync(string id, string secretBase64, CancellationToken ct = default);
}
