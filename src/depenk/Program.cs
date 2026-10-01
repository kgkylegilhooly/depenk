using System.CommandLine;
using System.Diagnostics;
using Depenk.Analysis;
using Depenk.Core;
using Depenk.Scanning.Config;

var workspaceOption = new Option<DirectoryInfo>(
    "--workspace", () => new DirectoryInfo(Directory.GetCurrentDirectory()),
    "Folder containing the local repo clones (default: current directory)");

var scan = new Command("scan", "Scan the workspace and write .depenk/graph.json") { workspaceOption };
scan.SetHandler(ctx =>
{
    var ws = ctx.ParseResult.GetValueForOption(workspaceOption)!.FullName;
    try
    {
        var sw = Stopwatch.StartNew();
        var graph = new ScanOrchestrator().Scan(ws);
        var path = ScanOrchestrator.GraphPath(ws);
        GraphJson.Save(graph, path);
        var warnings = graph.Diagnostics.Count(d => d.Severity == "warning");
        Console.WriteLine(
            $"Scanned {graph.Repos.Count} repos, {graph.Projects.Count} projects: " +
            $"{graph.Endpoints.Count} endpoints, {graph.ClientMethods.Count} client methods, " +
            $"{graph.CallSites.Count} call sites, {graph.Models.Count} models " +
            $"in {sw.Elapsed.TotalSeconds:F1}s -> {Path.GetRelativePath(ws, path)} ({warnings} warnings)");
        ctx.ExitCode = 0;
    }
    catch (ConfigException ex)
    {
        Console.Error.WriteLine(ex.Message);
        ctx.ExitCode = 2;
    }
});

var root = new RootCommand("depenk: cross-repo C# dependency explorer") { scan };
return await root.InvokeAsync(args);
