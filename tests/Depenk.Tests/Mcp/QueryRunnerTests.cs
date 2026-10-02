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

    [Fact]
    public async Task JsonNull_IsExit2()
    {
        using var ws = FixtureScanTests.CopyFixture();
        var r = await Run(ws.Root, "list_repos", "null");
        Assert.Equal(2, r.Code);
        Assert.Contains("--json", r.Err);
    }

    [Fact]
    public async Task StaleCache_IsRescannedBeforeAnswering()
    {
        using var ws = FixtureScanTests.CopyFixture();
        new GraphStore(ws.Root).Current();
        ws.File("orders/src/Orders.Api/CancelController.cs", """
            using Microsoft.AspNetCore.Mvc;
            namespace Acme.Orders.Api;
            [Route("api/orders")]
            public class CancelController : ControllerBase { [HttpPost("{id}/cancel")] public Task Cancel(Guid id) => Task.CompletedTask; }
            """);
        var (code, stdout, _) = await Run(ws.Root, "find_endpoints", """{"query":"cancel"}""");
        Assert.Equal(0, code);
        using var doc = JsonDocument.Parse(stdout);
        Assert.False(doc.RootElement.GetProperty("stale").GetBoolean());
        Assert.Contains("cancel", stdout);
        Assert.True(doc.RootElement.GetProperty("data").GetProperty("items").GetArrayLength() >= 1);
    }
}
