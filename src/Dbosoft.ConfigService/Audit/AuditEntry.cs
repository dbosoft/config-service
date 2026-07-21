namespace Dbosoft.ConfigService.Audit;

/// <summary>
/// One row in the append-only audit log. Captured for every successful
/// admin write so the operator can answer "who changed this and when".
/// </summary>
public class AuditEntry
{
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>"PUT" or "DELETE".</summary>
    public string Action { get; set; } = "";

    public string Key { get; set; } = "";

    public string? Label { get; set; }

    public string? OldValue { get; set; }

    public string? NewValue { get; set; }

    public string CredentialId { get; set; } = "";

    public string? ClientIp { get; set; }
}
