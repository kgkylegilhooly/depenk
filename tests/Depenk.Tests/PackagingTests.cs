using System.Text.Json;
using Depenk.Mcp;

namespace Depenk.Tests;

public class PackagingTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Depenk.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }

    private static JsonElement Json(string rel) => JsonDocument.Parse(File.ReadAllText(Path.Combine(Root(), rel))).RootElement;

    [Fact]
    public void PluginManifests_AreConsistent()
    {
        var plugin = Json(".claude-plugin/plugin.json");
        Assert.Equal("depenk", plugin.GetProperty("name").GetString());
        var listed = Json(".claude-plugin/marketplace.json").GetProperty("plugins")[0];
        Assert.Equal(("depenk", "./"), (listed.GetProperty("name").GetString(), listed.GetProperty("source").GetString()));

        var server = Json(".mcp.json").GetProperty("mcpServers").GetProperty("depenk");
        Assert.Equal("depenk", server.GetProperty("command").GetString());
        Assert.Equal("mcp", server.GetProperty("args")[0].GetString());
    }

    [Fact]
    public void Skill_HasFrontmatter_AndMentionsEveryTool()
    {
        var skill = File.ReadAllText(Path.Combine(Root(), "skills", "depenk", "SKILL.md"));
        var parts = skill.Split("---", 3);
        Assert.Equal("", parts[0].Trim());
        var front = parts[1];
        Assert.Contains("name: depenk", front);
        var description = front.Split('\n').Single(l => l.StartsWith("description:"))["description:".Length..].Trim();
        Assert.StartsWith("Use when", description);
        Assert.InRange(description.Length, 50, 1024);
        Assert.All(DepenkMcpServer.ToolNames, t => Assert.Contains(t, parts[2]));
        Assert.Contains("depenk://overview", parts[2]);
    }

    [Fact]
    public void PluginVersion_MatchesBuildVersion()
    {
        var props = File.ReadAllText(Path.Combine(Root(), "Directory.Build.props"));
        var version = Json(".claude-plugin/plugin.json").GetProperty("version").GetString();
        Assert.Contains($"<Version>{version}</Version>", props);
    }
}
