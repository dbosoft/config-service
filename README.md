# Dbosoft.ConfigService

A small ASP.NET Core configuration service that speaks the publicly
documented [Azure App Configuration data-plane REST API], so the unmodified
[`Microsoft.Extensions.Configuration.AzureAppConfiguration`][nuget-provider]
NuGet package works against it as a drop-in `IConfigurationProvider`.

We build only the server side; clients use the standard Microsoft package.

## Why

Cross-module configuration values (mode flags, feature toggles, shared
endpoints) need to be the same in every container of a deployment. Mounting
a shared file works on a single host, but stops scaling once you have one
container per module. Distributing values via an event bus is a category
error: settings are key-value reads, not events. A versioned key-value
store fronted by a small HTTP API is exactly the right primitive — and
Azure already specified the protocol, so we don't have to.

The original use case is the SAPId project's `Scim:BackendMode` setting
that was duplicated across the API, Controller, and SCIM modules; see the
RFC at `docs/config_service_rfc.md` in that repository for the full
discussion.

## What it implements

- **Read endpoints** at `/kv` matching the Azure App Configuration data
  plane: list with key/label filters, ETag/`If-None-Match`,
  `application/vnd.microsoft.appconfig.kv+json` content types. Enough of
  the protocol that the real Microsoft `.AddAzureAppConfiguration(...)`
  call works without modification.
- **HMAC-SHA256 authentication** exactly as the public Azure spec defines:
  `Authorization: HMAC-SHA256 Credential=...&SignedHeaders=...&Signature=...`,
  ±15 minute clock skew tolerance, body content hash verification,
  constant-time signature comparison.
- **Admin API** under `/admin` for write operations (`PUT`, `DELETE`,
  `GET /admin/audit`). Not part of the Azure protocol — used by tooling
  to manage values. Requires an `Admin` role credential.
- **JSON file storage** for values, credentials, and the audit log. No
  database. The whole working set lives in three files in a configurable
  data directory.
- **Append-only audit log** capturing every admin write with `OldValue`,
  `NewValue`, credential id, timestamp, and client IP.

## What it does not implement (yet)

- Feature flags or Key Vault references (the special Azure content types).
  The Microsoft provider only invokes those code paths if you opt in via
  `UseFeatureFlags()`; leave it off and everything is a plain key-value.
- Polling refresh / sentinel keys. The library is currently a "fetch at
  boot" service. Adding sentinel polling is a few lines on the consumer
  side once a value benefits from it.
- AAD-based authentication. HMAC with shared secrets only.
- Snapshots, full revision history, point-in-time reads. The audit log
  has the change record; the value table holds only the current value.

## Project layout

```
src/
├── Dbosoft.ConfigService/         # The library — embeddable in any ASP.NET Core
│                                  # or Hosuto host via two extension methods
└── Dbosoft.ConfigService.Host/    # Standalone executable + Dockerfile
test/
├── Dbosoft.ConfigService.Tests/             # Unit tests
└── Dbosoft.ConfigService.IntegrationTests/  # WebApplicationFactory + the
                                             # Microsoft-provider roundtrip
```

## Quickstart: standalone host

```bash
cd src/Dbosoft.ConfigService.Host
ConfigService__DataDirectory=/var/lib/configservice \
ConfigService__BootstrapAdminId=primary-admin \
ConfigService__BootstrapAdminSecret=BASE64_ENCODED_HMAC_KEY \
dotnet run
```

The host refuses to start if the credential store is empty *and* no
bootstrap admin id+secret are supplied — running open is never acceptable.
On first start the bootstrap credential is written to
`<DataDirectory>/credentials.json` and the env vars are ignored on
subsequent starts.

### Docker

```bash
docker build -t dbosoft/configservice -f src/Dbosoft.ConfigService.Host/Dockerfile .

docker run --rm -p 8080:8080 \
  -v configservice-data:/data \
  -e ConfigService__BootstrapAdminId=primary-admin \
  -e ConfigService__BootstrapAdminSecret=$(openssl rand -base64 32) \
  dbosoft/configservice
```

The Dockerfile mounts `/data` as a volume so values, credentials, and
audit log survive container upgrades.

