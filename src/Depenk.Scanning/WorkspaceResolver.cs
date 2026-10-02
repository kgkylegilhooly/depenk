namespace Depenk.Scanning;

public static class WorkspaceResolver
{
    public const string EnvVar = "DEPENK_WORKSPACE";

    public static string Resolve(string? explicitPath, string currentDirectory, Func<string, string?> getEnv)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath)) return Path.GetFullPath(explicitPath);
        var fromEnv = getEnv(EnvVar);
        if (!string.IsNullOrWhiteSpace(fromEnv)) return Path.GetFullPath(fromEnv);

        var cwd = Path.GetFullPath(currentDirectory);
        if (Path.GetPathRoot(cwd) != cwd) cwd = cwd.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (IsMarked(cwd)) return cwd;
        var parent = Path.GetDirectoryName(cwd);
        if (parent is not null && IsGitRepo(cwd) && (IsMarked(parent) || CountGitRepos(parent) >= 2)) return parent;
        return cwd;
    }

    private static bool IsMarked(string dir) =>
        File.Exists(Path.Combine(dir, "depenk.yml")) || Directory.Exists(Path.Combine(dir, ".depenk"));

    private static bool IsGitRepo(string dir) =>
        Directory.Exists(Path.Combine(dir, ".git")) || File.Exists(Path.Combine(dir, ".git"));

    private static int CountGitRepos(string dir)
    {
        try { return Directory.EnumerateDirectories(dir).Count(IsGitRepo); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0; }
    }
}
