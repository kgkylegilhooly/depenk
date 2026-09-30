using System.Text.RegularExpressions;

namespace Depenk.Scanning.Config;

public static class Glob
{
    public static bool IsMatch(string pattern, string value)
    {
        var regex = "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
        return Regex.IsMatch(value, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    public static bool Any(IEnumerable<string> patterns, string value) => patterns.Any(p => IsMatch(p, value));
}