## Quickstart: embed the library in your own host

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddConfigService(builder.Configuration);

var app = builder.Build();

// Bootstrap a single admin credential on first run if env vars are set.
var options = app.Services.GetRequiredService<IOptions<ConfigServiceOptions>>().Value;
var credentials = app.Services.GetRequiredService<ICredentialStore>();
if (!string.IsNullOrEmpty(options.BootstrapAdminId)
    && !string.IsNullOrEmpty(options.BootstrapAdminSecret))
{
    await credentials.EnsureBootstrapAdminAsync(
        options.BootstrapAdminId,
        options.BootstrapAdminSecret);
}

app.UseAuthentication();
app.UseAuthorization();
app.MapConfigService();

await app.RunAsync();
```

The same two calls (`AddConfigService` + `MapConfigService`) work inside
a Hosuto module class — convention-based methods on the module pick up
`IServiceCollection` and `IEndpointRouteBuilder` exactly the same way.

## Quickstart: read values with the Microsoft provider

In any consuming application, install the standard NuGet package:

```bash
dotnet add package Microsoft.Extensions.Configuration.AzureAppConfiguration
```

Then point it at your Config Service via a normal connection string:

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddAzureAppConfiguration(options =>
{
    options.Connect(builder.Configuration["ConfigService:ConnectionString"]);
});
```

Where `ConfigService:ConnectionString` is of the form:

```
Endpoint=https://config.your-deployment.local;Id=primary-admin;Secret=BASE64_ENCODED_HMAC_KEY
```

The application then reads values via the standard `IConfiguration` API:

```csharp
var mode = builder.Configuration["Scim:BackendMode"];
```

No SAPId-specific or Dbosoft-specific code on the consumer side.

## Storage layout

The data directory holds three files:

| File | Purpose |
|---|---|
| `values.json` | All key-value entries plus the monotonic ETag counter |
| `credentials.json` | HMAC credentials (id, base64 secret, role, expiry) |
| `audit.jsonl` | Append-only JSON Lines audit log of every admin write |

Writes go through an in-memory cache and are persisted via temp-file +
atomic rename. Single-process owner — do not point two Config Service
instances at the same directory.

The credentials file contains plaintext HMAC secrets (the server has to
recompute signatures, so they cannot be hashed at rest). Protect the data
directory with filesystem permissions: read/write for the service account
only, no group or world access.

## Authentication scheme

Identical to the public Azure App Configuration HMAC-SHA256 spec:

```
Authorization: HMAC-SHA256 Credential={id}&SignedHeaders={names}&Signature={base64}
x-ms-date: Fri, 11 May 2018 18:48:36 GMT
x-ms-content-sha256: {base64-encoded SHA-256 of request body}
```

Where `SignedHeaders` MUST contain `host`, `x-ms-content-sha256`, and a
date header (either `x-ms-date` or `Date` — the Microsoft Azure SDK
signs `Date`).

The string-to-sign is:

```
{HTTP_METHOD}\n{path_and_query}\n{semicolon-joined values of signed headers}
```

The signature is `base64(HMAC-SHA256(stringToSign, base64Decode(secret)))`.

## Tests

```bash
dotnet test
```

Two test projects:

- **`Dbosoft.ConfigService.Tests`** — pure unit tests for the JSON stores,
  the HMAC signature helpers, and the audit log.
- **`Dbosoft.ConfigService.IntegrationTests`** — boots the full host via
  `WebApplicationFactory<Program>`, exercises the REST endpoints with a
  test-side HMAC client, and runs the Microsoft-provider roundtrip test
  that drives the real
  `Microsoft.Extensions.Configuration.AzureAppConfiguration` package
  against the in-process server. **The roundtrip test is the only thing
  that proves protocol compatibility — never merge a change without it
  passing.**

## License

Not yet declared. Contact dbosoft.

[Azure App Configuration data-plane REST API]: https://github.com/MicrosoftDocs/azure-docs/blob/main/articles/azure-app-configuration/rest-api.md
[nuget-provider]: https://github.com/Azure/AppConfiguration-DotnetProvider
