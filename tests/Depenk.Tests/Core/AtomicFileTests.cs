using Depenk.Core;
using Depenk.Tests.TestUtil;

namespace Depenk.Tests.Core;

public class AtomicFileTests
{
    [Fact]
    public void ReplacesContent_AndLeavesNoTempFiles()
    {
        using var ws = new TempWorkspace();
        var path = Path.Combine(ws.Root, "sub", "graph.json");

        AtomicFile.WriteAllText(path, "first");
        AtomicFile.WriteAllText(path, "second");

        Assert.Equal("second", File.ReadAllText(path));
        Assert.Equal(["graph.json"], Directory.GetFiles(Path.GetDirectoryName(path)!).Select(Path.GetFileName));
    }

    [Fact]
    public void FailedWrite_KeepsThePreviousFile()
    {
        if (!OperatingSystem.IsWindows()) return; // elsewhere an open file doesn't block the rename
        using var ws = new TempWorkspace();
        var path = Path.Combine(ws.Root, "graph.json");
        AtomicFile.WriteAllText(path, "good");

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None)) // target can't be replaced
        {
            var ex = Record.Exception(() => AtomicFile.WriteAllText(path, "bad"));
            Assert.True(ex is IOException or UnauthorizedAccessException, ex?.ToString());
        }

        Assert.Equal("good", File.ReadAllText(path));
        Assert.Equal(["graph.json"], Directory.GetFiles(ws.Root).Select(Path.GetFileName));
    }
}
