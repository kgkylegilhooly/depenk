using System.Text.Json;
using Depenk.Core;
using Depenk.Query;

namespace Depenk.Tests;

public class QueryConformanceTests
{
    private static string CasesDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Depenk.sln"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "tests", "query-cases");
    }

    public static IEnumerable<object[]> Cases() =>
        Directory.EnumerateFiles(CasesDir(), "*.json").Order(StringComparer.Ordinal).Select(f => new object[] { Path.GetFileName(f) });

    [Theory]
    [MemberData(nameof(Cases))]
    public void Case(string file)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(CasesDir(), file)));
        var c = doc.RootElement;
        var graph = GraphJson.Deserialize(File.ReadAllText(Path.GetFullPath(Path.Combine(CasesDir(), c.GetProperty("graph").GetString()!))));
        var index = new GraphIndex(graph);
        var args = c.GetProperty("args");
        string Arg(string name) => args.GetProperty(name).GetString()!;

        IEnumerable<string> actual = c.GetProperty("op").GetString() switch
        {
            "neighbors" => (Arg("direction") == "down"
                    ? index.DependenciesOf(Arg("id")).Select(h => h.To)
                    : index.DependentsOf(Arg("id")).Select(h => h.From))
                .Distinct().Order(StringComparer.Ordinal),
            "trace" => new QueryService(index)
                .Trace(Arg("id"), Arg("direction"), args.GetProperty("depth").GetInt32(), QueryService.MaxLimit)
                .Nodes.Select(n => $"{n.Id}@{n.Depth}"),
            "search" => index.Suggest(Arg("query"), args.GetProperty("limit").GetInt32()),
            var op => throw new InvalidOperationException($"unknown op {op}"),
        };

        Assert.Equal(c.GetProperty("expected").EnumerateArray().Select(e => e.GetString()!), actual);
    }
}
