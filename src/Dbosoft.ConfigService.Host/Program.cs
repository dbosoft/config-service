using Dbosoft.ConfigService;
using Dbosoft.ConfigService.Authentication;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddConfigService(builder.Configuration);
builder.Services.AddHealthChecks();

var app = builder.Build();

// Bootstrap admin credential from configuration / environment if the
// credential store is empty. The host refuses to start when no credentials
// exist and no bootstrap values are supplied — running open is never
// acceptable.
var options = app.Services.GetRequiredService<IOptions<ConfigServiceOptions>>().Value;
var credentials = app.Services.GetRequiredService<ICredentialStore>();

if (!string.IsNullOrEmpty(options.BootstrapAdminId)
    && !string.IsNullOrEmpty(options.BootstrapAdminSecret))
{
    await credentials.EnsureBootstrapAdminAsync(
        options.BootstrapAdminId,
        options.BootstrapAdminSecret);
}

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

app.UseAuthentication();
app.UseAuthorization();
app.MapConfigService();
app.MapHealthChecks("/health");

await app.RunAsync();

/// <summary>
/// Marker so test projects can reference <c>Program</c> for
/// <c>WebApplicationFactory&lt;Program&gt;</c>.
/// </summary>
public partial class Program;
