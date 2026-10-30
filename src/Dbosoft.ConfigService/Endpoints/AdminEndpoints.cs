using System.Security.Claims;
using Dbosoft.ConfigService.Audit;
using Dbosoft.ConfigService.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Dbosoft.ConfigService.Endpoints;

/// <summary>
/// Admin-only endpoints under <c>/admin</c>. Not part of the Azure App
/// Configuration protocol — used by SAPId admin tooling and operators to
/// manage values via the same HMAC credential scheme.
/// </summary>
public static class AdminEndpoints
{
    public const string AdminPolicyName = "ConfigServiceAdmin";

    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/admin").RequireAuthorization(AdminPolicyName);

        group.MapPut("/kv", PutKeyValue);
        group.MapPut("/kv-set", ReplaceSet);
        group.MapMethods("/kv", new[] { HttpMethods.Delete }, DeleteKeyValue);
        group.MapGet("/audit", ReadAudit);

        return routes;
    }

    private static async Task<IResult> PutKeyValue(
        HttpContext ctx,
        IKeyValueStore store,
        IAuditLog audit,
        [FromBody] AdminPutKeyValueRequest? body)
    {
        if (body is null)
        {
            return Results.BadRequest(new { error = "request body is required" });
        }

        if (string.IsNullOrEmpty(body.Key))
        {
            return Results.BadRequest(new { error = "key is required" });
        }

        var existing = await store.GetAsync(body.Key, body.Label, ctx.RequestAborted).ConfigureAwait(false);

        var entry = await store.SetAsync(
                body.Key,
                body.Label,
                body.Value,
                body.ContentType,
                body.Tags,
                ctx.RequestAborted)
            .ConfigureAwait(false);

        await audit.AppendAsync(new AuditEntry
        {
            Timestamp = DateTimeOffset.UtcNow,
            Action = "PUT",
            Key = body.Key,
            Label = body.Label,
            OldValue = existing?.Value,
            NewValue = body.Value,
            CredentialId = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "",
            ClientIp = ctx.Connection.RemoteIpAddress?.ToString(),
        }, ctx.RequestAborted).ConfigureAwait(false);

        return Results.Ok(KeyValueDto.FromEntry(entry));
    }

    /// <summary>
    /// Replaces all entries of one label at once. A configuration is a whole: written key by key, a
    /// failure halfway leaves a mix of two versions that no one ever activated, and a process
    /// starting meanwhile reads it.
    /// </summary>
    private static async Task<IResult> ReplaceSet(
        HttpContext ctx,
        IKeyValueStore store,
        IAuditLog audit,
        [FromBody] AdminReplaceSetRequest? body)
    {
        if (body is null)
        {
            return Results.BadRequest(new { error = "request body is required" });
        }

        if (body.Items.Any(i => string.IsNullOrEmpty(i.Key)))
        {
            return Results.BadRequest(new { error = "every item needs a key" });
        }

        if (body.Items.Any(i => !string.IsNullOrEmpty(i.Label) && i.Label != body.Label))
        {
            return Results.BadRequest(new { error = "items carry the label of the set, not one of their own" });
        }

        if (body.Items.GroupBy(i => i.Key, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1) is { } duplicate)
        {
            return Results.BadRequest(new { error = $"the key '{duplicate.Key}' is given more than once" });
        }

        var (before, after) = await store.ReplaceAsync(
                body.Label,
                body.Items.Select(i => new KeyValueEntry
                {
                    Key = i.Key,
                    Value = i.Value,
                    ContentType = i.ContentType,
                    Tags = i.Tags ?? new Dictionary<string, string>(),
                }).ToList(),
                ctx.RequestAborted)
            .ConfigureAwait(false);

        var credential = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
        var ip = ctx.Connection.RemoteIpAddress?.ToString();
        var now = DateTimeOffset.UtcNow;
        var old = before.ToDictionary(e => e.Key, StringComparer.Ordinal);
        foreach (var entry in after.Where(e => !old.TryGetValue(e.Key, out var o) || o.ETag != e.ETag))
        {
            await audit.AppendAsync(new AuditEntry
            {
                Timestamp = now, Action = "PUT", Key = entry.Key, Label = body.Label,
                OldValue = old.GetValueOrDefault(entry.Key)?.Value, NewValue = entry.Value,
                CredentialId = credential, ClientIp = ip,
            }, ctx.RequestAborted).ConfigureAwait(false);
        }

        var kept = after.Select(e => e.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var removed in before.Where(e => !kept.Contains(e.Key)))
        {
            await audit.AppendAsync(new AuditEntry
            {
                Timestamp = now, Action = "DELETE", Key = removed.Key, Label = body.Label,
                OldValue = removed.Value, NewValue = null,
                CredentialId = credential, ClientIp = ip,
            }, ctx.RequestAborted).ConfigureAwait(false);
        }

        return Results.Ok(new KeyValueListDto { Items = after.Select(KeyValueDto.FromEntry).ToList() });
    }

    private static async Task<IResult> DeleteKeyValue(
        HttpContext ctx,
        IKeyValueStore store,
        IAuditLog audit,
        [FromBody] AdminDeleteKeyValueRequest? body)
    {
        if (body is null)
        {
            return Results.BadRequest(new { error = "request body is required" });
        }

        if (string.IsNullOrEmpty(body.Key))
        {
            return Results.BadRequest(new { error = "key is required" });
        }

        var deleted = await store.DeleteAsync(body.Key, body.Label, ctx.RequestAborted).ConfigureAwait(false);
        if (deleted == null)
        {
            return Results.NotFound();
        }

        await audit.AppendAsync(new AuditEntry
        {
            Timestamp = DateTimeOffset.UtcNow,
            Action = "DELETE",
            Key = body.Key,
            Label = body.Label,
            OldValue = deleted.Value,
            NewValue = null,
            CredentialId = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "",
            ClientIp = ctx.Connection.RemoteIpAddress?.ToString(),
        }, ctx.RequestAborted).ConfigureAwait(false);

        return Results.Ok(KeyValueDto.FromEntry(deleted));
    }

    private static async Task<IResult> ReadAudit(
        HttpContext ctx,
        IAuditLog audit,
        string? key)
    {
        var list = new List<AuditEntry>();
        await foreach (var entry in audit.ReadAsync(key, ctx.RequestAborted).ConfigureAwait(false))
        {
            list.Add(entry);
        }
        return Results.Ok(list);
    }
}
