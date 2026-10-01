using System.Security;

namespace Depenk.Scanning;

public static class PathUtil
{
    public static readonly HashSet<string> SkippedDirs =
        new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", "node_modules", ".git", ".depenk" };

    public static string Rel(string workspace, string absolutePath) =>
        Path.GetRelativePath(workspace, absolutePath).Replace('\\', '/');

    /// <summary>
    /// Recursively enumerates files matching the pattern, skipping SkippedDirs. A directory that cannot be read
    /// (access denied, deleted mid-scan, broken link, ...) is skipped and reported through <paramref name="onError"/>
    /// with its absolute path; the rest of the tree is still enumerated. Never throws for an unreadable directory.
    /// </summary>
    public static IEnumerable<string> EnumerateFiles(string root, string searchPattern, Action<string, Exception>? onError = null)
    {
        var stack = new Stack<string>([root]);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            string[] files, dirs;
            try
            {
                files = Directory.GetFiles(dir, searchPattern);
                dirs = Directory.GetDirectories(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                onError?.Invoke(dir, ex);
                continue;
            }
            Array.Sort(files, StringComparer.Ordinal);
            foreach (var f in files) yield return f;
            Array.Sort(dirs, StringComparer.Ordinal);
            for (var i = dirs.Length - 1; i >= 0; i--) // reverse push: subdirectories pop in ordinal order
                if (!SkippedDirs.Contains(Path.GetFileName(dirs[i]))) stack.Push(dirs[i]);
        }
    }

    /// <summary>Exception messages embed absolute paths; the graph must stay workspace-relative.</summary>
    public static string StripWorkspace(string workspace, string message)
    {
        var root = workspace.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return message
            .Replace(root + Path.DirectorySeparatorChar, "", StringComparison.OrdinalIgnoreCase)
            .Replace(root.Replace('\\', '/') + "/", "", StringComparison.OrdinalIgnoreCase);
    }
}
