using System.Text.RegularExpressions;

namespace Depenk.Analysis.Routes;

public static partial class RouteNormalizer
{
    public static string Normalize(string route)
    {
        var r = route.Trim();
        var scheme = r.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
        {
            var pathStart = r.IndexOf('/', scheme + 3);
            r = pathStart < 0 ? "" : r[pathStart..];
        }
        r = r.TrimStart('~');
        r = Placeholder().Replace(r, "{}");
        var cut = r.IndexOfAny(['?', '#']);
        if (cut >= 0) r = r[..cut];
        var segments = r.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join('/', segments).ToLowerInvariant();
    }

    public static string Combine(string? prefix, string? template)
    {
        var t = template?.Trim() ?? "";
        if (t.StartsWith("~/", StringComparison.Ordinal)) t = t[1..];
        string[] parts = t.StartsWith('/') ? [t] : [prefix ?? "", t];
        var joined = string.Join('/', parts.SelectMany(p => p.Split('/', StringSplitOptions.RemoveEmptyEntries)));
        return "/" + joined;
    }

    public static string ReplaceTokens(string template, string controllerName, string actionName)
    {
        var controller = controllerName.EndsWith("Controller", StringComparison.Ordinal)
            ? controllerName[..^"Controller".Length] : controllerName;
        var withController = Regex.Replace(template, @"\[controller\]", controller.ToLowerInvariant(), RegexOptions.IgnoreCase);
        return Regex.Replace(withController, @"\[action\]", actionName.ToLowerInvariant(), RegexOptions.IgnoreCase);
    }

    [GeneratedRegex(@"\{[^{}]*\}")]
    private static partial Regex Placeholder();
}
