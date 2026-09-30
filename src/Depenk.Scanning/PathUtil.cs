namespace Depenk.Scanning;

public static class PathUtil
{
    public static readonly HashSet<string> SkippedDirs =
        new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", "node_modules", ".git", ".depenk" };

    public static string Rel(string workspace, string absolutePath) =>
        Path.GetRelativePath(workspace, absolutePath).Replace('\\', '/');

    /// <summary>Recursively enumerates files matching the pattern, skipping SkippedDirs.</summary>
    public static IEnumerable<string> EnumerateFiles(string root, string searchPattern)
    {
        var stack = new Stack<string>([root]);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            foreach (var f in Directory.EnumerateFiles(dir, searchPattern)) yield return f;
            foreach (var d in Directory.EnumerateDirectories(dir))
                if (!SkippedDirs.Contains(Path.GetFileName(d))) stack.Push(d);
        }
    }
}
