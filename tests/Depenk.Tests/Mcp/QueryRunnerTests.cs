using System.Text.Json;
using Depenk.Mcp;

namespace Depenk.Tests.Mcp;

public class QueryRunnerTests
{
    private static async Task<(int Code, string Out, string Err)> Run(string ws, string tool, string? json = null)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var code = await QueryRunner.RunAsync(ws, tool, json, o, e);
        return (code, o.ToString(), e.ToString());
    }

    [Fact]
    public async Task CallsATool_AndPrintsTheEnvelope()
    {
        using var ws = FixtureScanTests.CopyFixture();
        var (code, stdout, _) = await Run(ws.Root, "find_endpoints", """{"query":"orders"}""");
        Assert.Equal(0, code);
        using var doc = JsonDocument.Parse(stdout);
        Assert.Equal(4, doc.RootElement.GetProperty("data").GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task ToolErrors_GoToStderr_WithExitCode1()
    {
        using var ws = FixtureScanTests.CopyFixture();
        var (code, stdout, stderr) = await Run(ws.Root, "get_endpoint", """{"endpoint":"GET /api/nope"}""");
        Assert.Equal(1, code);
        Assert.Equal("", stdout.Trim());
        Assert.Contains("not_found", stderr);
    }

    [Fact]
    public async Task UnknownTool_And_BadJson()
    {
        using var ws = FixtureScanTests.CopyFixture();
        Assert.Equal(1, (await Run(ws.Root, "no_such_tool")).Code);
        var bad = await Run(ws.Root, "list_repos", "[1,2]");
        Assert.Equal(2, bad.Code);
        Assert.Contains("--json", bad.Err);
    }

    [Fact]
    public async Task ToolsListsAllTwelve()
    {
        using var ws = FixtureScanTests.CopyFixture();
        var (code, stdout, _) = await Run(ws.Root, "tools");
        Assert.Equal(0, code);
        Assert.Equal(12, stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }
}
