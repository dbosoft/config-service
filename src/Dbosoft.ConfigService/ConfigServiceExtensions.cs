using Dbosoft.ConfigService.Audit;
using Dbosoft.ConfigService.Authentication;
using Dbosoft.ConfigService.Endpoints;
using Dbosoft.ConfigService.Storage;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Dbosoft.ConfigService;

/// <summary>
/// DI and routing extensions. The library plugs into either a plain ASP.NET
/// Core host or a Hosuto module via the same two calls:
/// <c>services.AddConfigService(...)</c> and
/// <c>app.MapConfigService()</c> (the latter requires the standard
/// <c>UseAuthentication</c>/<c>UseAuthorization</c> middleware).
/// </summary>
public static class ConfigServiceExtensions
{
    /// <summary>
    /// Registers the Config Service: storage, credential store, audit log,
    /// HMAC authentication scheme, and the admin authorization policy.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">
    /// Optional configuration root used to bind <see cref="ConfigServiceOptions"/>
    /// from the <c>ConfigService</c> section. May be <c>null</c> if the
    /// caller binds options another way.
    /// </param>
    /// <param name="configure">
    /// Optional programmatic override applied after configuration binding.
    /// </param>
    public static IServiceCollection AddConfigService(
        this IServiceCollection services,
        IConfiguration? configuration = null,
        Action<ConfigServiceOptions>? configure = null)
    {
        if (configuration != null)
        {
            services.Configure<ConfigServiceOptions>(
                configuration.GetSection(ConfigServiceOptions.SectionName));
        }

        if (configure != null)
        {
            services.Configure(configure);
        }

        services.AddSingleton<IKeyValueStore, JsonFileKeyValueStore>();
        services.AddSingleton<ICredentialStore, JsonFileCredentialStore>();
        services.AddSingleton<IAuditLog, JsonLinesAuditLog>();

        services
            .AddAuthentication(HmacSignature.SchemeName)
            .AddScheme<HmacSchemeOptions, HmacSha256AuthenticationHandler>(
                HmacSignature.SchemeName,
                _ => { });

        services.AddOptions<HmacSchemeOptions>(HmacSignature.SchemeName)
            .Configure<IOptions<ConfigServiceOptions>>((scheme, configOptions) =>
            {
                scheme.MaxClockSkew = configOptions.Value.MaxClockSkew;
            });

        services.AddAuthorization(options =>
        {
            options.AddPolicy(AdminEndpoints.AdminPolicyName, policy =>
            {
                policy.AddAuthenticationSchemes(HmacSignature.SchemeName);
                policy.RequireAuthenticatedUser();
                policy.RequireRole(nameof(CredentialRole.Admin));
            });
        });

        return services;
    }

    /// <summary>
    /// Maps the Config Service routes (read endpoints under <c>/kv</c> and
    /// admin endpoints under <c>/admin</c>). Caller must have already added
    /// authentication and authorization middleware (<c>UseAuthentication()</c>
    /// and <c>UseAuthorization()</c>) to the pipeline.
    /// </summary>
    public static IEndpointRouteBuilder MapConfigService(this IEndpointRouteBuilder routes)
    {
        routes.MapKeyValueEndpoints();
        routes.MapAdminEndpoints();
        return routes;
    }
}
