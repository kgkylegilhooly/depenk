using System.Diagnostics;
using Depenk.Analysis;
using Depenk.Core.Model;
using Depenk.Tests.TestUtil;
using Xunit.Abstractions;

namespace Depenk.Tests;

[Trait("Category", "Perf")]
public class PerfSmokeTests(ITestOutputHelper output)
{
    [Fact]
    public void FiftyRepos_FiveThousandEndpoints_WithinBudget()
    {
        using var ws = SyntheticWorkspace.Create();
        var orchestrator = new ScanOrchestrator(new ParseCache());

        var sw = Stopwatch.StartNew();
        var g = orchestrator.Scan(ws.Root);
        var full = sw.Elapsed;

        Assert.Equal(50, g.Repos.Count);
        Assert.Equal(5000, g.Endpoints.Count);
        Assert.Equal(5000, g.EdgesOf(EdgeKind.Targets).Count());
        Assert.Equal(150, g.EdgesOf(EdgeKind.DependsOn).Count());
        Assert.Equal(150, g.EdgesOf(EdgeKind.Invokes).Count()); // R0Client exists in every repo: namespace disambiguation

        ws.File("svc00/src/svc00.Api/Controllers/R0Controller.cs",
            "namespace svc00.Api;\n[Route(\"api/r0\")]\npublic class R0Controller : ControllerBase { [HttpGet(\"x\")] public int X() => 1; }\n");
        sw.Restart();
        orchestrator.Scan(ws.Root);
        var incremental = sw.Elapsed;

        output.WriteLine($"full={full.TotalSeconds:F1}s incremental={incremental.TotalSeconds:F2}s");
        Console.WriteLine($"PERF full={full.TotalSeconds:F1}s incremental={incremental.TotalSeconds:F2}s");
        Assert.True(full < TimeSpan.FromSeconds(60), $"full scan took {full}");
        Assert.True(incremental < TimeSpan.FromSeconds(2), $"incremental rescan took {incremental}");
    }
}
