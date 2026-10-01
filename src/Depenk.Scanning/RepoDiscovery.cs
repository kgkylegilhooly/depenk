using Depenk.Scanning.Config;

namespace Depenk.Scanning;

/// <param name="Name">Unique within one discovery. Same-named repos at different paths get "#2", "#3", ...</param>
/// <param name="BaseName">The folder name before any "#N" suffix was added.</param>
public sealed record DiscoveredRepo(string Name, string AbsolutePath, string RelativePath, string? HeadSha, bool Dirty)
{
    public string BaseName { get; init; } = Name;
}

public static class RepoDiscovery
{
    public static List<DiscoveredRepo> Discover(string workspace, DepenkConfig config)
    {
        workspace = Path.GetFullPath(workspace);
        var candidates = config.Repos.Paths.Count > 0
            ? config.Repos.Paths.Select(p => Path.GetFullPath(Path.Combine(workspace, p)).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                .Where(Directory.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase) // the same folder listed twice is one repo
            : Directory.EnumerateDirectories(workspace).Where(IsGitRepo);
        var repos = candidates
            .Select(d => Make(workspace, d, Path.GetFileName(d)))
            .Where(r => Glob.Any(config.Repos.Include, r.Name) && !Glob.Any(config.Repos.Exclude, r.Name))
            .OrderBy(r => r.Name, StringComparer.Ordinal)
            .ThenBy(r => r.RelativePath, StringComparer.Ordinal)
            .ToList();

        if (repos.Count == 0 && config.Repos.Paths.Count == 0 && IsGitRepo(workspace))
            repos.Add(Make(workspace, workspace, Path.GetFileName(workspace.TrimEnd(Path.DirectorySeparatorChar))));
        return Uniquify(repos);
    }

    /// <summary>Same-named repos at different paths: the first (by relative path) keeps the name, later ones get "#N".</summary>
    private static List<DiscoveredRepo> Uniquify(List<DiscoveredRepo> repos)
    {
        var used = repos.Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<DiscoveredRepo>(repos.Count);
        foreach (var r in repos)
        {
            if (seen.Add(r.Name)) { result.Add(r); continue; }
            var name = r.Name;
            for (var n = 2; used.Contains(name); n++) name = $"{r.Name}#{n}";
            used.Add(name);
            result.Add(r with { Name = name, BaseName = r.Name });
        }
        return result;
    }

    private static bool IsGitRepo(string dir) =>
        Directory.Exists(Path.Combine(dir, ".git")) || File.Exists(Path.Combine(dir, ".git"));

    private static DiscoveredRepo Make(string workspace, string dir, string name) =>
        new(name, dir, PathUtil.Rel(workspace, dir), TryReadHead(dir), Dirty: false);

    private static string? TryReadHead(string dir)
    {
        try { return ReadHead(dir); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; } // unreadable .git: no sha
    }

    /// <summary>Reads HEAD without shelling out. Handles detached HEAD, loose refs and packed-refs.</summary>
    internal static string? ReadHead(string repoDir)
    {
        var gitDir = Path.Combine(repoDir, ".git");
        if (File.Exists(gitDir)) // worktree/submodule: "gitdir: <path>"
        {
            var pointer = File.ReadAllText(gitDir).Trim();
            if (!pointer.StartsWith("gitdir:")) return null;
            gitDir = Path.GetFullPath(Path.Combine(repoDir, pointer["gitdir:".Length..].Trim()));
        }
        var headFile = Path.Combine(gitDir, "HEAD");
        if (!File.Exists(headFile)) return null;
        var head = File.ReadAllText(headFile).Trim();
        if (!head.StartsWith("ref:")) return head;

        var refName = head["ref:".Length..].Trim();
        var loose = Path.Combine(gitDir, refName.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(loose)) return File.ReadAllText(loose).Trim();

        var packed = Path.Combine(gitDir, "packed-refs");
        if (!File.Exists(packed)) return null;
        return File.ReadLines(packed)
            .Select(l => l.Split(' ', 2))
            .FirstOrDefault(p => p.Length == 2 && p[1].Trim() == refName)?[0];
    }
}
