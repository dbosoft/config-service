using System.Net.Http.Json;
using AwesomeAssertions;
using Azure.Core.Pipeline;
using Dbosoft.ConfigService.Endpoints;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.AzureAppConfiguration;
using Xunit;

namespace Dbosoft.ConfigService.IntegrationTests;

/// <summary>
/// The non-negotiable test from the RFC. Boots the Config Service in
/// process, seeds a value via the admin API, then constructs a real
/// <c>Microsoft.Extensions.Configuration.AzureAppConfiguration</c>
/// provider pointed at the in-process server using a connection string,
/// and asserts the value lands in <see cref="IConfiguration"/>. If this
/// test fails, the protocol-compatibility claim of the entire project
/// is unverified.
///
/// The Microsoft provider's HTTP pipeline transport is replaced with the
/// TestServer-backed HttpClient via <c>ConfigureClientOptions</c>, so the
/// request is signed and verified end-to-end without going through a real
/// socket.
/// </summary>
public class MicrosoftProviderRoundtripTests : IClassFixture<ConfigServiceWebAppFactory>
{
    private readonly ConfigServiceWebAppFactory _factory;

    public MicrosoftProviderRoundtripTests(ConfigServiceWebAppFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Microsoft_provider_reads_value_from_Config_Service_via_real_HMAC()
    {
        var admin = _factory.CreateAdminClient();
        var put = await admin.PutAsJsonAsync(
            "/admin/kv",
            new AdminPutKeyValueRequest { Key = "Scim:BackendMode", Value = "EntraId" });
        put.EnsureSuccessStatusCode();

        var connectionString =
            $"Endpoint={_factory.Server.BaseAddress.ToString().TrimEnd('/')};" +
            $"Id={_factory.AdminId};Secret={_factory.AdminSecret}";

        var configBuilder = new ConfigurationBuilder();
        configBuilder.AddAzureAppConfiguration(options =>
        {
            options.Connect(connectionString);
            options.ConfigureClientOptions(co =>
            {
                co.Transport = new HttpClientTransport(_factory.CreateClient());
                co.Retry.MaxRetries = 0;
            });
        });

        var config = configBuilder.Build();

        config["Scim:BackendMode"].Should().Be("EntraId");
    }
}
