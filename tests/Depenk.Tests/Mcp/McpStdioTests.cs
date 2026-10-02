using Depenk.Tests.TestUtil;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Depenk.Tests.Mcp;

public class McpStdioTests
{
    /// <summary>src/depenk/bin/&lt;Configuration&gt;/net9.0/depenk.dll, built first via the test project's build-order reference.</summary>
    private static string CliDll()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Depenk.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var config = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}") ? "Release" : "Debug";
        var dll = Path.Combine(dir!.FullName, "src", "depenk", "bin", config, "net9.0", "depenk.dll");
        Assert.True(File.Exists(dll), $"CLI not built at {dll}");
        return dll;
    }

    private static Task<McpClient> Connect(IList<string> args, Dictionary<string, string?>? env = null, string? workingDirectory = null) =>
        McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "depenk", Command = "dotnet", Arguments = [CliDll(), .. args], EnvironmentVariables = env,
            WorkingDirectory = workingDirectory, ShutdownTimeout = TimeSpan.FromSeconds(1),
        }));

    [Fact]
    public async Task StdioServer_ServesToolsAndResources()
    {
        using var ws = FixtureScanTests.CopyFixture();
        await using var client = await Connect(["mcp", "--workspace", ws.Root]);

        Assert.Equal(12, (await client.ListToolsAsync()).Count);
        var result = await client.CallToolAsync("list_repos", new Dictionary<string, object?>());
        Assert.Contains("\"name\":\"orders\"", result.Content.OfType<TextContentBlock>().Single().Text);
        var overview = await client.ReadResourceAsync("depenk://overview");
        Assert.StartsWith("# depenk", overview.Contents.OfType<TextResourceContents>().Single().Text);
    }

    [Fact]
    public async Task WorkspaceComesFromEnvironmentVariable()
    {
        using var ws = FixtureScanTests.CopyFixture();
        await using var client = await Connect(["mcp"], new() { ["DEPENK_WORKSPACE"] = ws.Root });
        var result = await client.CallToolAsync("get_repo", new Dictionary<string, object?> { ["repo"] = "billing" });
        Assert.NotEqual(true, result.IsError);
    }

    [Fact]
    public async Task MalformedAppSettingsInWorkingDirectory_DoesNotBreakTheServer()
    {
        using var ws = FixtureScanTests.CopyFixture();
        ws.File("appsettings.json", "{ not json");
        await using var client = await Connect(["mcp", "--workspace", ws.Root], workingDirectory: ws.Root);
        Assert.Equal(12, (await client.ListToolsAsync()).Count);
    }
}
