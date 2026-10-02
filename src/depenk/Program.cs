using System.CommandLine;
using System.Diagnostics;
using Depenk.Analysis;
using Depenk.Core;
using Depenk.Core.Model;
using Depenk.Mcp;
using Depenk.Scanning;
using Depenk.Scanning.Config;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Exit codes: 0 success, 1 unexpected error / tool error, 2 invalid depenk.yml or --json, 3 workspace folder not found.
var workspaceOption = new Option<DirectoryInfo?>("--workspace",
    "Folder containing the repo clones (default: $DEPENK_WORKSPACE, else auto-detected from the current directory)");
var forceOption = new Option<bool>("--force", "Rescan even if nothing changed since the last scan");
var jsonOption = new Option<string?>("--json", "Tool arguments as a JSON object, e.g. '{\"endpoint\":\"GET /api/orders/{id}\"}'");
var toolArgument = new Argument<string>("tool", "Tool name (see `depenk query tools`)");

string ResolveWorkspace(DirectoryInfo? explicitDir) =>
    WorkspaceResolver.Resolve(explicitDir?.FullName, Directory.GetCurrentDirectory(), Environment.GetEnvironmentVariable);

bool WorkspaceExists(string ws)
{
    if (Directory.Exists(ws)) return true;
    Console.Error.WriteLine($"depenk: workspace folder not found: {ws}");
    return false;
}

var scan = new Command("scan", "Scan the workspace and write .depenk/graph.json") { workspaceOption, forceOption };
scan.SetHandler(ctx =>
{
    var ws = ResolveWorkspace(ctx.ParseResult.GetValueForOption(workspaceOption));
    if (!WorkspaceExists(ws)) { ctx.ExitCode = 3; return; }
    Console.Error.WriteLine($"Workspace: {ws}");
    try
    {
        if (!ctx.ParseResult.GetValueForOption(forceOption) && WorkspaceManifest.IsUpToDate(ws))
        {
            Console.WriteLine($"Graph is up to date ({Path.GetRelativePath(ws, ScanOrchestrator.GraphPath(ws))})");
            ctx.ExitCode = 0;
            return;
        }
        var sw = Stopwatch.StartNew();
        var manifest = WorkspaceManifest.Compute(ws); // before the scan: edits made during it stay detectable
        var graph = new ScanOrchestrator().Scan(ws);
        var path = ScanOrchestrator.GraphPath(ws);
        GraphJson.Save(graph, path);
        WorkspaceManifest.Save(ws, manifest);
        var warnings = graph.Diagnostics.Count(d => d.Severity == Severities.Warning);
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
    catch (Exception ex)
    {
        Console.Error.WriteLine($"depenk: scan failed: {ex.GetType().Name}: {ex.Message}");
        ctx.ExitCode = 1;
    }
});

var mcp = new Command("mcp", "Run the depenk MCP server over stdio (for Claude Code and other MCP clients)") { workspaceOption };
mcp.SetHandler(async ctx =>
{
    var ws = ResolveWorkspace(ctx.ParseResult.GetValueForOption(workspaceOption));
    if (!WorkspaceExists(ws)) { ctx.ExitCode = 3; return; }

    var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings()); // no appsettings.json from the cwd, no env logging config
    builder.Logging.ClearProviders();
    builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace); // stdout is the protocol channel
    builder.Logging.SetMinimumLevel(LogLevel.Warning);
    var store = new GraphStore(ws);
    builder.Services.AddDepenkMcpServer(store).WithStdioServerTransport();
    using var host = builder.Build();
    // first scan / staleness refresh off the handshake path; failures go to stderr only (stdout is the protocol channel)
    _ = Task.Run(() =>
    {
        try { store.StartBackgroundRefresh()?.Wait(); }
        catch (Exception ex) { Console.Error.WriteLine($"depenk: background scan failed: {ex.GetBaseException().Message}"); }
    });
    await host.RunAsync(ctx.GetCancellationToken());
    ctx.ExitCode = 0;
});

var query = new Command("query", "Call a depenk MCP tool from the command line and print its JSON result")
    { toolArgument, jsonOption, workspaceOption };
query.SetHandler(async ctx =>
{
    var ws = ResolveWorkspace(ctx.ParseResult.GetValueForOption(workspaceOption));
    if (!WorkspaceExists(ws)) { ctx.ExitCode = 3; return; }
    Console.Error.WriteLine($"Workspace: {ws}");
    ctx.ExitCode = await QueryRunner.RunAsync(ws, ctx.ParseResult.GetValueForArgument(toolArgument),
        ctx.ParseResult.GetValueForOption(jsonOption), Console.Out, Console.Error, ctx.GetCancellationToken());
});

var root = new RootCommand("depenk: cross-repo C# dependency explorer") { scan, mcp, query };
return await root.InvokeAsync(args);

public partial class Program { }
