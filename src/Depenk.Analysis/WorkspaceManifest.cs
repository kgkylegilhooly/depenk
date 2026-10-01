using System.Text.Json;
using Depenk.Scanning;
using Depenk.Scanning.Config;

namespace Depenk.Analysis;

public static class WorkspaceManifest
{
    private static readonly string[] Patterns = ["*.cs", "*.csproj", "*.props"];

    public static string ManifestPath(string workspace) => Path.Combine(workspace, ".depenk", "manifest.json");

    public static SortedDictionary<string, string> Compute(string workspace)
    {
        workspace = Path.GetFullPath(workspace);
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var configPath = Path.Combine(workspace, ConfigLoader.FileName);
        if (File.Exists(configPath)) result[ConfigLoader.FileName] = ParseCache.HashText(File.ReadAllText(configPath));

        DepenkConfig config;
        try { config = ConfigLoader.Load(workspace); }
        catch (ConfigException) { return result; } // an invalid config is never "up to date"

        foreach (var repo in RepoDiscovery.Discover(workspace, config))
        {
            result[$"repo:{repo.Name}"] = repo.HeadSha ?? "";
            foreach (var pattern in Patterns)
            foreach (var file in PathUtil.EnumerateFiles(repo.AbsolutePath, pattern,
                         (dir, _) => result[PathUtil.Rel(workspace, dir) + "/"] = Unreadable))
                result[PathUtil.Rel(workspace, file)] = TryHash(file);
        }
        return result;
    }

    private const string Unreadable = "unreadable";

    // An unreadable file or directory is recorded, not fatal: the scan reports it as a parseError.
    private static string TryHash(string file)
    {
        try { return ParseCache.HashText(File.ReadAllText(file)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Unreadable; }
    }

    public static void Save(string workspace, SortedDictionary<string, string> manifest)
    {
        var path = ManifestPath(workspace);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static bool IsUpToDate(string workspace)
    {
        var path = ManifestPath(workspace);
        if (!File.Exists(path) || !File.Exists(ScanOrchestrator.GraphPath(workspace))) return false;
        try
        {
            var saved = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
            if (saved is null) return false;
            var current = Compute(workspace);
            return saved.Count == current.Count
                && current.All(kv => saved.TryGetValue(kv.Key, out var v) && string.Equals(v, kv.Value, StringComparison.Ordinal));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return false; // a corrupt or unreadable manifest/workspace forces a rescan
        }
    }
}
