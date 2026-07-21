using System.Text;

namespace Dbosoft.ConfigService.Storage;

/// <summary>
/// Implements the subset of the Azure App Configuration key/label filter
/// syntax this service supports:
/// <list type="bullet">
/// <item><c>null</c> or <c>"*"</c> matches anything.</item>
/// <item>A comma-separated list is an OR of alternatives (e.g.
/// <c>"keyA,keyB"</c>). Empty alternatives — produced by a leading, trailing
/// or doubled comma — match nothing; they never broaden the result set.</item>
/// <item>Each alternative is an exact match, or — if it ends with an
/// unescaped <c>*</c> — a prefix match (e.g. <c>"app*"</c>). A bare <c>"*"</c>
/// alternative therefore matches anything.</item>
/// <item>The reserved characters <c>*</c>, <c>,</c> and <c>\</c> can be
/// matched literally by escaping them with a backslash (e.g. <c>"a\,b"</c>
/// matches the single key <c>a,b</c>, and <c>"rate\*"</c> matches the literal
/// key <c>rate*</c>).</item>
/// </list>
/// Mid-string wildcards are not supported. Parsing is a single forward pass,
/// so adversarial escape-heavy input stays linear.
/// </summary>
public static class KeyValueFilter
{
    /// <summary>
    /// True when the filter selects every value (<c>null</c> or <c>"*"</c>).
    /// </summary>
    public static bool IsMatchAll(string? filter) => filter is null or "*";

    /// <summary>
    /// Evaluates <paramref name="filter"/> against <paramref name="value"/>
    /// using the semantics documented on this type.
    /// </summary>
    public static bool Matches(string value, string? filter)
    {
        if (IsMatchAll(filter))
        {
            return true;
        }

        foreach (var alternative in SplitAlternatives(filter!))
        {
            // An empty alternative (from a stray leading/trailing/doubled
            // comma) matches nothing. In particular it must not match the
            // empty string that a no-label entry is coerced to — the "no
            // label" case has its own dedicated sentinel handling in the
            // store, and a filter like "label=,prod" must not silently pull in
            // every unlabeled entry.
            if (alternative.Length == 0)
            {
                continue;
            }

            if (MatchesAlternative(value, alternative))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesAlternative(string value, string raw)
    {
        var literal = new StringBuilder(raw.Length);
        var wildcardSuffix = false;

        for (var i = 0; i < raw.Length; i++)
        {
            var c = raw[i];
            if (c == '\\' && i + 1 < raw.Length)
            {
                // Escaped character: emit the following char literally.
                literal.Append(raw[i + 1]);
                i++;
                wildcardSuffix = false;
            }
            else if (c == '*' && i == raw.Length - 1)
            {
                // An unescaped trailing '*' denotes a prefix match.
                wildcardSuffix = true;
            }
            else
            {
                literal.Append(c);
                wildcardSuffix = false;
            }
        }

        var prefix = literal.ToString();
        return wildcardSuffix
            ? value.StartsWith(prefix, StringComparison.Ordinal)
            : string.Equals(value, prefix, StringComparison.Ordinal);
    }

    /// <summary>
    /// Splits the filter on unescaped commas in a single forward pass.
    /// </summary>
    private static IEnumerable<string> SplitAlternatives(string filter)
    {
        var start = 0;
        var escaped = false;

        for (var i = 0; i < filter.Length; i++)
        {
            if (escaped)
            {
                escaped = false;
            }
            else if (filter[i] == '\\')
            {
                escaped = true;
            }
            else if (filter[i] == ',')
            {
                yield return filter[start..i];
                start = i + 1;
            }
        }

        yield return filter[start..];
    }
}
