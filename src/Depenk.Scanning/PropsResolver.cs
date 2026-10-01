using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Depenk.Scanning;

public static partial class PropsResolver
{
    /// <summary>
    /// Walks from projectDir up to repoRoot (inclusive). Properties from every Directory.Build.props are merged
    /// with nearer files winning; central versions come from the nearest Directory.Packages.props.
    /// </summary>
    public static (Dictionary<string, string> Properties, Dictionary<string, string> CentralVersions, HashSet<string> PackageReferenceIds) Collect(
        string projectDir, string repoRoot)
    {
        var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var central = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var packageRefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dirs = new List<string>();
        var root = Path.GetFullPath(repoRoot).TrimEnd(Path.DirectorySeparatorChar);
        for (var d = Path.GetFullPath(projectDir); d is not null; d = Path.GetDirectoryName(d))
        {
            dirs.Add(d);
            if (string.Equals(d.TrimEnd(Path.DirectorySeparatorChar), root, StringComparison.OrdinalIgnoreCase)) break;
        }

        var centralFound = false;
        foreach (var dir in dirs) // nearest first
        {
            var buildProps = Path.Combine(dir, "Directory.Build.props");
            if (File.Exists(buildProps))
            {
                var doc = XDocument.Load(buildProps);
                foreach (var (k, v) in ReadProperties(doc))
                    props.TryAdd(k, v); // nearer already added → wins
                // items are additive in MSBuild; only the ids are needed (test-SDK detection)
                foreach (var id in doc.Descendants().Where(e => e.Name.LocalName == "PackageReference")
                             .Select(e => (string?)e.Attribute("Include")).OfType<string>())
                    packageRefs.Add(id);
            }

            var pkgProps = Path.Combine(dir, "Directory.Packages.props");
            if (!centralFound && File.Exists(pkgProps))
            {
                centralFound = true;
                foreach (var e in XDocument.Load(pkgProps).Descendants().Where(e => e.Name.LocalName == "PackageVersion"))
                {
                    var id = (string?)e.Attribute("Include");
                    var ver = (string?)e.Attribute("Version");
                    if (id is not null && ver is not null) central[id] = ver;
                }
            }
        }
        return (props, central, packageRefs);
    }

    public static IEnumerable<(string Key, string Value)> ReadProperties(XDocument doc) =>
        doc.Descendants().Where(e => e.Name.LocalName == "PropertyGroup")
            .SelectMany(pg => pg.Elements())
            .Select(e => (e.Name.LocalName, e.Value.Trim()));

    /// <summary>Substitutes $(Name) up to 5 levels deep; leaves "unresolved($(X))" if any reference is unknown.</summary>
    public static string? Expand(string? value, IReadOnlyDictionary<string, string> props)
    {
        if (value is null) return null;
        for (var i = 0; i < 5 && value.Contains("$("); i++)
            value = PropRef().Replace(value, m => props.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);
        var leftover = PropRef().Match(value);
        return leftover.Success ? $"unresolved({leftover.Value})" : value;
    }

    [GeneratedRegex(@"\$\(([A-Za-z_][A-Za-z0-9_.-]*)\)")]
    private static partial Regex PropRef();
}
