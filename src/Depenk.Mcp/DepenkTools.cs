using System.ComponentModel;
using Depenk.Query;
using Depenk.Scanning.Config;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Depenk.Mcp;

[McpServerToolType]
public sealed class DepenkTools(GraphStore store)
{
    private const string ScanHint = "Fix depenk.yml or run `depenk scan` to see the full error.";

    [McpServerTool(Name = "list_repos", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("List every repo in the workspace with project/endpoint counts and the repo→repo dependency links created by client NuGet packages.")]
    public string ListRepos() =>
        Run(s => s.Query.ListRepos(), r => $"{r.Repos.Count} repos, {r.Links.Count} cross-repo links");

    [McpServerTool(Name = "get_repo", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Details for one repo: projects, packages it publishes and consumes, repos it depends on and repos that depend on it.")]
    public string GetRepo([Description("Repo name (e.g. \"orders\") or id (\"repo:orders\").")] string repo) =>
        Run(s => s.Query.GetRepo(repo),
            r => $"{r.Name}: {r.Projects.Count} projects, publishes {r.Publishes.Count} package(s), {r.DependsOn.Count} dependencies, {r.DependedOnBy.Count} dependents");

    [McpServerTool(Name = "find_endpoints", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Search HTTP endpoints by route or handler text, optionally filtered by repo and verb. Shows how many client methods call each.")]
    public string FindEndpoints(
        [Description("Text to find in the route or handler, e.g. \"orders\" or \"OrdersController\". Omit to list all.")] string? query = null,
        [Description("Only endpoints in this repo (name or id).")] string? repo = null,
        [Description("Only this HTTP verb, e.g. GET.")] string? verb = null,
        [Description("Max results (default 50, max 500).")] int? limit = null) =>
        Run(s => s.Query.FindEndpoints(query, repo, verb, limit), r => $"{r.Items.Count} of {r.Total} endpoints");

    [McpServerTool(Name = "get_endpoint", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Full contract for one endpoint: parameters, request/response models, handler location, the client methods that target it and the call sites that use them.")]
    public string GetEndpoint([Description("Endpoint id (\"ep:orders:GET:/api/orders/{id}\") or \"VERB /route\" (\"GET /api/orders/{id}\").")] string endpoint) =>
        Run(s => s.Query.GetEndpoint(endpoint),
            e => $"{e.Verb} {e.Route} in {e.Repo}: {e.ClientMethods.Count} client method(s), {e.Callers.Count} call site(s)");

    [McpServerTool(Name = "get_model", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Field tree of a request/response model (DTO), expanding nested model types; marks fields whose type lives in another repo.")]
    public string GetModel(
        [Description("Model id, full name (\"Acme.Orders.Client.OrderDto\") or simple name (\"OrderDto\").")] string model,
        [Description("Levels of nested models to expand (0-6, default 2).")] int depth = 2) =>
        Run(s => s.Query.GetModel(model, depth), m => $"{m.FullName} ({m.Kind.ToString().ToLowerInvariant()}) with {m.Fields.Count} fields");

    [McpServerTool(Name = "find_model_usages", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Every endpoint that accepts or returns a model (directly or nested inside another model) and every repo that consumes those endpoints.")]
    public string FindModelUsages([Description("Model id, full name or simple name.")] string model) =>
        Run(s => s.Query.FindModelUsages(model), u => $"{u.Endpoints.Count} endpoint usage(s) across {u.Repos.Count} repo(s)");

    [McpServerTool(Name = "get_source", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Source snippet (with line numbers) for an endpoint, client method, call site, model or project — works across repos.")]
    public string GetSource(
        [Description("Node id from another depenk result.")] string nodeId,
        [Description("Lines of context above and below (0-50, default 10).")] int context = 10) =>
        Run(s => new SourceReader(store.Workspace).Read(s.Index, nodeId, context), x => $"{x.Path}:{x.Line}");

    [McpServerTool(Name = "trace", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Walk the dependency graph from any node: \"down\" = what it depends on, \"up\" = what depends on it, \"both\".")]
    public string Trace(
        [Description("Any node id, repo name, package id, \"VERB /route\" or model name.")] string node,
        [Description("down, up or both (default down).")] string direction = "down",
        [Description("Max hops (1-10, default 3).")] int depth = 3,
        [Description("Max nodes returned (default 50, max 500).")] int? limit = null) =>
        Run(s => s.Query.Trace(node, direction, depth, limit), t => $"{t.Nodes.Count} node(s) {t.Direction} from {t.Root}");

    [McpServerTool(Name = "impact_of_change", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("What breaks if this changes: affected repos, projects, endpoints, client methods and call sites (with confidence) for a package, endpoint, model or single model field. Call before editing a controller, DTO or client package.")]
    public string ImpactOfChange(
        [Description("Package id, endpoint (\"GET /api/orders/{id}\"), model name, or \"Model.Field\" (e.g. \"OrderDto.Lines\").")] string target,
        [Description("Max items per list (default 50, max 500).")] int? limit = null) =>
        Run(s => s.Query.ImpactOfChange(target, limit),
            r => $"Changing {r.Target}{(r.Field is null ? "" : "." + r.Field)} affects {r.Totals.Repos} repo(s), {r.Totals.Endpoints} endpoint(s), {r.Totals.CallSites} call site(s)");

    [McpServerTool(Name = "get_diagnostics", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Findings from the scan: versionDrift, cycle, unused*, ambiguous*, unresolved*, parseError … optionally filtered.")]
    public string GetDiagnostics(
        [Description("Diagnostic kind, e.g. versionDrift.")] string? kind = null,
        [Description("Only diagnostics touching this repo (name or id).")] string? repo = null,
        [Description("info, warning or error.")] string? severity = null,
        [Description("Max results (default 50, max 500).")] int? limit = null) =>
        Run(s => s.Query.GetDiagnostics(kind, repo, severity, limit), d => $"{d.Items.Count} of {d.Total} diagnostics");

    [McpServerTool(Name = "how_to_call", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("How to call an endpoint from another service: which client package and version to reference, the client interface/method signature, and the request/response models.")]
    public string HowToCall([Description("Endpoint id or \"VERB /route\".")] string endpoint) =>
        Run(s => s.Query.HowToCall(endpoint), h => $"{h.Options.Count} way(s) to call {h.EndpointId}");

    [McpServerTool(Name = "rescan", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Rescan the workspace now (incremental: only changed files are re-parsed) and refresh .depenk/graph.json.")]
    public string Rescan()
    {
        try
        {
            var g = store.Rescan().Graph;
            var counts = new RescanCounts(g.Repos.Count, g.Projects.Count, g.Endpoints.Count, g.ClientMethods.Count,
                g.CallSites.Count, g.Models.Count, g.Diagnostics.Count);
            return ToolJson.Envelope($"Rescanned: {counts.Repos} repos, {counts.Endpoints} endpoints, {counts.Diagnostics} diagnostics",
                false, counts);
        }
        catch (Exception e) when (e is not McpException)
        {
            throw Fail(e);
        }
    }

    private string Run<T>(Func<GraphSnapshot, T> query, Func<T, string> summary)
    {
        try
        {
            var snapshot = store.Current();
            var data = query(snapshot);
            return ToolJson.Envelope(summary(data), snapshot.Stale, data);
        }
        catch (Exception e) when (e is not McpException)
        {
            throw Fail(e);
        }
    }

    /// <summary>Maps any failure to the structured error body; never exposes stack traces.</summary>
    private static McpException Fail(Exception e) => e switch
    {
        QueryException q => new McpException(ToolJson.Error(q)),
        ConfigException c => new McpException(ToolJson.Error(QueryException.InvalidArgument, c.Message, ScanHint)),
        _ => new McpException(ToolJson.Error("scan_failed", e.Message, ScanHint)),
    };

    private sealed record RescanCounts(int Repos, int Projects, int Endpoints, int ClientMethods, int CallSites, int Models,
        int Diagnostics);
}
