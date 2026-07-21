using AwesomeAssertions;
using Dbosoft.ConfigService.Authentication;
using Xunit;

namespace Dbosoft.ConfigService.Tests;

public class CredentialTests
{
    [Fact]
    public void IsRevoked_is_false_when_RevokedAt_is_null()
        => new Credential { RevokedAt = null }.IsRevoked.Should().BeFalse();

    [Fact]
    public void IsRevoked_is_true_when_RevokedAt_is_in_the_past()
        => new Credential { RevokedAt = DateTimeOffset.UtcNow.AddMinutes(-1) }
            .IsRevoked.Should().BeTrue();

    [Fact]
    public void IsRevoked_is_false_when_RevokedAt_is_in_the_future()
    {
        // A future RevokedAt means "scheduled for later" and is not yet in
        // effect — a real distinction worth pinning down so a timezone-naive
        // future date does not silently behave as an immediate revocation
        // (or vice versa).
        new Credential { RevokedAt = DateTimeOffset.UtcNow.AddMinutes(5) }
            .IsRevoked.Should().BeFalse();
    }

    [Fact]
    public void IsExpired_is_false_when_ExpiresAt_is_null()
        => new Credential { ExpiresAt = null }.IsExpired.Should().BeFalse();

    [Fact]
    public void IsExpired_is_true_when_ExpiresAt_is_in_the_past()
        => new Credential { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) }
            .IsExpired.Should().BeTrue();

    [Fact]
    public void IsExpired_is_false_when_ExpiresAt_is_in_the_future()
        => new Credential { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5) }
            .IsExpired.Should().BeFalse();
}
