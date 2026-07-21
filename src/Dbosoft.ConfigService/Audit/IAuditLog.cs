namespace Dbosoft.ConfigService.Audit;

/// <summary>
/// Append-only audit log for admin writes. Implementations must guarantee
/// that <see cref="AppendAsync"/> never overwrites existing entries.
/// </summary>
public interface IAuditLog
{
    Task AppendAsync(AuditEntry entry, CancellationToken ct = default);

    /// <summary>
    /// Reads all audit entries, optionally filtered by exact key match.
    /// Implementations stream the file lazily; the caller controls buffering.
    /// </summary>
    IAsyncEnumerable<AuditEntry> ReadAsync(string? keyFilter = null, CancellationToken ct = default);
}
