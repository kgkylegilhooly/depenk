using System.Xml;
using System.Xml.Linq;

namespace Depenk.Scanning;

public static class ProjectParser
{
    private const string TestSdk = "Microsoft.NET.Test.Sdk";

    /// <summary>Name fallback for test detection, unless the project says IsTestProject=false.</summary>
    private static readonly string[] TestNameSuffixes = [".Tests", ".UnitTests", ".IntegrationTests", ".Test"];

    /// <summary>Malformed XML in the csproj or any props file becomes a ProjectParseException.</summary>
    public static ProjectFile Parse(string csprojPath, string repoRoot)
    {
        try { return ParseCore(csprojPath, repoRoot); }
        catch (XmlException ex) { throw new ProjectParseException(csprojPath, ex); }
    }

    private static ProjectFile ParseCore(string csprojPath, string repoRoot)
    {
        var doc = XDocument.Load(csprojPath);
        var (props, central, propsPackageRefs) = PropsResolver.Collect(Path.GetDirectoryName(csprojPath)!, repoRoot);
        foreach (var (k, v) in PropsResolver.ReadProperties(doc)) props[k] = v; // project wins

        string? Prop(string name) => props.TryGetValue(name, out var v) && v.Length > 0 ? PropsResolver.Expand(v, props) : null;

        var name = Path.GetFileNameWithoutExtension(csprojPath);
        var sdk = (string?)doc.Root?.Attribute("Sdk")
                  ?? doc.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "Sdk")?.Attribute("Name")?.Value;
        var explicitId = Prop("PackageId");

        var packageRefs = doc.Descendants().Where(e => e.Name.LocalName == "PackageReference")
            .Select(e =>
            {
                var id = (string?)e.Attribute("Include");
                if (id is null) return null;
                var raw = (string?)e.Attribute("VersionOverride")
                          ?? (string?)e.Attribute("Version")
                          ?? e.Elements().FirstOrDefault(c => c.Name.LocalName == "Version")?.Value
                          ?? (central.TryGetValue(id, out var cv) ? cv : null);
                return new PackageRef(id, PropsResolver.Expand(raw, props));
            })
            .OfType<PackageRef>()
            .ToList();

        var projectRefs = doc.Descendants().Where(e => e.Name.LocalName == "ProjectReference")
            .Select(e => (string?)e.Attribute("Include"))
            .OfType<string>()
            .Select(p => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(csprojPath)!,
                p.Replace('\\', Path.DirectorySeparatorChar))))
            .ToList();

        var isTestProp = Prop("IsTestProject");
        var isTest = string.Equals(isTestProp, "true", StringComparison.OrdinalIgnoreCase)
                     || packageRefs.Any(r => r.Id.Equals(TestSdk, StringComparison.OrdinalIgnoreCase))
                     || propsPackageRefs.Contains(TestSdk)
                     || (!string.Equals(isTestProp, "false", StringComparison.OrdinalIgnoreCase)
                         && TestNameSuffixes.Any(s => name.EndsWith(s, StringComparison.OrdinalIgnoreCase)));
        var packableProp = Prop("IsPackable");
        var isPackable = packableProp is not null
            ? packableProp.Equals("true", StringComparison.OrdinalIgnoreCase)
            : explicitId is not null
              || string.Equals(Prop("GeneratePackageOnBuild"), "true", StringComparison.OrdinalIgnoreCase);

        return new ProjectFile(
            AbsolutePath: Path.GetFullPath(csprojPath),
            Name: name,
            Sdk: sdk,
            EffectivePackageId: explicitId ?? Prop("AssemblyName") ?? name,
            ExplicitPackageId: explicitId is not null,
            Version: Prop("Version") ?? Prop("PackageVersion") ?? Prop("VersionPrefix"),
            IsPackable: isPackable && !isTest,
            IsTestProject: isTest,
            PackageReferences: packageRefs,
            ProjectReferences: projectRefs);
    }
}
