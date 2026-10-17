using System.Net;
using System.Net.Http.Json;
using System.Text;
using AwesomeAssertions;
using Dbosoft.ConfigService.Endpoints;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Dbosoft.ConfigService.IntegrationTests;

/// <summary>
/// The applications that only read the configuration sign in with a reader the platform declares,
/// not with the admin credential that may change it.
/// </summary>
public sealed class DeclaredReaderTests : IClassFixture<DeclaredReaderTests.Factory>
{
    private readonly Factory _factory;

    public DeclaredReaderTests(Factory factory) => _factory = factory;

    [Fact]
    public async Task The_declared_reader_reads_and_may_not_write()
    {
        _ = _factory.Server;
        var reader = new HttpClient(new HmacClientHandler(Factory.ReaderId, Factory.ReaderSecret, _factory.Server.CreateHandler()))
            { BaseAddress = _factory.Server.BaseAddress };

        (await reader.GetAsync("/kv")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await reader.PutAsJsonAsync("/admin/kv", new AdminPutKeyValueRequest { Key = "k", Value = "v" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task The_bootstrap_admin_is_still_created_beside_it()
    {
        var admin = _factory.CreateAdminClient();

        (await admin.PutAsJsonAsync("/admin/kv", new AdminPutKeyValueRequest { Key = "k", Value = "v" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    public sealed class Factory : ConfigServiceWebAppFactory
    {
        public const string ReaderId = "modules";
        public static readonly string ReaderSecret = Convert.ToBase64String(Encoding.UTF8.GetBytes("reader-key-of-32-bytes-length!!!"));

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration(cfg => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConfigService:ReaderId"] = ReaderId,
                ["ConfigService:ReaderSecret"] = ReaderSecret,
            }));
        }
    }
}
