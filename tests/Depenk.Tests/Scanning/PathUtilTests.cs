using Depenk.Scanning;
using Depenk.Tests.TestUtil;

namespace Depenk.Tests.Scanning;

public class PathUtilTests
{
    [Fact]
    public void EnumerateFiles_MissingRoot_ReportsAndDoesNotThrow()
    {
        using var ws = new TempWorkspace();
        var missing = Path.Combine(ws.Root, "gone");
        var errors = new List<string>();

        var files = PathUtil.EnumerateFiles(missing, "*.cs", (dir, _) => errors.Add(dir)).ToList();

        Assert.Empty(files);
        Assert.Equal([missing], errors);
    }

    /// <summary>
    /// Deterministic stand-in for an unreadable directory: subdirectories are listed, then deleted before they are
    /// visited, so reading them throws DirectoryNotFoundException exactly like an access-denied folder throws
    /// UnauthorizedAccessException. Both must be skipped and reported while the rest of the tree is still returned.
    /// </summary>
    [Fact]
    public void EnumerateFiles_DirectoryVanishingMidEnumeration_IsSkippedAndReported()
    {
        using var ws = new TempWorkspace()
            .File("r/top.cs", "")
            .File("r/a/x.cs", "")
            .File("r/b/y.cs", "");
        var root = Path.Combine(ws.Root, "r");
        var errors = new List<(string Dir, Exception Ex)>();
        var files = new List<string>();

        foreach (var f in PathUtil.EnumerateFiles(root, "*.cs", (d, ex) => errors.Add((d, ex))))
        {
            files.Add(PathUtil.Rel(ws.Root, f));
            if (files.Count == 1) Directory.Delete(Path.Combine(root, "a"), recursive: true);
        }

        Assert.Equal(["r/top.cs", "r/b/y.cs"], files);
        var (dir, ex) = Assert.Single(errors);
        Assert.Equal(Path.Combine(root, "a"), dir);
        Assert.IsAssignableFrom<IOException>(ex);
    }

    [Fact]
    public void EnumerateFiles_WithoutCallback_SkipsSilently()
    {
        using var ws = new TempWorkspace();
        Assert.Empty(PathUtil.EnumerateFiles(Path.Combine(ws.Root, "gone"), "*.cs"));
    }

    [Fact]
    public void StripWorkspace_RemovesBothSeparatorStyles()
    {
        var ws = Path.Combine(Path.GetTempPath(), "wsroot");
        var msg = $"Access to the path '{Path.Combine(ws, "r", "x")}' is denied. {ws.Replace('\\', '/')}/r/y";
        Assert.Equal($"Access to the path '{Path.Combine("r", "x")}' is denied. r/y", PathUtil.StripWorkspace(ws, msg));
    }
}
