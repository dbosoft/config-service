using Dbosoft.ConfigService;
using Dbosoft.ConfigService.Authentication;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(options =>
{
    // Config values are small (single key-value strings). Cap the request body
    // well below Kestrel's ~28.6 MB default so an oversized body is rejected at
    // the transport (413) before the auth handler buffers it into memory.
    options.Limits.MaxRequestBodySize = 1 * 1024 * 1024; // 1 MiB
});

builder.Services.AddConfigService(builder.Configuration);
builder.Services.AddHealthChecks();

var app = builder.Build();

// Bootstrap admin credential from configuration / environment if the
// credential store is empty. The host refuses to start when no credentials
// exist and no bootstrap values are supplied — running open is never
// acceptable.
var options = app.Services.GetRequiredService<IOptions<ConfigServiceOptions>>().Value;
var credentials = app.Services.GetRequiredService<ICredentialStore>();

var bootstrapRequested = !string.IsNullOrEmpty(options.BootstrapAdminId)
    && !string.IsNullOrEmpty(options.BootstrapAdminSecret);
var storeWasEmpty = (await credentials.ListAsync()).Count == 0;

// Fail fast on a non-base64 bootstrap secret. Otherwise the credential is
// created and startup succeeds, but every request later fails to authenticate
// (the HMAC key can't be base64-decoded) — a silent, total lockout.
if (bootstrapRequested && storeWasEmpty && !IsBase64(options.BootstrapAdminSecret!))
{
    app.Logger.LogCritical(
        "ConfigService:BootstrapAdminSecret is not valid base64. The HMAC secret must be a " +
        "base64-encoded key (e.g. `openssl rand -base64 32`). Refusing to start.");
    Environment.ExitCode = 78; // EX_CONFIG
    return;
}

if (bootstrapRequested)
{
    await credentials.EnsureBootstrapAdminAsync(
        options.BootstrapAdminId!,
        options.BootstrapAdminSecret!);
}

var readerRequested = !string.IsNullOrEmpty(options.ReaderId) && !string.IsNullOrEmpty(options.ReaderSecret);
if (readerRequested && !IsBase64(options.ReaderSecret!))
{
    app.Logger.LogCritical("ConfigService:ReaderSecret is not valid base64. Refusing to start.");
    Environment.ExitCode = 78; // EX_CONFIG
    return;
}

// Declared after the bootstrap admin: a reader alone in an empty store would stop the admin from
// ever being created.
if (readerRequested)
    await credentials.SetDeclaredReaderAsync(options.ReaderId!, options.ReaderSecret!);

var existing = await credentials.ListAsync();
if (existing.Count == 0)
{
    app.Logger.LogCritical(
        "Config Service has no credentials and no bootstrap admin was supplied. " +
        "Set ConfigService:BootstrapAdminId and ConfigService:BootstrapAdminSecret " +
        "(env: ConfigService__BootstrapAdminId / ConfigService__BootstrapAdminSecret) " +
        "on first start. Refusing to run open.");
    Environment.ExitCode = 78; // EX_CONFIG
    return;
}

if (bootstrapRequested)
{
    if (storeWasEmpty)
    {
        app.Logger.LogInformation(
            "Bootstrap admin credential '{Id}' was created. You can now remove " +
            "ConfigService:BootstrapAdminId / ConfigService:BootstrapAdminSecret " +
            "(env: ConfigService__BootstrapAdminId / ConfigService__BootstrapAdminSecret) " +
            "from the environment; they are ignored on subsequent starts.",
            options.BootstrapAdminId);
    }
    else
    {
        app.Logger.LogWarning(
            "Bootstrap admin values are set but the credential store already contains " +
            "credentials. The bootstrap values are ignored and should be removed from " +
            "the environment so the secret is not kept around unnecessarily.");
    }
}

app.UseAuthentication();
app.UseAuthorization();
app.MapConfigService();
app.MapHealthChecks("/health");

await app.RunAsync();

static bool IsBase64(string value)
{
    // Upper bound on decoded length; TryFromBase64String validates the format.
    var buffer = new byte[value.Length];
    return Convert.TryFromBase64String(value, buffer, out _);
}

/// <summary>
/// Marker so test projects can reference <c>Program</c> for
/// <c>WebApplicationFactory&lt;Program&gt;</c>.
/// </summary>
public partial class Program;
