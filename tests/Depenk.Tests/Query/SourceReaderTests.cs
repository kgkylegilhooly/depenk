using System.Diagnostics;
using Depenk.Core.Model;
using Depenk.Query;
using Depenk.Tests.TestUtil;

namespace Depenk.Tests.Query;

public class SourceReaderTests
{
    private static GraphIndex IndexWith(params (string Id, string Path, int Line)[] endpoints)
    {
        var g = new DepGraph();
        g.Repos.Add(new RepoNode("repo:r", "r", "r", null, false));
        foreach (var (id, path, line) in endpoints)
            g.Endpoints.Add(new EndpointNode(id, "r", "proj:r/P", "GET", "/x", "x", "C.M", [], [], new SourceLocation(path, line)));
        return new GraphIndex(g);
    }

    private static string Lines(int n) => string.Join("\n", Enumerable.Range(1, n).Select(i => $"line{i}"));

    [Fact]
    public void ReadsNumberedSnippetAroundLine()
    {
        using var ws = new TempWorkspace().File("r/src/A.cs", Lines(20));
        var s = new SourceReader(ws.Root).Read(IndexWith(("ep:a", "r/src/A.cs", 5)), "ep:a", context: 2);
        Assert.Equal(("r/src/A.cs", 5, 3), (s.Path, s.Line, s.StartLine));
        Assert.Equal(["    3| line3", "    4| line4", "    5| line5", "    6| line6", "    7| line7"], s.Lines);
    }

    [Fact]
    public void ClampsAtFileBoundaries()
    {
        using var ws = new TempWorkspace().File("r/A.cs", Lines(3));
        var s = new SourceReader(ws.Root).Read(IndexWith(("ep:a", "r/A.cs", 1)), "ep:a", context: 10);
        Assert.Equal((1, 3), (s.StartLine, s.Lines.Count));
    }

    [Theory]
    [InlineData("../outside.cs")]
    [InlineData("r/../../outside.cs")]
    [InlineData("C:/Windows/win.ini")]
    [InlineData("/etc/passwd")]
    public void RefusesPathsOutsideWorkspace(string path)
    {
        using var ws = new TempWorkspace().File("r/A.cs", "x");
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(ws.Root)!, "outside.cs"), "secret");
        var ex = Assert.Throws<QueryException>(() => new SourceReader(ws.Root).Read(IndexWith(("ep:a", path, 1)), "ep:a"));
        // "C:/..." is only an absolute path on Windows; elsewhere it is a (missing) relative path inside the workspace
        var expected = !OperatingSystem.IsWindows() && path.StartsWith("C:") ? "not_found" : "outside_workspace";
        Assert.Equal(expected, ex.Code);
    }

    [Fact]
    public void RefusesFilesReachedThroughALinkToOutside()
    {
        using var ws = new TempWorkspace().File("r/A.cs", "x");
        var outside = Path.Combine(Path.GetTempPath(), "depenk-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.cs"), "secret");
        try
        {
            var link = Path.Combine(ws.Root, "r", "linked");
            Assert.True(TryLinkDirectory(link, outside), "could not create a directory junction/symlink for the test");
            var ex = Assert.Throws<QueryException>(() =>
                new SourceReader(ws.Root).Read(IndexWith(("ep:a", "r/linked/secret.cs", 1)), "ep:a"));
            Assert.Equal("outside_workspace", ex.Code);
        }
        finally { Directory.Delete(outside, recursive: true); }
    }

    [Fact]
    public void NodesWithoutLocation_AndMissingFiles()
    {
        using var ws = new TempWorkspace();
        var ix = IndexWith(("ep:gone", "r/Gone.cs", 1));
        Assert.Equal("invalid_argument", Assert.Throws<QueryException>(() => new SourceReader(ws.Root).Read(ix, "repo:r")).Code);
        Assert.Equal("not_found", Assert.Throws<QueryException>(() => new SourceReader(ws.Root).Read(ix, "ep:gone")).Code);
        Assert.Equal("not_found", Assert.Throws<QueryException>(() => new SourceReader(ws.Root).Read(ix, "ep:nope")).Code);
    }

    private static bool TryLinkDirectory(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        if (!OperatingSystem.IsWindows()) return false;
        using var p = Process.Start(new ProcessStartInfo("cmd", $"/c mklink /J \"{link}\" \"{target}\"")
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!;
        p.WaitForExit();
        return p.ExitCode == 0;
    }
}
