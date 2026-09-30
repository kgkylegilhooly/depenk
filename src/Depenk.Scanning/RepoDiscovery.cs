using Depenk.Scanning.Config;

namespace Depenk.Scanning;

public sealed record DiscoveredRepo(string Name, string AbsolutePath, string RelativePath, string? HeadSha, bool Dirty);

public static class RepoDiscovery
{
    public static List<DiscoveredRepo> Discover(string workspace, DepenkConfig config)
    {
        workspace = Path.GetFullPath(workspace);
        var candidates = config.Repos.Paths.Count > 0
            ? config.Repos.Paths.Select(p => Path.GetFullPath(Path.Combine(workspace, p))).Where(Directory.Exists)
            : Directory.EnumerateDirectories(workspace).Where(IsGitRepo);
        var repos = candidates
            .Select(d => Make(workspace, d, Path.GetFileName(d)))
            .Where(r => Glob.Any(config.Repos.Include, r.Name) && !Glob.Any(config.Repos.Exclude, r.Name))
            .OrderBy(r => r.Name, StringComparer.Ordinal)
            .ToList();

        if (repos.Count == 0 && config.Repos.Paths.Count == 0 && IsGitRepo(workspace))
            repos.Add(Make(workspace, workspace, Path.GetFileName(workspace.TrimEnd(Path.DirectorySeparatorChar))));
        return repos;
    }

    private static bool IsGitRepo(string dir) =>
        Directory.Exists(Path.Combine(dir, ".git")) || File.Exists(Path.Combine(dir, ".git"));

    private static DiscoveredRepo Make(string workspace, string dir, string name) =>
        new(name, dir, PathUtil.Rel(workspace, dir), ReadHead(dir), Dirty: false);

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
