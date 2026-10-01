namespace Depenk.Scanning;

public sealed record PackageRef(string Id, string? Version);

public sealed record ProjectFile(
    string AbsolutePath, string Name, string? Sdk, string EffectivePackageId, bool ExplicitPackageId,
    string? Version, bool IsPackable, bool IsTestProject,
    List<PackageRef> PackageReferences, List<string> ProjectReferences)
{
    public static bool IsUnresolved(string? v) => v is not null && v.StartsWith("unresolved(", StringComparison.Ordinal);
}

public sealed class ProjectParseException(string path, Exception inner)
    : Exception($"Could not parse {path}: {inner.Message}", inner)
{
    public string ProjectPath { get; } = path;
}
