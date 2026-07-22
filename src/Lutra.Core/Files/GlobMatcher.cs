using System.Text.RegularExpressions;

namespace Lutra.Core.Files;

/// <summary>
/// Minimal glob-style matcher for archive exclude patterns.
/// <c>*</c> matches any sequence of characters (including <c>/</c>),
/// <c>?</c> matches a single character. Matching is case-insensitive.
/// </summary>
internal static class GlobMatcher
{
    public static bool IsMatch(string pattern, string value)
    {
        var regex = "^" + Regex.Escape(pattern)
            .Replace("\\*", ".*")
            .Replace("\\?", ".") + "$";

        return Regex.IsMatch(value, regex, RegexOptions.IgnoreCase);
    }
}
