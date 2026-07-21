using System.Text.Json.Serialization;
using Dbosoft.ConfigService.Storage;

namespace Dbosoft.ConfigService.Endpoints;

/// <summary>
/// Wire shape of a single key-value as returned by the Azure App
/// Configuration data-plane API. Property names use snake_case to match
/// what the Microsoft .NET configuration provider expects.
/// </summary>
internal sealed class KeyValueDto
{
    [JsonPropertyName("etag")]
    public string? ETag { get; set; }

    [JsonPropertyName("key")]
    public string Key { get; set; } = "";

    [JsonPropertyName("label")]
    public string? Label { get; set; }

    [JsonPropertyName("content_type")]
    public string? ContentType { get; set; }

    [JsonPropertyName("value")]
    public string? Value { get; set; }

    [JsonPropertyName("tags")]
    public Dictionary<string, string> Tags { get; set; } = new();

    // Part of the Azure key-value wire shape. This service has no lock
    // feature, so an entry is never locked; the field is always emitted as
    // false to keep the response schema the Microsoft provider expects.
    [JsonPropertyName("locked")]
    public bool Locked { get; set; }

    [JsonPropertyName("last_modified")]
    public DateTimeOffset LastModified { get; set; }

    public static KeyValueDto FromEntry(KeyValueEntry e) => new()
    {
        ETag = e.ETag,
        Key = e.Key,
        Label = e.Label,
        ContentType = e.ContentType,
        Value = e.Value,
        Tags = new Dictionary<string, string>(e.Tags),
        Locked = false,
        LastModified = e.LastModified,
    };
}

internal sealed class KeyValueListDto
{
    [JsonPropertyName("items")]
    public List<KeyValueDto> Items { get; set; } = new();
}

/// <summary>
/// Body shape for the admin PUT endpoint. Plain camelCase since admin
/// callers are our own tooling, not Azure clients. The key and label live
/// in the body rather than the URL path so values containing characters
/// like ':' do not have to round-trip through URL encoding (which would
/// otherwise force every caller to agree on percent-encoding rules with
/// the HMAC signature pipeline).
/// </summary>
public sealed class AdminPutKeyValueRequest
{
    public string Key { get; set; } = "";
    public string? Label { get; set; }
    public string? Value { get; set; }
    public string? ContentType { get; set; }
    public Dictionary<string, string>? Tags { get; set; }
}

public sealed class AdminDeleteKeyValueRequest
{
    public string Key { get; set; } = "";
    public string? Label { get; set; }
}
