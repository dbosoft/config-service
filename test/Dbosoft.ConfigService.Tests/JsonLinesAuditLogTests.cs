using AwesomeAssertions;
using Dbosoft.ConfigService.Audit;
using Microsoft.Extensions.Options;
using Xunit;

namespace Dbosoft.ConfigService.Tests;

public class JsonLinesAuditLogTests : IDisposable
{
    private readonly string _dataDir;
    private readonly JsonLinesAuditLog _log;

    public JsonLinesAuditLogTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "configservice-audit-tests-" + Guid.NewGuid().ToString("N"));
        var opts = Options.Create(new ConfigServiceOptions { DataDirectory = _dataDir });
        _log = new JsonLinesAuditLog(opts);
    }

    public void Dispose()
    {
        _log.Dispose();
        if (Directory.Exists(_dataDir))
        {
            Directory.Delete(_dataDir, true);
        }
    }

    [Fact]
    public async Task ReadAsync_returns_empty_when_no_writes_have_happened()
    {
        var entries = new List<AuditEntry>();
        await foreach (var e in _log.ReadAsync())
        {
            entries.Add(e);
        }
        entries.Should().BeEmpty();
    }

    [Fact]
    public async Task Append_then_Read_returns_the_entry()
    {
        await _log.AppendAsync(new AuditEntry
        {
            Timestamp = DateTimeOffset.UtcNow,
            Action = "PUT",
            Key = "Scim:BackendMode",
            Label = null,
            OldValue = null,
            NewValue = "EntraId",
            CredentialId = "admin",
            ClientIp = "127.0.0.1",
        });

        var entries = new List<AuditEntry>();
        await foreach (var e in _log.ReadAsync())
        {
            entries.Add(e);
        }

        entries.Should().HaveCount(1);
        entries[0].Key.Should().Be("Scim:BackendMode");
        entries[0].NewValue.Should().Be("EntraId");
    }

    [Fact]
    public async Task ReadAsync_skips_corrupt_lines_and_returns_the_valid_ones()
    {
        await _log.AppendAsync(new AuditEntry { Action = "PUT", Key = "k1", NewValue = "v1", CredentialId = "x" });

        // Simulate a partially-written / corrupted line landing in the file
        // between two good entries (e.g. a crash mid-write).
        await File.AppendAllTextAsync(
            Path.Combine(_dataDir, "audit.jsonl"),
            "this is not valid json" + Environment.NewLine);

        await _log.AppendAsync(new AuditEntry { Action = "PUT", Key = "k2", NewValue = "v2", CredentialId = "x" });

        var entries = new List<AuditEntry>();
        await foreach (var e in _log.ReadAsync())
        {
            entries.Add(e);
        }

        entries.Should().HaveCount(2);
        entries.Select(e => e.Key).Should().BeEquivalentTo(new[] { "k1", "k2" });
    }

    [Fact]
    public async Task ReadAsync_with_key_filter_returns_only_matching_entries()
    {
        await _log.AppendAsync(new AuditEntry { Action = "PUT", Key = "a", NewValue = "1", CredentialId = "x" });
        await _log.AppendAsync(new AuditEntry { Action = "PUT", Key = "b", NewValue = "2", CredentialId = "x" });
        await _log.AppendAsync(new AuditEntry { Action = "PUT", Key = "a", NewValue = "3", CredentialId = "x" });

        var matches = new List<AuditEntry>();
        await foreach (var e in _log.ReadAsync("a"))
        {
            matches.Add(e);
        }

        matches.Should().HaveCount(2);
        matches.Select(e => e.NewValue).Should().BeEquivalentTo(new[] { "1", "3" });
    }
}
