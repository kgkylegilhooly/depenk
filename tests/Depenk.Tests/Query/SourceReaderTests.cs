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
    [InlineData("../WS/secret.cs")]
    [InlineData("r/../../outside.cs")]
    [InlineData("C:/Windows/win.ini")]
    [InlineData("/etc/passwd")]
    [InlineData("r/A.cs:ads")]
    [InlineData("r/a\0.cs")]
    [InlineData("r//A.cs")]
    [InlineData("\\\\server\\share\\x.cs")]
    public void RefusesPathsOutsideWorkspace(string path)
    {
        using var ws = new TempWorkspace().File("r/A.cs", "x");
        var ex = Assert.Throws<QueryException>(() => new SourceReader(ws.Root).Read(IndexWith(("ep:a", path, 1)), "ep:a"));
        // "C:/..." is only an absolute path on Windows; elsewhere it is a (missing) relative path inside the workspace
        var expected = !OperatingSystem.IsWindows() && path.StartsWith("C:") ? "not_found" : "outside_workspace";
        Assert.Equal(expected, ex.Code);
        // Verify error message never contains the absolute workspace root
        Assert.DoesNotContain(ws.Root, ex.Message);
    }

    [Theory]
    [InlineData("r/.env")]
    [InlineData("r/config.json")]
    public void RefusesUnsupportedExtensions(string path)
    {
        using var ws = new TempWorkspace().File(path, "x");
        var ex = Assert.Throws<QueryException>(() => new SourceReader(ws.Root).Read(IndexWith(("ep:a", path, 1)), "ep:a"));
        Assert.Equal("invalid_argument", ex.Code);
    }

    [Fact]
    public void RefusesFilesReachedThroughAnyLink()
    {
        using var ws = new TempWorkspace().File("r/A.cs", "x");
        var inside = Path.Combine(ws.Root, "r", "target-folder");
        Directory.CreateDirectory(inside);
        File.WriteAllText(Path.Combine(inside, "file.cs"), "content");
        var link = Path.Combine(ws.Root, "r", "linked");
        var outside = Path.Combine(Path.GetTempPath(), "depenk-outside-" + Guid.NewGuid().ToString("N"));

        // Test 1: Link to outside workspace
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.cs"), "secret");
        try
        {
            Assert.True(TryLinkDirectory(link, outside), "could not create a directory junction/symlink for the test");
            var ex = Assert.Throws<QueryException>(() =>
                new SourceReader(ws.Root).Read(IndexWith(("ep:a", "r/linked/secret.cs", 1)), "ep:a"));
            Assert.Equal("outside_workspace", ex.Code);
        }
        finally
        {
            try { Directory.Delete(link); } catch { }
            Directory.Delete(outside, recursive: true);
        }

        // Test 2: Link to inside workspace (still rejected)
        try
        {
            Assert.True(TryLinkDirectory(link, inside), "could not create a directory junction/symlink for the test");
            var ex = Assert.Throws<QueryException>(() =>
                new SourceReader(ws.Root).Read(IndexWith(("ep:a", "r/linked/file.cs", 1)), "ep:a"));
            Assert.Equal("outside_workspace", ex.Code);
        }
        finally
        {
            try { Directory.Delete(link); } catch { }
            Directory.Delete(inside, recursive: true);
        }
    }

    [Fact]
    public void RefusesLargeFiles()
    {
        using var ws = new TempWorkspace();
        var largeFile = Path.Combine(ws.Root, "r", "large.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(largeFile)!);
        // Create a file larger than 5 MB
        using (var f = File.Create(largeFile))
        {
            f.SetLength(6 * 1024 * 1024);
        }
        var ex = Assert.Throws<QueryException>(() => new SourceReader(ws.Root).Read(IndexWith(("ep:a", "r/large.cs", 1)), "ep:a"));
        Assert.Equal("invalid_argument", ex.Code);
    }

    [Fact]
    public void TruncatesLongLines()
    {
        using var ws = new TempWorkspace();
        var longLine = new string('x', 10000);
        var content = "line1\n" + longLine + "\nline3";
        ws.File("r/A.cs", content);
        var s = new SourceReader(ws.Root).Read(IndexWith(("ep:a", "r/A.cs", 2)), "ep:a", context: 1);
        Assert.Equal(["    1| line1", "    2| " + new string('x', 1999) + "…", "    3| line3"], s.Lines);
    }

    [Fact]
    public void HugeLineNumber_ShowsEndOfFile_WithoutOverflow()
    {
        using var ws = new TempWorkspace().File("r/A.cs", Lines(20));
        var s = new SourceReader(ws.Root).Read(IndexWith(("ep:a", "r/A.cs", int.MaxValue)), "ep:a", context: 2);
        Assert.Equal(["   18| line18", "   19| line19", "   20| line20"], s.Lines);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void NonPositiveLine_ShowsStartOfFile(int line)
    {
        using var ws = new TempWorkspace().File("r/A.cs", Lines(20));
        var s = new SourceReader(ws.Root).Read(IndexWith(("ep:a", "r/A.cs", line)), "ep:a", context: 2);
        Assert.Equal(1, s.StartLine);
        Assert.Equal(["    1| line1", "    2| line2", "    3| line3"], s.Lines);
    }

    [Fact]
    public void LockedFile_IsNotReportedAsMissing()
    {
        if (!OperatingSystem.IsWindows()) return; // exclusive locks are advisory elsewhere
        using var ws = new TempWorkspace().File("r/A.cs", Lines(3));
        using var lockHandle = new FileStream(Path.Combine(ws.Root, "r", "A.cs"), FileMode.Open, FileAccess.Read, FileShare.None);
        var ex = Assert.Throws<QueryException>(() => new SourceReader(ws.Root).Read(IndexWith(("ep:a", "r/A.cs", 1)), "ep:a"));
        Assert.Equal("invalid_argument", ex.Code);
        Assert.DoesNotContain("no longer exists", ex.Message);
    }

    [Fact]
    public async Task NamedPipe_IsNeverOpened()
    {
        if (OperatingSystem.IsWindows()) return; // no FIFOs on the Windows filesystem
        using var ws = new TempWorkspace().File("r/A.cs", "x");
        var fifo = Path.Combine(ws.Root, "r", "Pipe.cs");
        using (var p = Process.Start("mkfifo", fifo)) await p.WaitForExitAsync();
        var read = Task.Run(() => new SourceReader(ws.Root).Read(IndexWith(("ep:a", "r/Pipe.cs", 1)), "ep:a"));
        var snippet = await read.WaitAsync(TimeSpan.FromSeconds(5)); // TimeoutException = the read blocked on the FIFO
        Assert.Empty(snippet.Lines);
    }

    [Fact]
    public void NodesWithoutLocation_AndMissingFiles()
    {
        using var ws = new TempWorkspace();
        var ix = IndexWith(("ep:gone", "r/Gone.cs", 1));
        Assert.Equal("invalid_argument", Assert.Throws<QueryException>(() => new SourceReader(ws.Root).Read(ix, "repo:r")).Code);
        Assert.Equal("not_found", Assert.Throws<QueryException>(() => new SourceReader(ws.Root).Read(ix, "ep:gone")).Code);
        var ex = Assert.Throws<QueryException>(() => new SourceReader(ws.Root).Read(ix, "ep:nope"));
        Assert.Equal("not_found", ex.Code);
        // Verify error message never contains absolute workspace root
        Assert.DoesNotContain(ws.Root, ex.Message);
    }

    [Fact]
    public void ErrorMessagesNeverContainAbsolutePath()
    {
        using var ws = new TempWorkspace().File("r/A.cs", "x");
        var ex = Assert.Throws<QueryException>(() => new SourceReader(ws.Root).Read(IndexWith(("ep:a", "r/missing.cs", 1)), "ep:a"));
        // The workspace root should never appear in error messages
        Assert.DoesNotContain(ws.Root, ex.Message);
        Assert.DoesNotContain(Path.Combine(ws.Root, "r"), ex.Message);
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
