namespace Dbosoft.ConfigService;

/// <summary>
/// Options controlling the Config Service. Bind from configuration section
/// <see cref="SectionName"/> or set programmatically when calling
/// <see cref="ConfigServiceExtensions.AddConfigService"/>.
/// </summary>
public class ConfigServiceOptions
{
    public const string SectionName = "ConfigService";

    /// <summary>
    /// Directory holding <c>values.json</c>, <c>credentials.json</c>, and
    /// <c>audit.jsonl</c>. The service creates the directory on first run.
    /// In containers this should point at a mounted volume so the data
    /// survives image upgrades.
    /// </summary>
    public string DataDirectory { get; set; } = "./data";

    /// <summary>
    /// Bootstrap admin credential id. If set together with
    /// <see cref="BootstrapAdminSecret"/>, an Admin credential with this id
    /// is created on first start when the credential store is empty. On
    /// subsequent starts the values are ignored. If neither is set and the
    /// store is empty the host refuses to start.
    /// </summary>
    public string? BootstrapAdminId { get; set; }

    /// <summary>
    /// Base64-encoded HMAC secret for the bootstrap admin credential.
    /// </summary>
    public string? BootstrapAdminSecret { get; set; }

    /// <summary>
    /// Id of a Reader credential the platform declares. Set together with
    /// <see cref="ReaderSecret"/>, it is kept with that secret on every start,
    /// so the applications reading the configuration need no admin credential.
    /// </summary>
    public string? ReaderId { get; set; }

    /// <summary>Base64-encoded HMAC secret of the <see cref="ReaderId"/> credential.</summary>
    public string? ReaderSecret { get; set; }

    /// <summary>
    /// Maximum allowed clock skew between client and server for HMAC date
    /// header validation. Default 15 minutes, matching the Azure App
    /// Configuration data-plane API.
    /// </summary>
    public TimeSpan MaxClockSkew { get; set; } = TimeSpan.FromMinutes(15);
}
