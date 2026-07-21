using Dbosoft.ConfigService.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Dbosoft.ConfigService.Endpoints;

/// <summary>
/// Read-side endpoints under <c>/kv</c>. Implements the subset of the
/// Azure App Configuration data-plane API that the Microsoft .NET
/// configuration provider exercises during a normal load.
/// </summary>
public static class KeyValueEndpoints
{
    public const string KeyValueContentType = "application/vnd.microsoft.appconfig.kv+json";
    public const string KeyValueSetContentType = "application/vnd.microsoft.appconfig.kvset+json";

    public static IEndpointRouteBuilder MapKeyValueEndpoints(this IEndpointRouteBuilder routes)
    {
        // The list endpoint sits at /kv (no trailing slash) — matches what
        // the Microsoft Azure App Configuration provider issues.
        routes.MapGet("/kv", ListKeyValues).RequireAuthorization();

        routes.MapGet("/kv/{*key}", GetKeyValue).RequireAuthorization();
        routes.MapMethods("/kv/{*key}", new[] { HttpMethods.Head }, HeadKeyValue).RequireAuthorization();

        routes.MapGet("/labels", ListLabels).RequireAuthorization();

        return routes;
    }

    private static async Task<IResult> ListKeyValues(
        HttpContext ctx,
        IKeyValueStore store,
        string? key,
        string? label)
    {
        var entries = await store.ListAsync(key, label, ctx.RequestAborted).ConfigureAwait(false);
        var dto = new KeyValueListDto
        {
            Items = entries.Select(KeyValueDto.FromEntry).ToList(),
        };
        // Use Results.Json with an explicit content type: Results.Ok(dto)
        // would serialize as application/json and overwrite the vendor media
        // type the Azure provider expects.
        return Results.Json(dto, contentType: KeyValueSetContentType);
    }

    private static async Task<IResult> GetKeyValue(
        HttpContext ctx,
        IKeyValueStore store,
        string key,
        string? label)
    {
        var entry = await store.GetAsync(key, label, ctx.RequestAborted).ConfigureAwait(false);
        if (entry == null)
        {
            return Results.NotFound();
        }

        if (TryMatchIfNoneMatch(ctx.Request, entry.ETag))
        {
            ctx.Response.StatusCode = StatusCodes.Status304NotModified;
            ctx.Response.Headers.ETag = $"\"{entry.ETag}\"";
            return Results.Empty;
        }

        ctx.Response.Headers.ETag = $"\"{entry.ETag}\"";
        ctx.Response.Headers.LastModified = entry.LastModified.ToString("R");
        return Results.Json(KeyValueDto.FromEntry(entry), contentType: KeyValueContentType);
    }

    private static async Task<IResult> HeadKeyValue(
        HttpContext ctx,
        IKeyValueStore store,
        string key,
        string? label)
    {
        var entry = await store.GetAsync(key, label, ctx.RequestAborted).ConfigureAwait(false);
        if (entry == null)
        {
            return Results.NotFound();
        }

        ctx.Response.Headers.ETag = $"\"{entry.ETag}\"";
        ctx.Response.Headers.LastModified = entry.LastModified.ToString("R");
        ctx.Response.ContentType = KeyValueContentType;
        return Results.Ok();
    }

    private static async Task<IResult> ListLabels(
        HttpContext ctx,
        IKeyValueStore store,
        string? name)
    {
        var entries = await store.ListAsync(null, null, ctx.RequestAborted).ConfigureAwait(false);
        var labels = entries
            .Select(e => e.Label)
            .Where(l => l != null)
            .Distinct()
            // Apply the optional name filter with the same semantics as the
            // key/label filters (null or "*" => all; prefix and comma-list
            // supported). Without this the parameter was silently ignored.
            .Where(l => KeyValueFilter.Matches(l!, name))
            .OrderBy(l => l, StringComparer.Ordinal)
            .Select(l => new { name = l })
            .ToList();
        return Results.Ok(new { items = labels });
    }

    private static bool TryMatchIfNoneMatch(HttpRequest request, string etag)
    {
        if (!request.Headers.TryGetValue("If-None-Match", out var values))
        {
            return false;
        }

        foreach (var raw in values)
        {
            if (raw == null)
            {
                continue;
            }

            var trimmed = raw.Trim();
            // Strip an optional weak-validator prefix before comparing.
            if (trimmed.StartsWith("W/", StringComparison.Ordinal))
            {
                trimmed = trimmed[2..];
            }

            trimmed = trimmed.Trim('"');
            if (trimmed == "*" || string.Equals(trimmed, etag, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
