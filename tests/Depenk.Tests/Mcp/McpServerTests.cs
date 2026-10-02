using System.Text.Json;
using Depenk.Mcp;
using Depenk.Tests.TestUtil;
using ModelContextProtocol.Protocol;

namespace Depenk.Tests.Mcp;

public class McpServerTests
{
    private static readonly string[] ExpectedTools =
    [
        "find_endpoints", "find_model_usages", "get_diagnostics", "get_endpoint", "get_model", "get_repo",
        "get_source", "how_to_call", "impact_of_change", "list_repos", "rescan", "trace",
    ];

    [Fact]
    public async Task ExposesTwelveTools_WithReadOnlyAnnotations_AndOverviewResource()
    {
        using var ws = FixtureScanTests.CopyFixture();
        await using var h = await McpHarness.StartAsync(ws.Root);

        Assert.StartsWith("0.2.0", h.Client.ServerInfo.Version);
        var tools = await h.Client.ListToolsAsync();
        Assert.Equal(ExpectedTools, tools.Select(t => t.Name).Order(StringComparer.Ordinal));
        Assert.Equal(ExpectedTools, DepenkMcpServer.ToolNames.Order(StringComparer.Ordinal));
        Assert.All(tools.Where(t => t.Name != "rescan"), t => Assert.True(t.ProtocolTool.Annotations?.ReadOnlyHint));
        Assert.False(tools.Single(t => t.Name == "rescan").ProtocolTool.Annotations?.ReadOnlyHint);
        Assert.All(tools, t => Assert.False(string.IsNullOrWhiteSpace(t.Description)));

        var resources = await h.Client.ListResourcesAsync();
        Assert.Contains(resources, r => r.Uri == "depenk://overview");
        var overview = await h.Client.ReadResourceAsync("depenk://overview");
        Assert.StartsWith("# depenk workspace overview", overview.Contents.OfType<TextResourceContents>().Single().Text);
    }

    [Fact]
    public async Task EveryTool_ReturnsTheEnvelope()
    {
        using var ws = FixtureScanTests.CopyFixture();
        await using var h = await McpHarness.StartAsync(ws.Root);

        Assert.Equal(5, (await h.DataAsync("list_repos")).GetProperty("repos").GetArrayLength());
        Assert.Equal("orders", (await h.DataAsync("get_repo", new() { ["repo"] = "orders" })).GetProperty("name").GetString());
        Assert.Equal(4, (await h.DataAsync("find_endpoints", new() { ["query"] = "orders" })).GetProperty("items").GetArrayLength());
        var ep = await h.DataAsync("get_endpoint", new() { ["endpoint"] = "GET /api/orders/{id}" });
        Assert.Equal("ep:orders:GET:/api/orders/{id}", ep.GetProperty("id").GetString());
        Assert.Equal(4, (await h.DataAsync("get_model", new() { ["model"] = "OrderDto" })).GetProperty("fields").GetArrayLength());
        Assert.Equal(3, (await h.DataAsync("find_model_usages", new() { ["model"] = "OrderDto" })).GetProperty("repos").GetArrayLength());
        var src = await h.DataAsync("get_source", new() { ["nodeId"] = "ep:orders:GET:/api/orders/{id}", ["context"] = 1 });
        Assert.Contains("Get(Guid id)", string.Join("\n", src.GetProperty("lines").EnumerateArray().Select(l => l.GetString())));
        Assert.Equal(2, (await h.DataAsync("trace", new() { ["node"] = "billing", ["depth"] = 1 })).GetProperty("nodes").GetArrayLength());
        Assert.Equal(3, (await h.DataAsync("impact_of_change", new() { ["target"] = "OrderDto" })).GetProperty("callSites").GetArrayLength());
        Assert.Equal(1, (await h.DataAsync("get_diagnostics", new() { ["kind"] = "versionDrift" })).GetProperty("total").GetInt32());
        Assert.Equal("IOrdersClient", (await h.DataAsync("how_to_call", new() { ["endpoint"] = "GET /api/orders/{id}" }))
            .GetProperty("options")[0].GetProperty("type").GetString());
        Assert.Equal(5, (await h.DataAsync("rescan")).GetProperty("repos").GetInt32());
    }

    [Fact]
    public async Task UnknownIds_AreToolErrors_WithStructuredBody()
    {
        using var ws = FixtureScanTests.CopyFixture();
        await using var h = await McpHarness.StartAsync(ws.Root);

        var (isError, text) = await h.CallAsync("get_endpoint", new() { ["endpoint"] = "ep:orders:GET:/api/order/{id}" });

        Assert.True(isError);
        var json = text[text.IndexOf('{')..];
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("not_found", doc.RootElement.GetProperty("code").GetString());
        Assert.Equal("ep:orders:GET:/api/orders/{id}", doc.RootElement.GetProperty("suggestions")[0].GetString());
    }

    [Fact]
    public async Task LargeResults_AreFlaggedTruncated()
    {
        using var ws = FixtureScanTests.CopyFixture();
        await using var h = await McpHarness.StartAsync(ws.Root);

        var (_, text) = await h.CallAsync("find_endpoints", new() { ["limit"] = 2 });
        using var doc = JsonDocument.Parse(text);
        Assert.True(doc.RootElement.GetProperty("truncated").GetBoolean());
        Assert.Equal("Narrow with query, repo or verb.", doc.RootElement.GetProperty("data").GetProperty("hint").GetString());
    }

    [Fact]
    public async Task InvalidConfig_IsToolError_WithInvalidArgumentCode_AndNoStackTrace()
    {
        using var ws = new TempWorkspace();
        ws.File("depenk.yml", "repos: [unterminated\n  : : :\n\t- bad");
        await using var h = await McpHarness.StartAsync(ws.Root);

        var (isError, text) = await h.CallAsync("list_repos");

        Assert.True(isError);
        using var doc = JsonDocument.Parse(text[text.IndexOf('{')..]);
        Assert.Equal("invalid_argument", doc.RootElement.GetProperty("code").GetString());
        Assert.Contains("depenk scan", doc.RootElement.GetProperty("hint").GetString());
        Assert.DoesNotContain("   at ", text);
    }
}
