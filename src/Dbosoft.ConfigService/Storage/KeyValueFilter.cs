using System.Text;

namespace Dbosoft.ConfigService.Storage;

/// <summary>
/// Implements the subset of the Azure App Configuration key/label filter
/// syntax this service supports:
/// <list type="bullet">
/// <item><c>null</c> or <c>"*"</c> matches anything.</item>
/// <item>A comma-separated list is an OR of alternatives (e.g.
/// <c>"keyA,keyB"</c>).</item>
/// <item>Each alternative is an exact match, or — if it ends with an
/// unescaped <c>*</c> — a prefix match (e.g. <c>"app*"</c>).</item>
/// <item>The reserved characters <c>*</c>, <c>,</c> and <c>\</c> can be
/// matched literally by escaping them with a backslash (e.g. <c>"a\,b"</c>
/// matches the single key <c>a,b</c>, and <c>"rate\*"</c> matches the literal
/// key <c>rate*</c>).</item>
/// </list>
/// Mid-string wildcards are not supported.
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
            if (MatchesAlternative(value, alternative))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesAlternative(string value, string raw)
    {
        // A trailing '*' that is not itself escaped denotes a prefix match.
        var isPrefix = false;
        if (raw.Length > 0 && raw[^1] == '*' && !IsEscaped(raw, raw.Length - 1))
        {
            isPrefix = true;
            raw = raw[..^1];
        }

        var literal = Unescape(raw);
        return isPrefix
            ? value.StartsWith(literal, StringComparison.Ordinal)
            : string.Equals(value, literal, StringComparison.Ordinal);
    }

    private static IEnumerable<string> SplitAlternatives(string filter)
    {
        var start = 0;
        for (var i = 0; i < filter.Length; i++)
        {
            if (filter[i] == ',' && !IsEscaped(filter, i))
            {
                yield return filter[start..i];
                start = i + 1;
            }
        }

        yield return filter[start..];
    }

    /// <summary>
    /// True when the character at <paramref name="index"/> is preceded by an
    /// odd number of backslashes (and is therefore escaped).
    /// </summary>
    private static bool IsEscaped(string s, int index)
    {
        var backslashes = 0;
        for (var i = index - 1; i >= 0 && s[i] == '\\'; i--)
        {
            backslashes++;
        }

        return backslashes % 2 == 1;
    }

    private static string Unescape(string s)
    {
        if (s.IndexOf('\\') < 0)
        {
            return s;
        }

        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length)
            {
                sb.Append(s[i + 1]);
                i++;
            }
            else
            {
                sb.Append(s[i]);
            }
        }

        return sb.ToString();
    }
}
