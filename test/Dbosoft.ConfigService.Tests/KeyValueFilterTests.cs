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

    [Theory]
    // A bare "*" as one alternative in an OR list means match-all.
    [InlineData("a,*", "anything", true)]
    [InlineData("a,*", "a", true)]
    public void Matches_star_alternative_in_or_list_matches_all(string filter, string value, bool expected)
        => KeyValueFilter.Matches(value, filter).Should().Be(expected);

    [Theory]
    // Empty alternatives (leading / trailing / doubled / lone comma) match
    // nothing — they must never broaden the result set, and in particular an
    // empty alternative must not match the empty string an unlabeled entry is
    // coerced to. This is the "label=,prod must not include unlabeled" fix.
    [InlineData(",prod", "", false)]
    [InlineData(",prod", "prod", true)]
    [InlineData("prod,", "", false)]
    [InlineData("a,,b", "", false)]
    [InlineData(",", "", false)]
    [InlineData("", "", false)]
    [InlineData("", "anything", false)]
    public void Matches_empty_alternatives_match_nothing(string filter, string value, bool expected)
        => KeyValueFilter.Matches(value, filter).Should().Be(expected);

    [Theory]
    // A lone trailing backslash is preserved as a literal backslash (it is
    // not an escape because nothing follows it).
    [InlineData("abc\\", "abc\\", true)]
    [InlineData("abc\\", "abc", false)]
    public void Matches_trailing_lone_backslash_is_literal(string filter, string value, bool expected)
        => KeyValueFilter.Matches(value, filter).Should().Be(expected);
}
