namespace Dbosoft.ConfigService.Storage;

/// <summary>
/// Persistence abstraction for key-value entries. The shipped implementation
/// is a JSON file on disk; alternative implementations (in-memory, database)
/// can be substituted without affecting the REST endpoints or the HMAC
/// authentication layer.
/// </summary>
public interface IKeyValueStore
{
    /// <summary>
    /// Returns the entry matching the given key and label, or <c>null</c> if
    /// no such entry exists. <paramref name="label"/> is matched literally:
    /// <c>null</c> means the unlabeled entry, the empty string is treated
    /// the same as <c>null</c> per the Azure protocol convention.
    /// </summary>
    Task<KeyValueEntry?> GetAsync(string key, string? label, CancellationToken ct = default);

    /// <summary>
    /// Returns entries matching the supplied filters.
    /// <paramref name="keyFilter"/> and <paramref name="labelFilter"/> follow
    /// the simplified Azure protocol semantics this service supports:
    /// <list type="bullet">
    /// <item><c>null</c> or <c>"*"</c> matches any value</item>
    /// <item><c>""</c> on label matches only entries with no label</item>
    /// <item>A string ending in <c>*</c> is a prefix match</item>
    /// <item>Anything else is an exact match</item>
    /// </list>
    /// Results are ordered by key, then label, for stable pagination.
    /// </summary>
    Task<IReadOnlyList<KeyValueEntry>> ListAsync(
        string? keyFilter,
        string? labelFilter,
        CancellationToken ct = default);

    /// <summary>
    /// Creates or updates an entry. The store assigns a fresh ETag and
    /// <c>LastModified</c> on every successful write.
    /// </summary>
    Task<KeyValueEntry> SetAsync(
        string key,
        string? label,
        string? value,
        string? contentType,
        IDictionary<string, string>? tags,
        CancellationToken ct = default);

    /// <summary>
    /// Deletes the matching entry and returns the deleted snapshot, or
    /// <c>null</c> if nothing matched.
    /// </summary>
    Task<KeyValueEntry?> DeleteAsync(string key, string? label, CancellationToken ct = default);
}
