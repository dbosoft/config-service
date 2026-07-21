namespace Dbosoft.ConfigService.Storage;

/// <summary>
/// A single key-value entry as stored on disk and returned to clients.
/// Field shape mirrors the Azure App Configuration data-plane key-value
/// resource so the existing Microsoft .NET provider works against this
/// service unchanged.
/// </summary>
public class KeyValueEntry
{
    public string Key { get; set; } = "";

    /// <summary>
    /// Label for the entry. <c>null</c> represents the unlabeled entry,
    /// which is the default the Azure protocol uses when no label query
    /// parameter is supplied.
    /// </summary>
    public string? Label { get; set; }

    public string? Value { get; set; }

    public string? ContentType { get; set; }

    /// <summary>
    /// Opaque ETag assigned by the store. Changes on every successful write.
    /// </summary>
    public string ETag { get; set; } = "";

    public DateTimeOffset LastModified { get; set; }

    public Dictionary<string, string> Tags { get; set; } = new();
}
