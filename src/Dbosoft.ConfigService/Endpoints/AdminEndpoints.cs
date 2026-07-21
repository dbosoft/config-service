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
