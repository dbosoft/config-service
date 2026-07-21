using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Dbosoft.ConfigService.IntegrationTests;

/// <summary>
/// In-process Config Service host for integration tests. Each instance gets
/// a fresh temporary data directory and a deterministic bootstrap admin
/// credential, both cleaned up on disposal.
/// </summary>
public class ConfigServiceWebAppFactory : WebApplicationFactory<Program>
{
    public string AdminId { get; } = "test-admin";

    public string AdminSecret { get; } =
        Convert.ToBase64String(Encoding.UTF8.GetBytes("test-secret-key-32-bytes-long!!!"));

    public string DataDirectory { get; } =
        Path.Combine(Path.GetTempPath(), "configservice-it-" + Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");
        builder.ConfigureAppConfiguration(cfg =>
        {
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConfigService:DataDirectory"] = DataDirectory,
                ["ConfigService:BootstrapAdminId"] = AdminId,
                ["ConfigService:BootstrapAdminSecret"] = AdminSecret,
            });
        });
    }

    /// <summary>
    /// Returns an HttpClient that signs every request with the bootstrap
    /// admin credential, suitable for hitting the admin API directly.
    /// </summary>
    public HttpClient CreateAdminClient()
    {
        var inner = Server.CreateHandler();
        var hmac = new HmacClientHandler(AdminId, AdminSecret, inner);
        return new HttpClient(hmac) { BaseAddress = Server.BaseAddress };
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(DataDirectory))
        {
            try
            {
                Directory.Delete(DataDirectory, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup; the temp dir GC will catch leftovers.
            }
        }
    }
}
