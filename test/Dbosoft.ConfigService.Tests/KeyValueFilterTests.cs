using AwesomeAssertions;
using Dbosoft.ConfigService.Storage;
using Xunit;

namespace Dbosoft.ConfigService.Tests;

public class KeyValueFilterTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("*")]
    public void IsMatchAll_is_true_for_null_or_star(string? filter)
        => KeyValueFilter.IsMatchAll(filter).Should().BeTrue();

    [Theory]
    [InlineData("")]
    [InlineData("app")]
    [InlineData("app*")]
    public void IsMatchAll_is_false_for_specific_filters(string? filter)
        => KeyValueFilter.IsMatchAll(filter).Should().BeFalse();

    [Theory]
    [InlineData(null, "anything")]
    [InlineData("*", "anything")]
    public void Matches_match_all_matches_any_value(string? filter, string value)
        => KeyValueFilter.Matches(value, filter).Should().BeTrue();

    [Theory]
    [InlineData("Scim:BackendMode", "Scim:BackendMode", true)]
    [InlineData("Scim:BackendMode", "Scim:Backend", false)]
    [InlineData("Scim:BackendMode", "scim:backendmode", false)] // ordinal, case-sensitive
    public void Matches_exact(string filter, string value, bool expected)
        => KeyValueFilter.Matches(value, filter).Should().Be(expected);

    [Theory]
    [InlineData("Scim:*", "Scim:BackendMode", true)]
    [InlineData("Scim:*", "Scim:", true)]
    [InlineData("Scim:*", "Other:Key", false)]
    [InlineData("*", "anything", true)]
    public void Matches_prefix_wildcard(string filter, string value, bool expected)
        => KeyValueFilter.Matches(value, filter).Should().Be(expected);

    [Theory]
    [InlineData("keyA,keyB", "keyA", true)]
    [InlineData("keyA,keyB", "keyB", true)]
    [InlineData("keyA,keyB", "keyC", false)]
    [InlineData("app:*,db:*", "db:host", true)]
    [InlineData("app:*,db:*", "web:host", false)]
    public void Matches_comma_separated_or_list(string filter, string value, bool expected)
        => KeyValueFilter.Matches(value, filter).Should().Be(expected);

    [Theory]
    // Escaped comma: the filter is the single literal key "a,b", not "a" OR "b".
    [InlineData("a\\,b", "a,b", true)]
    [InlineData("a\\,b", "a", false)]
    [InlineData("a\\,b", "b", false)]
    // Escaped star: literal trailing asterisk, not a wildcard.
    [InlineData("rate\\*", "rate*", true)]
    [InlineData("rate\\*", "rateLimit", false)]
    public void Matches_honours_escapes_of_reserved_characters(string filter, string value, bool expected)
        => KeyValueFilter.Matches(value, filter).Should().Be(expected);
}
