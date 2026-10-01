# depenk Plan 2 — Query Layer, MCP Server & Claude Code Skill Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the graph from Plan 1 queryable. This plan adds:
- **Query layer:** a pure C# query layer over `DepGraph`
- **MCP server:** a stdio MCP server (`depenk mcp`) whose tools answer impact and contract questions for AI agents
- **Query command:** a `depenk query` CLI for agents without MCP
- **Claude Code integration:** a skill, plugin and marketplace manifest
- **Config schema:** a JSON Schema for `depenk.yml`
- **Conformance cases:** shared query test cases that the Plan 3 UI will reuse

**Architecture:**
- `Depenk.Query` (references Core only) holds `GraphIndex` (lookups, normalized dependency adjacency, fuzzy suggestions), `QueryService` (every query, returning result records) and `SourceReader` (sandboxed snippets).
- `Depenk.Mcp` (references Query + Analysis + the MCP SDK) holds:
  - `GraphStore`: loads `.depenk/graph.json` or scans, tracks staleness, and refreshes in the background
  - the `[McpServerToolType]` tools, which wrap results in a JSON envelope
  - the `depenk://overview` resource
- The CLI adds `mcp` (stdio host) and `query` (in-memory MCP client → same tool code).

**Tech Stack:** .NET 9, C# 13, ModelContextProtocol 2.2.0, Microsoft.Extensions.Hosting 10.0.12, System.Text.Json, xUnit 2.9.3, System.CommandLine 2.0.0-beta4.22272.1 (already used).

**Spec:** `docs/superpowers/specs/2026-09-30-depenk-design.md` — this plan covers §5 (query layer and conformance suite), §6 (MCP tools: every tool **except** `export_diagram` and `open_diagram`, which need the UI → Plan 3, and `compare_snapshots` and `check_contract_changes`, which need history → Plan 4), §7 (CLI query, skill, plugin, `.mcp.json`) and §2 (JSON Schema for `depenk.yml`).
- Deferred from §7: NuGet `McpServer` package type / `dnx`, because both need the .NET 10 SDK and this machine has 9.0.3xx. The container image is deferred too.
- The spec's `rescan(repos?)` is implemented as `rescan()`. Scans are already incremental, so per-repo filtering would add API with no benefit.

## Global Constraints

- All projects target `net9.0`. `TreatWarningsAsErrors` applies (from `Directory.Build.props`). Package versions are exactly `ModelContextProtocol` **2.2.0** and `Microsoft.Extensions.Hosting` **10.0.12**.
- **stdout is the MCP protocol channel** in `depenk mcp`. Nothing else may write to stdout in that mode; all logging goes to stderr.
- Every tool returns one text content block containing JSON in this exact envelope (camelCase, `GraphJson.Options`):
  `{"summary": "<one line>", "stale": <bool>, "truncated": <bool>, "data": <result>}`
- Tool errors are thrown as `McpException` whose message is JSON `{"code": "...", "message": "...", "hint": "...", "suggestions": [...]}`. Codes: `not_found`, `ambiguous`, `invalid_argument`, `outside_workspace`.
- List results are capped (default `limit` 50, maximum 500) and report `total` + `truncated`.
- All tools are read-only (`ReadOnly = true, Destructive = false, OpenWorld = false`) except `rescan` (`ReadOnly = false, Destructive = false`).
- `get_source` must never read a file outside the workspace. Paths are resolved and checked against the workspace root before any read.
- Node IDs and graph JSON format are unchanged from Plan 1 (`schemaVersion: 1`).
- Workspace resolution for `mcp`/`query`: `--workspace` if given, else environment variable `DEPENK_WORKSPACE`, else the current directory.
- Commit messages end with a blank line then `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Review Focus

1. **First call with no `.depenk/graph.json` yet:** the tool must scan synchronously and answer — it must not hang, crash or return an empty graph (tested in Task 5).
2. **A tampered or odd graph location:** for example `../../etc/passwd`, an absolute path, or a path through a junction. `get_source` must return `outside_workspace` without reading anything (tested in Task 4).
3. **A typo'd ID or name:** the result must be `not_found` with useful `suggestions`, not a generic error, and the same for MCP and `depenk query` (tested in Tasks 1, 6, 7).
4. **Concurrent tool calls while a background refresh swaps the graph:** these must not throw or mix two graphs. `stale` is `true` while refreshing and `false` after (tested in Task 5).
5. **Huge result sets:** for example `find_endpoints("")` on 5,000 endpoints or a deep `trace`. The result must be capped with `truncated: true` and a narrowing hint, and must not flood the agent's context (tested in Tasks 2, 3).

---

## File Structure

```
src/
├─ Depenk.Query/                       # pure: references Depenk.Core only
│  ├─ NodeKind.cs                      # NodeKind enum, NodeRef, Hop records
│  ├─ QueryException.cs                # code/message/hint/suggestions
│  ├─ Fuzzy.cs                         # Levenshtein + ranking for suggestions/search
│  ├─ GraphIndex.cs                    # id lookups, normalized dependency adjacency, resolvers
│  ├─ Results.cs                       # all query result records
│  ├─ QueryService.Browse.cs           # list_repos, get_repo, find_endpoints, get_endpoint, get_diagnostics
│  ├─ QueryService.Analyze.cs          # get_model, find_model_usages, trace, impact_of_change, how_to_call
│  ├─ OverviewWriter.cs                # depenk://overview markdown
│  └─ SourceReader.cs                  # sandboxed get_source
├─ Depenk.Scanning/WorkspaceResolver.cs # --workspace / $DEPENK_WORKSPACE / auto-detect
├─ Depenk.Mcp/                         # references Query, Analysis, ModelContextProtocol
│  ├─ GraphStore.cs                    # load/scan/stale/background refresh/rescan
│  ├─ ToolJson.cs                      # envelope + error JSON
│  ├─ DepenkTools.cs                   # [McpServerToolType] — 12 tools
│  ├─ DepenkResources.cs               # depenk://overview
│  ├─ DepenkMcpServer.cs               # DI registration + server instructions
│  └─ QueryRunner.cs                   # `depenk query`: in-memory MCP client → one tool call
└─ depenk/
   └─ Program.cs                       # adds `mcp` and `query` commands
schemas/depenk.schema.json             # JSON Schema for depenk.yml
skills/depenk/SKILL.md                 # Claude Code skill
.claude-plugin/plugin.json             # Claude Code plugin manifest
.claude-plugin/marketplace.json        # lets `/plugin marketplace add kgkylegilhooly/depenk` work
.mcp.json                              # plugin MCP server config
tests/
├─ query-cases/*.json                  # shared query conformance cases (C# now, TS in Plan 3)
└─ Depenk.Tests/
   ├─ TestUtil/FixtureGraph.cs         # scans the fixture once, shares GraphIndex/QueryService
   ├─ TestUtil/McpHarness.cs           # in-memory MCP server + client for tool tests
   ├─ Query/*.cs, Mcp/*.cs, Scanning/WorkspaceResolverTests.cs, Scanning/ConfigSchemaTests.cs
   ├─ QueryConformanceTests.cs
   └─ PackagingTests.cs
```

---

### Task 1: Depenk.Query project + GraphIndex (lookups, dependency adjacency, resolvers, suggestions)

**Files:**
- Create: `src/Depenk.Query/Depenk.Query.csproj`, `src/Depenk.Query/NodeKind.cs`, `src/Depenk.Query/QueryException.cs`, `src/Depenk.Query/Fuzzy.cs`, `src/Depenk.Query/GraphIndex.cs`
- Create: `tests/Depenk.Tests/TestUtil/FixtureGraph.cs`
- Test: `tests/Depenk.Tests/Query/GraphIndexTests.cs`

**Interfaces:**
- Consumes: `Depenk.Core.Model.*` (DepGraph, node records, Edge, EdgeKind, Confidence); in tests `FixtureScanTests.CopyFixture()` and `ScanOrchestrator`.
- Produces:
  - `enum NodeKind { Repo, Project, Package, Endpoint, ClientMethod, CallSite, Model }`
  - `sealed record NodeRef(string Id, NodeKind Kind, string Label, string? Repo)`
  - `sealed record Hop(string From, string To, EdgeKind Kind, Confidence Confidence, Edge Edge)`: **From is always the dependent and To the dependency**. Every edge kind keeps its direction except `produces` (project→package), which is flipped to package→project, so "package depends on its producer".
  - `sealed class QueryException(string code, string message, string? hint = null, IReadOnlyList<string>? suggestions = null) : Exception`, exposing `Code`, `Hint` and `Suggestions`
  - `static class Fuzzy { int Distance(string a, string b); }`
  - `sealed class GraphIndex(DepGraph graph)` with:
    - typed dictionaries: `Repos`, `Projects`, `Packages`, `Endpoints`, `ClientMethods`, `CallSites`, `Models`
    - lookups: `IReadOnlyCollection<NodeRef> Nodes`, `bool TryGet(string id, out NodeRef node)`, `NodeRef Get(string id)` (throws `not_found` with suggestions)
    - adjacency: `IReadOnlyList<Hop> DependenciesOf(string id)`, `IReadOnlyList<Hop> DependentsOf(string id)`
    - resolvers: `string ResolveRepo(string q)`, `string ResolvePackage(string q)`, `string ResolveEndpoint(string q)`, `string ResolveModel(string q)`, `string ResolveAny(string q)`
    - search: `IReadOnlyList<string> Suggest(string q, int max = 5)`
  - Test helper: `FixtureGraph.Value : DepGraph` (the fixture scanned once) and `FixtureGraph.Index : GraphIndex`

**Resolver rules:**
- **All resolvers:** an exact node ID always wins.
- **`ResolveRepo`:** repo name, case-insensitive.
- **`ResolvePackage`:** PackageId, case-insensitive.
- **`ResolveEndpoint`:** `"VERB /route"`.
  - The verb is case-insensitive. An exact match on `Route` (OrdinalIgnoreCase) is tried first, then a match on the normalized form (`{…}` becomes `{}`, lowercase, trimmed `/`) against `EndpointNode.NormalizedRoute`.
  - More than one match throws `ambiguous`, with the candidate IDs as suggestions.
- **`ResolveModel`:** exact `FullName`, then the simple name (the part after the last `.`, ignoring a `` `N `` arity suffix). More than one match throws `ambiguous`.
- **`ResolveAny`:** tries, in order, ID, repo name, PackageId, endpoint form (only if the query contains a space), and model. Otherwise it throws `not_found`.
- **`Suggest`:** ranks every node by:
  1. whether the lowercased query is a substring of its lowercased ID or label (matches first)
  2. `min(Distance(id, q), Distance(label, q))`, ignoring case
  3. ordinal ID

  It returns the top `max` IDs.
- **Labels:**

  | Node | Label |
  |---|---|
  | repo | name |
  | project | name |
  | package | PackageId |
  | endpoint | `"{Verb} {Route}"` |
  | client method | `"{TypeName}.{MethodName}"` |
  | call site | ContainingMember |
  | model | FullName |

- **`NodeRef.Repo`:**
  - a node's own `Repo` field
  - a package's first producer project's repo, or null when there is no producer
  - an opaque model's empty repo becomes null

- [ ] **Step 1: Create the project**

```bash
cd /c/code/repos/depenk
dotnet new classlib -n Depenk.Query -o src/Depenk.Query
rm src/Depenk.Query/Class1.cs
dotnet sln add src/Depenk.Query
dotnet add src/Depenk.Query reference src/Depenk.Core
dotnet add tests/Depenk.Tests reference src/Depenk.Query
```

Remove the generated `<TargetFramework>`, `<Nullable>` and `<ImplicitUsings>` lines from `src/Depenk.Query/Depenk.Query.csproj`, because `Directory.Build.props` owns them. Leave the empty `<PropertyGroup>` out entirely.

- [ ] **Step 2: Write the test helper and failing tests**

`tests/Depenk.Tests/TestUtil/FixtureGraph.cs`:

```csharp
using Depenk.Analysis;
using Depenk.Core.Model;
using Depenk.Query;

namespace Depenk.Tests.TestUtil;

/// <summary>The Plan 1 fixture workspace, scanned once per test run and shared read-only.</summary>
public static class FixtureGraph
{
    private static readonly Lazy<DepGraph> Graph = new(() =>
    {
        using var ws = FixtureScanTests.CopyFixture();
        return new ScanOrchestrator().Scan(ws.Root);
    });

    public static DepGraph Value => Graph.Value;
    public static GraphIndex Index => new(Graph.Value);
}
```

`tests/Depenk.Tests/Query/GraphIndexTests.cs`:

```csharp
using Depenk.Core.Model;
using Depenk.Query;
using Depenk.Tests.TestUtil;

namespace Depenk.Tests.Query;

public class GraphIndexTests
{
    private static readonly GraphIndex Ix = FixtureGraph.Index;

    [Fact]
    public void IndexesEveryNode()
    {
        var g = FixtureGraph.Value;
        var expected = g.Repos.Count + g.Projects.Count + g.Packages.Count + g.Endpoints.Count
                       + g.ClientMethods.Count + g.CallSites.Count + g.Models.Count;
        Assert.Equal(expected, Ix.Nodes.Count);
        Assert.Equal(NodeKind.Endpoint, Ix.Get("ep:orders:GET:/api/orders/{id}").Kind);
        Assert.Equal("GET /api/orders/{id}", Ix.Get("ep:orders:GET:/api/orders/{id}").Label);
        Assert.Equal("orders", Ix.Get("pkg:Orders.Client").Repo);
        Assert.Null(Ix.Get("pkg:Refit").Repo);
    }

    [Fact]
    public void Adjacency_IsNormalizedToDependentToDependency()
    {
        Assert.Equal(["repo:customers", "repo:orders"],
            Ix.DependenciesOf("repo:billing").Where(h => h.Kind == EdgeKind.DependsOn).Select(h => h.To).Order());

        var pkgDependents = Ix.DependentsOf("pkg:Orders.Client").Select(h => h.From).ToList();
        Assert.Contains("proj:billing/Billing.Api", pkgDependents);
        Assert.Contains("proj:gateway/Gateway.Api", pkgDependents);

        // produces is flipped: the package depends on the project that produces it
        var produced = Ix.DependenciesOf("pkg:Orders.Client").Single(h => h.Kind == EdgeKind.Produces);
        Assert.Equal("proj:orders/Orders.Client", produced.To);
        Assert.Equal(EdgeKind.Produces, produced.Edge.Kind);
    }

    [Fact]
    public void ResolvesEndpoints_ByVerbAndRoute_CaseInsensitive()
    {
        Assert.Equal("ep:orders:GET:/api/orders/{id}", Ix.ResolveEndpoint("get /API/orders/{id}"));
        Assert.Equal("ep:orders:GET:/api/orders/{id}", Ix.ResolveEndpoint("ep:orders:GET:/api/orders/{id}"));
        Assert.Equal("ep:orders:GET:/api/orders/{id}", Ix.ResolveEndpoint("GET /api/orders/{orderId}")); // normalized
    }

    [Fact]
    public void AmbiguousEndpoint_ListsCandidates()
    {
        var ex = Assert.Throws<QueryException>(() => Ix.ResolveEndpoint("GET /api/customers/{x}"));
        Assert.Equal("ambiguous", ex.Code);
        Assert.Equal(["ep:customers:GET:/api/customers/{id:guid}", "ep:customers:GET:/api/customers/{slug}"],
            ex.Suggestions!.Order());
    }

    [Fact]
    public void ResolvesModels_ByIdFullNameOrSimpleName()
    {
        const string id = "model:Orders.Client:Acme.Orders.Client.OrderDto";
        Assert.Equal(id, Ix.ResolveModel(id));
        Assert.Equal(id, Ix.ResolveModel("Acme.Orders.Client.OrderDto"));
        Assert.Equal(id, Ix.ResolveModel("OrderDto"));
    }

    [Fact]
    public void UnknownId_IsNotFound_WithSuggestions()
    {
        var ex = Assert.Throws<QueryException>(() => Ix.Get("ep:orders:GET:/api/order/{id}"));
        Assert.Equal("not_found", ex.Code);
        Assert.Equal("ep:orders:GET:/api/orders/{id}", ex.Suggestions![0]);
        Assert.False(string.IsNullOrEmpty(ex.Hint));
    }

    [Fact]
    public void ResolveAny_PrefersRepoName_ThenPackage_ThenModel()
    {
        Assert.Equal("repo:orders", Ix.ResolveAny("orders"));
        Assert.Equal("pkg:Orders.Client", Ix.ResolveAny("orders.client"));
        Assert.Equal("model:Shared.Kernel:Acme.Shared.Money", Ix.ResolveAny("Money"));
        Assert.Equal("not_found", Assert.Throws<QueryException>(() => Ix.ResolveAny("zzz-nothing")).Code);
    }

    [Theory]
    [InlineData("kitten", "sitting", 3)]
    [InlineData("", "abc", 3)]
    [InlineData("same", "same", 0)]
    public void Levenshtein(string a, string b, int d) => Assert.Equal(d, Fuzzy.Distance(a, b));
}
```

- [ ] **Step 3: Run to verify failure**

Run: `dotnet test tests/Depenk.Tests --filter GraphIndexTests`
Expected: the build FAILS with `The type or namespace name 'GraphIndex' could not be found`.

- [ ] **Step 4: Implement**

`src/Depenk.Query/NodeKind.cs`:

```csharp
using Depenk.Core.Model;

namespace Depenk.Query;

public enum NodeKind { Repo, Project, Package, Endpoint, ClientMethod, CallSite, Model }

public sealed record NodeRef(string Id, NodeKind Kind, string Label, string? Repo);

/// <summary>A dependency hop: <see cref="From"/> depends on <see cref="To"/>. <see cref="Edge"/> is the original edge.</summary>
public sealed record Hop(string From, string To, EdgeKind Kind, Confidence Confidence, Edge Edge);
```

`src/Depenk.Query/QueryException.cs`:

```csharp
namespace Depenk.Query;

public sealed class QueryException(string code, string message, string? hint = null, IReadOnlyList<string>? suggestions = null)
    : Exception(message)
{
    public const string NotFound = "not_found", Ambiguous = "ambiguous", InvalidArgument = "invalid_argument",
        OutsideWorkspace = "outside_workspace";

    public string Code { get; } = code;
    public string? Hint { get; } = hint;
    public IReadOnlyList<string>? Suggestions { get; } = suggestions;
}
```

`src/Depenk.Query/Fuzzy.cs`:

```csharp
namespace Depenk.Query;

public static class Fuzzy
{
    public static int Distance(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }
}
```

`src/Depenk.Query/GraphIndex.cs`:

```csharp
using System.Text.RegularExpressions;
using Depenk.Core.Model;

namespace Depenk.Query;

public sealed partial class GraphIndex
{
    private readonly Dictionary<string, NodeRef> _nodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Hop>> _deps = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Hop>> _dependents = new(StringComparer.Ordinal);

    public GraphIndex(DepGraph graph)
    {
        Graph = graph;
        Repos = ById(graph.Repos, r => r.Id);
        Projects = ById(graph.Projects, p => p.Id);
        Packages = ById(graph.Packages, p => p.Id);
        Endpoints = ById(graph.Endpoints, e => e.Id);
        ClientMethods = ById(graph.ClientMethods, c => c.Id);
        CallSites = ById(graph.CallSites, c => c.Id);
        Models = ById(graph.Models, m => m.Id);

        foreach (var r in Repos.Values) Add(new NodeRef(r.Id, NodeKind.Repo, r.Name, r.Name));
        foreach (var p in Projects.Values) Add(new NodeRef(p.Id, NodeKind.Project, p.Name, p.Repo));
        foreach (var p in Packages.Values)
            Add(new NodeRef(p.Id, NodeKind.Package, p.PackageId,
                p.ProducerProjectIds.Select(id => Projects.GetValueOrDefault(id)?.Repo).FirstOrDefault(r => r is not null)));
        foreach (var e in Endpoints.Values) Add(new NodeRef(e.Id, NodeKind.Endpoint, $"{e.Verb} {e.Route}", e.Repo));
        foreach (var c in ClientMethods.Values) Add(new NodeRef(c.Id, NodeKind.ClientMethod, $"{c.TypeName}.{c.MethodName}", c.Repo));
        foreach (var c in CallSites.Values) Add(new NodeRef(c.Id, NodeKind.CallSite, c.ContainingMember, c.Repo));
        foreach (var m in Models.Values)
            Add(new NodeRef(m.Id, NodeKind.Model, m.FullName, string.IsNullOrEmpty(m.Repo) ? null : m.Repo));

        foreach (var e in graph.Edges)
        {
            var hop = e.Kind == EdgeKind.Produces
                ? new Hop(e.To, e.From, e.Kind, e.Confidence, e)
                : new Hop(e.From, e.To, e.Kind, e.Confidence, e);
            Append(_deps, hop.From, hop);
            Append(_dependents, hop.To, hop);
        }
    }

    public DepGraph Graph { get; }
    public IReadOnlyDictionary<string, RepoNode> Repos { get; }
    public IReadOnlyDictionary<string, ProjectNode> Projects { get; }
    public IReadOnlyDictionary<string, PackageNode> Packages { get; }
    public IReadOnlyDictionary<string, EndpointNode> Endpoints { get; }
    public IReadOnlyDictionary<string, ClientMethodNode> ClientMethods { get; }
    public IReadOnlyDictionary<string, CallSiteNode> CallSites { get; }
    public IReadOnlyDictionary<string, ModelNode> Models { get; }
    public IReadOnlyCollection<NodeRef> Nodes => _nodes.Values;

    public bool TryGet(string id, out NodeRef node) => _nodes.TryGetValue(id, out node!);

    public NodeRef Get(string id) => TryGet(id, out var n) ? n : throw NotFound(id);

    public IReadOnlyList<Hop> DependenciesOf(string id) => _deps.TryGetValue(id, out var l) ? l : [];
    public IReadOnlyList<Hop> DependentsOf(string id) => _dependents.TryGetValue(id, out var l) ? l : [];

    public string ResolveRepo(string q) =>
        _nodes.ContainsKey(q) && _nodes[q].Kind == NodeKind.Repo ? q
        : Repos.Values.FirstOrDefault(r => r.Name.Equals(q, StringComparison.OrdinalIgnoreCase))?.Id ?? throw NotFound(q);

    public string ResolvePackage(string q) =>
        Packages.ContainsKey(q) ? q
        : Packages.Values.FirstOrDefault(p => p.PackageId.Equals(q, StringComparison.OrdinalIgnoreCase))?.Id ?? throw NotFound(q);

    public string ResolveEndpoint(string q)
    {
        if (Endpoints.ContainsKey(q)) return q;
        var space = q.Trim().IndexOf(' ');
        if (space <= 0) throw NotFound(q);
        var verb = q.Trim()[..space].ToUpperInvariant();
        var route = q.Trim()[(space + 1)..].Trim();
        var sameVerb = Endpoints.Values.Where(e => e.Verb.Equals(verb, StringComparison.OrdinalIgnoreCase)).ToList();
        var exact = sameVerb.Where(e => e.Route.Equals(route, StringComparison.OrdinalIgnoreCase)).ToList();
        var matches = exact.Count > 0 ? exact
            : sameVerb.Where(e => e.NormalizedRoute == NormalizeRoute(route)).ToList();
        return matches.Count switch
        {
            1 => matches[0].Id,
            0 => throw NotFound(q),
            _ => throw new QueryException(QueryException.Ambiguous, $"'{q}' matches {matches.Count} endpoints",
                "Pass one of the suggested endpoint ids.", matches.Select(m => m.Id).Order(StringComparer.Ordinal).ToList()),
        };
    }

    public string ResolveModel(string q)
    {
        if (Models.ContainsKey(q)) return q;
        var full = Models.Values.Where(m => m.FullName == q).ToList();
        var matches = full.Count > 0 ? full : Models.Values.Where(m => SimpleName(m.FullName) == q).ToList();
        return matches.Count switch
        {
            1 => matches[0].Id,
            0 => throw NotFound(q),
            _ => throw new QueryException(QueryException.Ambiguous, $"'{q}' matches {matches.Count} models",
                "Pass one of the suggested model ids.", matches.Select(m => m.Id).Order(StringComparer.Ordinal).ToList()),
        };
    }

    public string ResolveAny(string q)
    {
        if (_nodes.ContainsKey(q)) return q;
        var repo = Repos.Values.FirstOrDefault(r => r.Name.Equals(q, StringComparison.OrdinalIgnoreCase));
        if (repo is not null) return repo.Id;
        var pkg = Packages.Values.FirstOrDefault(p => p.PackageId.Equals(q, StringComparison.OrdinalIgnoreCase));
        if (pkg is not null) return pkg.Id;
        if (q.Trim().Contains(' ')) return ResolveEndpoint(q);
        return ResolveModel(q);
    }

    public IReadOnlyList<string> Suggest(string q, int max = 5)
    {
        var needle = q.ToLowerInvariant();
        return _nodes.Values
            .Select(n => (n.Id,
                Contains: n.Id.Contains(needle, StringComparison.OrdinalIgnoreCase) || n.Label.Contains(needle, StringComparison.OrdinalIgnoreCase),
                Dist: Math.Min(Fuzzy.Distance(n.Id.ToLowerInvariant(), needle), Fuzzy.Distance(n.Label.ToLowerInvariant(), needle))))
            .OrderBy(x => x.Contains ? 0 : 1).ThenBy(x => x.Dist).ThenBy(x => x.Id, StringComparer.Ordinal)
            .Take(max).Select(x => x.Id).ToList();
    }

    public QueryException NotFound(string q) =>
        new(QueryException.NotFound, $"No node matches '{q}'",
            "Use list_repos or find_endpoints to discover ids; endpoints also accept 'VERB /route'.", Suggest(q));

    private void Add(NodeRef n) => _nodes.TryAdd(n.Id, n);

    private static void Append(Dictionary<string, List<Hop>> map, string key, Hop hop)
    {
        if (!map.TryGetValue(key, out var list)) map[key] = list = [];
        list.Add(hop);
    }

    private static IReadOnlyDictionary<string, T> ById<T>(IEnumerable<T> items, Func<T, string> id) =>
        items.GroupBy(id).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

    private static string SimpleName(string fullName)
    {
        var s = fullName[(fullName.LastIndexOf('.') + 1)..];
        var tick = s.IndexOf('`');
        return tick >= 0 ? s[..tick] : s;
    }

    private static string NormalizeRoute(string route) =>
        string.Join('/', Placeholder().Replace(route.Split('?', '#')[0], "{}")
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToLowerInvariant();

    [GeneratedRegex(@"\{[^{}]*\}")]
    private static partial Regex Placeholder();
}
```

- [ ] **Step 5: Run tests**

Run: `dotnet test tests/Depenk.Tests --filter GraphIndexTests`
Expected: PASS (10 tests: 7 facts plus 3 Levenshtein cases). Then run `dotnet build Depenk.sln` and expect 0 warnings.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(query): graph index with normalized dependency adjacency, resolvers and suggestions"
```

### Task 2: Result records + QueryService browse queries (list_repos, get_repo, find_endpoints, get_endpoint, get_diagnostics)

**Files:**
- Create: `src/Depenk.Query/Results.cs`, `src/Depenk.Query/QueryService.Browse.cs`
- Modify: `tests/Depenk.Tests/TestUtil/FixtureGraph.cs` (add `Query`)
- Test: `tests/Depenk.Tests/Query/QueryBrowseTests.cs`

**Interfaces:**
- Consumes: `GraphIndex`, `NodeRef`, `Hop`, `QueryException` (Task 1); Core model.
- Produces (all in namespace `Depenk.Query`):
  - every record in `Results.cs` below (Task 3 adds no new result files; it uses these)
  - `sealed partial class QueryService(GraphIndex index)` with:
    - fields: `Index`, `Graph`, and the constants `DefaultLimit = 50` and `MaxLimit = 500`
    - methods: `ListReposResult ListRepos()`, `RepoDetail GetRepo(string repo)`, `Paged<EndpointSummary> FindEndpoints(string? query, string? repo = null, string? verb = null, int? limit = null)`, `EndpointDetail GetEndpoint(string endpoint)`, `DiagnosticsResult GetDiagnostics(string? kind = null, string? repo = null, string? severity = null, int? limit = null)`
    - internal helpers: `ModelRef ToModelRef(string id, Confidence c, string? source)`, `string? PackageOf(string projectId)`, `IEnumerable<Diagnostic> DiagnosticsFor(string repoName)`, `int Cap(int? limit)`
  - `FixtureGraph.Query : QueryService`

**Rules:**
- **Repo names:** `Repo` fields on nodes hold the repo **name** (`"orders"`), and repo IDs are `repo:{name}`.
- **Cap:** `Cap(limit) = Math.Clamp(limit ?? 50, 1, 500)`.
- **Lists:** every list is ordered by ordinal ID unless stated otherwise.
- **A diagnostic belongs to a repo when:** any of its node IDs is that repo's ID, or resolves to a node whose `Repo` equals the repo name.
- **`find_endpoints`:**
  - the query matches `Route` or `Handler` case-insensitively; empty or null matches everything
  - `Callers` = the number of `targets` dependents
  - when truncated, the hint is `"Narrow with query, repo or verb."`
- **`get_repo.consumes`:** only internal (non-external) packages whose producing repo is a different repo.

- [ ] **Step 1: Write failing tests**

Add this to `FixtureGraph` (in `tests/Depenk.Tests/TestUtil/FixtureGraph.cs`):

```csharp
    public static QueryService Query => new(Index);
```

`tests/Depenk.Tests/Query/QueryBrowseTests.cs`:

```csharp
using Depenk.Core.Model;
using Depenk.Query;
using Depenk.Tests.TestUtil;

namespace Depenk.Tests.Query;

public class QueryBrowseTests
{
    private static readonly QueryService Q = FixtureGraph.Query;

    [Fact]
    public void ListRepos_WithLinks()
    {
        var r = Q.ListRepos();
        Assert.Equal(["billing", "customers", "gateway", "orders", "shared"], r.Repos.Select(x => x.Name));
        Assert.Equal(6, r.Links.Count);
        var billing = r.Repos.Single(x => x.Name == "billing");
        Assert.Equal(["repo:customers", "repo:orders"], billing.DependsOn);
        Assert.Equal(["repo:billing", "repo:gateway"], r.Repos.Single(x => x.Name == "orders").DependedOnBy);
        Assert.Equal(1, r.Links.Single(l => l.From == "repo:billing" && l.To == "repo:orders").CallCount);
    }

    [Fact]
    public void GetRepo_PublishesConsumesAndDependents()
    {
        var r = Q.GetRepo("orders");
        Assert.Equal(["Orders.Api", "Orders.Client", "Orders.Tests"], r.Projects.Select(p => p.Name));
        Assert.Equal(("Orders.Client", "3.4.1"), (r.Publishes.Single().PackageId, r.Publishes.Single().Version));
        Assert.Equal(["Billing.Client", "Customers.Client", "Shared.Kernel"],
            r.Consumes.Select(c => c.PackageId).Distinct().Order());
        Assert.Equal("billing", r.Consumes.First(c => c.PackageId == "Billing.Client").ProducerRepo);
        Assert.DoesNotContain(r.Consumes, c => c.PackageId == "Acme.Http");
        Assert.Equal(["repo:billing", "repo:gateway"], r.DependedOnBy.Select(l => l.From));
        Assert.Equal(4, r.Endpoints);
    }

    [Fact]
    public void FindEndpoints_MatchesRouteOrHandler_AndCountsCallers()
    {
        var r = Q.FindEndpoints("orders");
        Assert.Equal(4, r.Items.Count);
        Assert.All(r.Items, e => Assert.Equal("orders", e.Repo));
        Assert.Equal(2, r.Items.Single(e => e.Id == "ep:orders:GET:/api/orders/{id}").Callers);
        Assert.False(r.Truncated);
    }

    [Fact]
    public void FindEndpoints_IsCapped_WithHint()
    {
        var r = Q.FindEndpoints(null, limit: 2);
        Assert.Equal((2, 7, true), (r.Items.Count, r.Total, r.Truncated));
        Assert.Equal("Narrow with query, repo or verb.", r.Hint);
        Assert.Equal(2, Q.FindEndpoints("", repo: "customers", verb: "get").Items.Count);
    }

    [Fact]
    public void GetEndpoint_ContractClientsAndCallers()
    {
        var e = Q.GetEndpoint("GET /api/orders/{id}");
        Assert.Equal("ep:orders:GET:/api/orders/{id}", e.Id);
        Assert.Equal(("id", "route"), (e.Parameters.Single().Name, e.Parameters.Single().Source));
        var ok = e.Returns.Single(r => r.StatusCode == 200);
        Assert.Equal("model:Orders.Client:Acme.Orders.Client.OrderDto", ok.Models.Single().Id);
        Assert.Equal(["cm:Orders.Client:IOrdersClient.GetOrderAsync", "cm:Orders.Client:OrdersClient.GetOrderAsync"],
            e.ClientMethods.Select(c => c.Id));
        Assert.All(e.ClientMethods, c => Assert.Equal(("Orders.Client", "configured-wrapper"), (c.PackageId, c.Strategy)));
        var caller = e.Callers.Single(c => c.Id.StartsWith("cs:billing/Billing.Api:InvoiceBuilder.BuildAsync:"));
        Assert.Equal("cm:Orders.Client:IOrdersClient.GetOrderAsync", caller.ClientMethodId);
        Assert.Equal(Confidence.Medium, caller.Confidence);

        var post = Q.GetEndpoint("POST /api/orders");
        Assert.Equal(("model:Orders.Client:Acme.Orders.Client.CreateOrderRequest", "body"),
            (post.Accepts.Single().Id, post.Accepts.Single().Source));
    }

    [Fact]
    public void GetEndpoint_Unknown_IsNotFound()
    {
        var ex = Assert.Throws<QueryException>(() => Q.GetEndpoint("GET /api/nope"));
        Assert.Equal("not_found", ex.Code);
    }

    [Fact]
    public void GetDiagnostics_FiltersAndCounts()
    {
        Assert.Equal("pkg:Orders.Client", Q.GetDiagnostics(kind: "versionDrift").Items.Single().NodeIds[0]);
        var billing = Q.GetDiagnostics(repo: "billing");
        Assert.Contains(billing.Items, d => d.Kind == DiagnosticKinds.ParseError);
        Assert.Contains(billing.Items, d => d.Kind == DiagnosticKinds.Cycle);
        Assert.Equal(billing.Total, billing.ByKind.Sum(k => k.Count));
        Assert.True(Q.GetDiagnostics(limit: 1).Truncated);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Depenk.Tests --filter QueryBrowseTests`
Expected: the build FAILS with `The type or namespace name 'QueryService' could not be found`.

- [ ] **Step 3: Implement the result records**

`src/Depenk.Query/Results.cs`:

```csharp
using Depenk.Core.Model;

namespace Depenk.Query;

public interface ITruncatable { bool Truncated { get; } }

public sealed record Paged<T>(List<T> Items, int Total, bool Truncated, string? Hint) : ITruncatable;

// list_repos / get_repo
public sealed record RepoLink(string From, string To, Confidence Confidence, List<string> ViaPackages, int CallCount);
public sealed record RepoSummary(string Id, string Name, int Projects, int Endpoints, int ClientMethods, int Diagnostics,
    List<string> DependsOn, List<string> DependedOnBy);
public sealed record ListReposResult(List<RepoSummary> Repos, List<RepoLink> Links);
public sealed record ProjectSummary(string Id, string Name, ProjectKind Kind, string Path);
public sealed record PackageVersion(string PackageId, string? Version, string ProjectId);
public sealed record ConsumedPackage(string PackageId, string? Version, string ConsumerProjectId, string? ProducerRepo);
public sealed record DiagnosticCount(string Kind, int Count);
public sealed record RepoDetail(string Id, string Name, string Path, string? HeadSha, List<ProjectSummary> Projects,
    List<PackageVersion> Publishes, List<ConsumedPackage> Consumes, List<RepoLink> DependsOn, List<RepoLink> DependedOnBy,
    int Endpoints, List<DiagnosticCount> Diagnostics);

// endpoints
public sealed record EndpointSummary(string Id, string Verb, string Route, string Handler, string Repo, int Callers);
public sealed record ModelRef(string Id, string FullName, string Repo, ModelKind Kind, Confidence Confidence, string? Source);
public sealed record ResponseRef(int StatusCode, string TypeName, List<ModelRef> Models);
public sealed record ClientMethodRef(string Id, string Type, string Method, string Signature, string Repo, string? PackageId,
    string Strategy, Confidence Confidence);
public sealed record CallSiteRef(string Id, string Repo, string ProjectId, string Member, SourceLocation Location,
    Confidence Confidence, string ClientMethodId);
public sealed record EndpointDetail(string Id, string Verb, string Route, string Repo, string ProjectId, string Handler,
    SourceLocation Location, List<EndpointParameter> Parameters, List<ModelRef> Accepts, List<ResponseRef> Returns,
    List<ClientMethodRef> ClientMethods, List<CallSiteRef> Callers);

// diagnostics
public sealed record DiagnosticsResult(List<Diagnostic> Items, int Total, bool Truncated, List<DiagnosticCount> ByKind)
    : ITruncatable;

// models (Task 3)
public sealed record FieldNode(string Name, string TypeName, bool Nullable, bool Collection, bool CrossRepo,
    List<ModelTree>? Types);
public sealed record ModelTree(string Id, string FullName, ModelKind Kind, string Repo, string? ProjectId,
    SourceLocation? Location, List<string>? EnumValues, List<FieldNode> Fields);
public sealed record UsageRef(string EndpointId, string Relation, string Via, Confidence Confidence, string Repo);
public sealed record ModelUsages(string ModelId, List<UsageRef> Endpoints, List<string> ContainedIn, List<string> Repos);

// trace / impact (Task 3)
public sealed record TraceStep(string Id, NodeKind Kind, string Label, string Direction, int Depth, string ViaFrom,
    EdgeKind ViaKind, Confidence Confidence);
public sealed record TraceResult(string Root, string Direction, List<TraceStep> Nodes, bool Truncated) : ITruncatable;
public sealed record Affected(string Id, string Label, string Repo, Confidence Confidence);
public sealed record ImpactResult(string Target, string TargetKind, string? Field, List<Affected> Repos,
    List<Affected> Projects, List<Affected> Endpoints, List<Affected> ClientMethods, List<Affected> CallSites,
    List<Affected> Models);

// how_to_call (Task 3)
public sealed record CallOption(string PackageId, string? LatestVersion, string ProducerProjectId, string Type,
    string Method, string Signature, Confidence Confidence, List<ModelRef> Request, List<ResponseRef> Response);
public sealed record HowToCallResult(string EndpointId, List<CallOption> Options, string? Hint);

// get_source (Task 4)
public sealed record SourceSnippet(string NodeId, string Path, int Line, int StartLine, List<string> Lines);
```

- [ ] **Step 4: Implement the browse queries**

`src/Depenk.Query/QueryService.Browse.cs`:

```csharp
using Depenk.Core;
using Depenk.Core.Model;

namespace Depenk.Query;

public sealed partial class QueryService(GraphIndex index)
{
    public const int DefaultLimit = 50, MaxLimit = 500;

    public GraphIndex Index { get; } = index;
    public DepGraph Graph => Index.Graph;

    public ListReposResult ListRepos()
    {
        var links = Links();
        var repos = Index.Repos.Values.OrderBy(r => r.Name, StringComparer.Ordinal).Select(r => new RepoSummary(
            r.Id, r.Name,
            Graph.Projects.Count(p => p.Repo == r.Name),
            Graph.Endpoints.Count(e => e.Repo == r.Name),
            Graph.ClientMethods.Count(c => c.Repo == r.Name),
            DiagnosticsFor(r.Name).Count(),
            links.Where(l => l.From == r.Id).Select(l => l.To).ToList(),
            links.Where(l => l.To == r.Id).Select(l => l.From).ToList())).ToList();
        return new ListReposResult(repos, links);
    }

    public RepoDetail GetRepo(string repo)
    {
        var r = Index.Repos[Index.ResolveRepo(repo)];
        var projects = Graph.Projects.Where(p => p.Repo == r.Name).OrderBy(p => p.Id, StringComparer.Ordinal).ToList();
        var projectIds = projects.Select(p => p.Id).ToHashSet();
        var publishes = Graph.EdgesOf(EdgeKind.Produces).Where(e => projectIds.Contains(e.From))
            .Select(e => new PackageVersion(Index.Packages[e.To].PackageId, e.Version, e.From))
            .OrderBy(p => p.PackageId, StringComparer.Ordinal).ToList();
        var consumes = Graph.EdgesOf(EdgeKind.References)
            .Where(e => projectIds.Contains(e.From) && Index.Packages.TryGetValue(e.To, out var p) && !p.External)
            .Select(e => new ConsumedPackage(Index.Packages[e.To].PackageId, e.Version, e.From, Index.Get(e.To).Repo))
            .Where(c => c.ProducerRepo != r.Name)
            .OrderBy(c => c.PackageId, StringComparer.Ordinal).ThenBy(c => c.ConsumerProjectId, StringComparer.Ordinal).ToList();
        var links = Links();
        return new RepoDetail(r.Id, r.Name, r.Path, r.HeadSha,
            projects.Select(p => new ProjectSummary(p.Id, p.Name, p.Kind, p.Path)).ToList(),
            publishes, consumes,
            links.Where(l => l.From == r.Id).ToList(),
            links.Where(l => l.To == r.Id).ToList(),
            Graph.Endpoints.Count(e => e.Repo == r.Name),
            CountByKind(DiagnosticsFor(r.Name)));
    }

    public Paged<EndpointSummary> FindEndpoints(string? query, string? repo = null, string? verb = null, int? limit = null)
    {
        var repoName = repo is null ? null : Index.Repos[Index.ResolveRepo(repo)].Name;
        var matches = Graph.Endpoints
            .Where(e => string.IsNullOrEmpty(query)
                        || e.Route.Contains(query, StringComparison.OrdinalIgnoreCase)
                        || e.Handler.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Where(e => repoName is null || e.Repo == repoName)
            .Where(e => verb is null || e.Verb.Equals(verb, StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.Id, StringComparer.Ordinal)
            .Select(e => new EndpointSummary(e.Id, e.Verb, e.Route, e.Handler, e.Repo,
                Index.DependentsOf(e.Id).Count(h => h.Kind == EdgeKind.Targets)))
            .ToList();
        return Page(matches, limit, "Narrow with query, repo or verb.");
    }

    public EndpointDetail GetEndpoint(string endpoint)
    {
        var ep = Index.Endpoints[Index.ResolveEndpoint(endpoint)];
        var deps = Index.DependenciesOf(ep.Id);
        var accepts = deps.Where(h => h.Kind == EdgeKind.Accepts)
            .Select(h => ToModelRef(h.To, h.Confidence, h.Edge.Source)).ToList();
        var returns = Responses(ep, deps);
        var clientMethods = Index.DependentsOf(ep.Id).Where(h => h.Kind == EdgeKind.Targets)
            .Select(h => ToClientMethodRef(Index.ClientMethods[h.From], h))
            .OrderBy(c => c.Id, StringComparer.Ordinal).ToList();
        var callers = clientMethods
            .SelectMany(cm => Index.DependentsOf(cm.Id).Where(h => h.Kind == EdgeKind.Invokes)
                .Select(h => (Site: Index.CallSites[h.From], Hop: h, ClientMethod: cm.Id)))
            .Select(x => new CallSiteRef(x.Site.Id, x.Site.Repo, x.Site.ProjectId, x.Site.ContainingMember,
                x.Site.Location, x.Hop.Confidence, x.ClientMethod))
            .OrderBy(c => c.Id, StringComparer.Ordinal).ToList();
        return new EndpointDetail(ep.Id, ep.Verb, ep.Route, ep.Repo, ep.ProjectId, ep.Handler, ep.Location,
            ep.Parameters, accepts, returns, clientMethods, callers);
    }

    public DiagnosticsResult GetDiagnostics(string? kind = null, string? repo = null, string? severity = null, int? limit = null)
    {
        IEnumerable<Diagnostic> items = Graph.Diagnostics;
        if (repo is not null) items = DiagnosticsFor(Index.Repos[Index.ResolveRepo(repo)].Name);
        if (kind is not null) items = items.Where(d => d.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase));
        if (severity is not null) items = items.Where(d => d.Severity.Equals(severity, StringComparison.OrdinalIgnoreCase));
        var all = items.ToList();
        var cap = Cap(limit);
        return new DiagnosticsResult(all.Take(cap).ToList(), all.Count, all.Count > cap, CountByKind(all));
    }

    internal ModelRef ToModelRef(string id, Confidence c, string? source)
    {
        var m = Index.Models[id];
        return new ModelRef(m.Id, m.FullName, m.Repo, m.Kind, c, source);
    }

    internal List<ResponseRef> Responses(EndpointNode ep, IReadOnlyList<Hop> deps) =>
        ep.Responses.GroupBy(r => r.StatusCode).OrderBy(g => g.Key).Select(g => new ResponseRef(g.Key, g.First().TypeName,
            deps.Where(h => h.Kind == EdgeKind.Returns && h.Edge.StatusCode == g.Key)
                .Select(h => ToModelRef(h.To, h.Confidence, null)).ToList())).ToList();

    internal ClientMethodRef ToClientMethodRef(ClientMethodNode cm, Hop target) =>
        new(cm.Id, cm.TypeName, cm.MethodName, cm.Signature, cm.Repo, PackageOf(cm.ProjectId),
            target.Edge.Strategy ?? cm.Strategy, target.Confidence);

    internal string? PackageOf(string projectId) =>
        Graph.EdgesOf(EdgeKind.Produces).Where(e => e.From == projectId)
            .Select(e => Index.Packages.GetValueOrDefault(e.To)?.PackageId).FirstOrDefault(p => p is not null);

    internal IEnumerable<Diagnostic> DiagnosticsFor(string repoName) =>
        Graph.Diagnostics.Where(d => d.NodeIds.Any(id =>
            id == Ids.Repo(repoName) || (Index.TryGet(id, out var n) && n.Repo == repoName)));

    internal static int Cap(int? limit) => Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);

    private Paged<T> Page<T>(List<T> all, int? limit, string hint)
    {
        var cap = Cap(limit);
        var truncated = all.Count > cap;
        return new Paged<T>(all.Take(cap).ToList(), all.Count, truncated, truncated ? hint : null);
    }

    private List<RepoLink> Links() =>
        Graph.EdgesOf(EdgeKind.DependsOn)
            .Select(e => new RepoLink(e.From, e.To, e.Confidence, e.ViaPackages ?? [], e.CallCount ?? 0))
            .OrderBy(l => l.From, StringComparer.Ordinal).ThenBy(l => l.To, StringComparer.Ordinal).ToList();

    private static List<DiagnosticCount> CountByKind(IEnumerable<Diagnostic> ds) =>
        ds.GroupBy(d => d.Kind).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new DiagnosticCount(g.Key, g.Count())).ToList();
}
```

- [ ] **Step 5: Run tests**

Run: `dotnet test tests/Depenk.Tests --filter QueryBrowseTests`
Expected: PASS (7 tests). Then run `dotnet build Depenk.sln` and expect 0 warnings.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(query): browse queries — repos, endpoints with contracts and callers, diagnostics"
```

### Task 3: Analysis queries (get_model, find_model_usages, trace, impact_of_change, how_to_call) + overview markdown

**Files:**
- Create: `src/Depenk.Query/QueryService.Analyze.cs`, `src/Depenk.Query/OverviewWriter.cs`
- Test: `tests/Depenk.Tests/Query/QueryAnalyzeTests.cs`

**Interfaces:**
- Consumes: `QueryService` internals (`Index`, `Graph`, `ToModelRef`, `Responses`, `PackageOf`, `Cap`), the result records (Task 2) and `GraphIndex` (Task 1).
- Produces these methods on `QueryService`:
  - `ModelTree GetModel(string model, int depth = 2)`
  - `ModelUsages FindModelUsages(string model)`
  - `TraceResult Trace(string node, string direction = "down", int depth = 3, int? limit = null)`
  - `ImpactResult ImpactOfChange(string target)`
  - `HowToCallResult HowToCall(string endpoint)`
  - `string Overview()`
  - `internal static Confidence Min(Confidence a, Confidence b)`
- Also produces: `static class OverviewWriter { string Write(QueryService q); }`

**Rules:**
- **Confidence order:** `Low < Medium < High < Certain` (enum order). A path's confidence is the **minimum** along the path. When several paths reach a node, the node gets the **best** such minimum (relaxation BFS).
- **`get_model`:** `depth` is clamped to 0–6 and is the number of model levels expanded below the root.
  - A field's `Types` lists the child trees for that field's `fieldOf` edges. It is `null` when the depth is exhausted or the field has no model types.
  - A type already on the current path becomes a stub with no fields (cycle guard).
  - `CrossRepo` is true when any child model's repo is non-empty and differs from the parent's.
- **`find_model_usages`:**
  - The targets are the model plus every model that contains it transitively (dependents via `fieldOf`). `ContainedIn` lists those containing model IDs.
  - Each endpoint that `accepts` or `returns` a target becomes a `UsageRef` whose `Via` is the target model.
  - `Repos` (repo **IDs**, sorted) = the endpoints' repos plus the repos of call sites that invoke client methods targeting those endpoints.
- **`trace`:**
  - The root comes from `ResolveAny`. `direction` is `up`, `down` or `both`; anything else is `invalid_argument`. `depth` is clamped to 1–10.
  - BFS per direction: `down` follows `DependenciesOf` (to `Hop.To`) and `up` follows `DependentsOf` (to `Hop.From`).
  - Each node is visited once per direction. Hops are visited in ordinal order of the far node's ID. `both` = down first, then up.
  - Results are capped with `Cap(limit)`.
- **`impact_of_change`:**
  - **Target:** resolved with `ResolveAny`. If that throws `not_found` and the target contains `.`, try `"<model>.<field>"`: resolve the part before the last `.` as a model, which must have that field. This is `TargetKind = "field"`, and the field's impact is the owning model's impact.
  - **`TargetKind` otherwise:** the node kind in camelCase (`repo`, `project`, `package`, `endpoint`, `clientMethod`, `callSite`, `model`).
  - **Affected nodes:** relaxation BFS over `DependentsOf` from the start node, which is excluded from the results. Nodes are partitioned by kind.
  - **Roll-up:** every affected node with a `ProjectId` (endpoint, client method, call site, model) also adds that project. Every affected node with a non-empty repo adds `repo:{repo}`. Both use the best confidence.
  - Every list is ordered by ID.
- **`how_to_call`:**
  - There is one option per client method that `targets` the endpoint and whose project produces a package.
  - `LatestVersion` is that `produces` edge's version.
  - Ordering: interface types first (name matches `^I[A-Z]`), then ordinal ID.
  - With no options, the hint is `"No client package method targets this endpoint; call it over HTTP directly or add a method to its client package."`

- [ ] **Step 1: Write failing tests**

`tests/Depenk.Tests/Query/QueryAnalyzeTests.cs`:

```csharp
using Depenk.Core.Model;
using Depenk.Query;
using Depenk.Tests.TestUtil;

namespace Depenk.Tests.Query;

public class QueryAnalyzeTests
{
    private static readonly QueryService Q = FixtureGraph.Query;
    private const string OrderDto = "model:Orders.Client:Acme.Orders.Client.OrderDto";
    private const string CustomerDto = "model:Customers.Client:Acme.Customers.Client.CustomerDto";
    private static readonly string[] OrderEndpoints =
        ["ep:orders:GET:/api/orders", "ep:orders:GET:/api/orders/{id}", "ep:orders:POST:/api/orders"];

    [Fact]
    public void GetModel_ExpandsTwoLevels_AndMarksCrossRepo()
    {
        var m = Q.GetModel("OrderDto");
        Assert.Equal(["Id", "Status", "Customer", "Lines"], m.Fields.Select(f => f.Name));
        var customer = m.Fields.Single(f => f.Name == "Customer");
        Assert.True(customer.CrossRepo);
        Assert.Equal(CustomerDto, customer.Types!.Single().Id);
        var line = m.Fields.Single(f => f.Name == "Lines").Types!.Single();
        var money = line.Fields.Single(f => f.Name == "UnitPrice").Types!.Single();
        Assert.Equal("model:Shared.Kernel:Acme.Shared.Money", money.Id);
        Assert.Equal(["Amount", "Currency"], money.Fields.Select(f => f.Name));
        Assert.All(money.Fields, f => Assert.Null(f.Types)); // depth exhausted
        Assert.Null(Q.GetModel("OrderDto", depth: 0).Fields.Single(f => f.Name == "Lines").Types);
    }

    [Fact]
    public void FindModelUsages_IncludesContainingModelsAndConsumerRepos()
    {
        var direct = Q.FindModelUsages("OrderDto");
        Assert.Equal(OrderEndpoints, direct.Endpoints.Select(u => u.EndpointId).Order(StringComparer.Ordinal));
        Assert.Equal(["repo:billing", "repo:gateway", "repo:orders"], direct.Repos);

        var nested = Q.FindModelUsages(CustomerDto);
        Assert.Equal([OrderDto], nested.ContainedIn);
        Assert.All(nested.Endpoints, u => Assert.Equal(OrderDto, u.Via));
        Assert.Equal(["repo:billing", "repo:gateway", "repo:orders"], nested.Repos);
    }

    [Fact]
    public void Trace_DownUpAndBoth()
    {
        Assert.Equal(["repo:customers", "repo:orders"], Q.Trace("billing", "down", 1).Nodes.Select(n => n.Id));

        var up = Q.Trace(OrderDto, "up", 3).Nodes;
        Assert.Contains(up, n => n.Id == "ep:orders:GET:/api/orders/{id}" && n.Depth == 1 && n.ViaKind == EdgeKind.Returns);
        Assert.Contains(up, n => n.Id == "cm:Orders.Client:IOrdersClient.GetOrderAsync" && n.Depth == 2);
        Assert.Contains(up, n => n.Id.StartsWith("cs:billing/Billing.Api:InvoiceBuilder.BuildAsync:")
                                 && n.Depth == 3 && n.Confidence == Confidence.Medium);

        var both = Q.Trace("orders", "both", 1);
        Assert.Contains(both.Nodes, n => n.Direction == "down" && n.Id == "repo:shared");
        Assert.Contains(both.Nodes, n => n.Direction == "up" && n.Id == "repo:gateway");
        Assert.True(Q.Trace("orders", "both", 10, limit: 1).Truncated);
        Assert.Equal("invalid_argument", Assert.Throws<QueryException>(() => Q.Trace("orders", "sideways")).Code);
    }

    [Fact]
    public void ImpactOfModel_ReachesCallSitesInOtherRepos()
    {
        var r = Q.ImpactOfChange("OrderDto");
        Assert.Equal(("model", (string?)null), (r.TargetKind, r.Field));
        Assert.Equal(OrderEndpoints, r.Endpoints.Select(a => a.Id));
        Assert.Equal(6, r.ClientMethods.Count);
        Assert.Equal(3, r.CallSites.Count);
        Assert.All(r.CallSites, c => Assert.Equal(Confidence.Medium, c.Confidence));
        Assert.Equal(["repo:billing", "repo:gateway", "repo:orders"], r.Repos.Select(a => a.Id));
        Assert.Contains(r.Projects, p => p.Id == "proj:gateway/Gateway.Api");
        Assert.DoesNotContain(r.Models, m => m.Id == OrderDto); // start excluded
    }

    [Fact]
    public void ImpactOfNestedModel_And_Field_And_Package()
    {
        Assert.Contains(Q.ImpactOfChange(CustomerDto).Models, m => m.Id == OrderDto);

        var field = Q.ImpactOfChange("OrderDto.Lines");
        Assert.Equal(("field", "Lines"), (field.TargetKind, field.Field));
        Assert.Equal(OrderEndpoints, field.Endpoints.Select(a => a.Id));

        var pkg = Q.ImpactOfChange("Orders.Client");
        Assert.Equal("package", pkg.TargetKind);
        Assert.Equal(["proj:billing/Billing.Api", "proj:gateway/Gateway.Api"], pkg.Projects.Select(p => p.Id));
        Assert.Equal(["repo:billing", "repo:gateway"], pkg.Repos.Select(p => p.Id));

        Assert.Equal("not_found", Assert.Throws<QueryException>(() => Q.ImpactOfChange("OrderDto.Nope")).Code);
    }

    [Fact]
    public void HowToCall_PrefersInterface_AndExplainsMissingClient()
    {
        var r = Q.HowToCall("GET /api/orders/{id}");
        var first = r.Options[0];
        Assert.Equal(("IOrdersClient", "GetOrderAsync", "Orders.Client", "3.4.1"),
            (first.Type, first.Method, first.PackageId, first.LatestVersion));
        Assert.Equal(OrderDto, first.Response.Single(x => x.StatusCode == 200).Models.Single().Id);
        Assert.Null(r.Hint);

        var none = Q.HowToCall("DELETE /api/orders/{id}");
        Assert.Empty(none.Options);
        Assert.NotNull(none.Hint);
    }

    [Fact]
    public void Overview_IsMarkdownSummary()
    {
        var md = Q.Overview();
        Assert.StartsWith("# depenk workspace overview", md);
        Assert.Contains("5 repos", md);
        Assert.Contains("billing → orders", md);
        Assert.Contains("versionDrift", md);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Depenk.Tests --filter QueryAnalyzeTests`
Expected: the build FAILS with `'QueryService' does not contain a definition for 'GetModel'`.

- [ ] **Step 3: Implement**

`src/Depenk.Query/QueryService.Analyze.cs`:

```csharp
using System.Text.RegularExpressions;
using Depenk.Core.Model;

namespace Depenk.Query;

public sealed partial class QueryService
{
    public ModelTree GetModel(string model, int depth = 2) =>
        Tree(Index.ResolveModel(model), Math.Clamp(depth, 0, 6), new HashSet<string>(StringComparer.Ordinal));

    public ModelUsages FindModelUsages(string model)
    {
        var id = Index.ResolveModel(model);
        var targets = Relax(id, h => h.Kind == EdgeKind.FieldOf);
        var usages = targets
            .SelectMany(t => Index.DependentsOf(t.Key)
                .Where(h => h.Kind is EdgeKind.Accepts or EdgeKind.Returns)
                .Select(h => new UsageRef(h.From, h.Kind == EdgeKind.Accepts ? "accepts" : "returns", t.Key,
                    Min(t.Value, h.Confidence), Index.Endpoints[h.From].Repo)))
            .DistinctBy(u => (u.EndpointId, u.Relation, u.Via))
            .OrderBy(u => u.EndpointId, StringComparer.Ordinal).ThenBy(u => u.Via, StringComparer.Ordinal).ToList();
        var repos = usages.Select(u => u.Repo)
            .Concat(usages.SelectMany(u => CallerRepos(u.EndpointId)))
            .Distinct().Select(r => $"repo:{r}").Order(StringComparer.Ordinal).ToList();
        return new ModelUsages(id, usages,
            targets.Keys.Where(k => k != id).Order(StringComparer.Ordinal).ToList(), repos);
    }

    public TraceResult Trace(string node, string direction = "down", int depth = 3, int? limit = null)
    {
        var root = Index.ResolveAny(node);
        var dir = direction.ToLowerInvariant();
        string[] dirs = dir switch
        {
            "down" => ["down"],
            "up" => ["up"],
            "both" => ["down", "up"],
            _ => throw new QueryException(QueryException.InvalidArgument,
                $"direction must be up, down or both (got '{direction}')", "Use direction: \"down\" for dependencies, \"up\" for dependents."),
        };
        var maxDepth = Math.Clamp(depth, 1, 10);
        var steps = new List<TraceStep>();
        foreach (var d in dirs)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal) { root };
            var queue = new Queue<(string Id, int Depth, Confidence Conf)>([(root, 0, Confidence.Certain)]);
            while (queue.TryDequeue(out var cur))
            {
                if (cur.Depth >= maxDepth) continue;
                var hops = d == "down" ? Index.DependenciesOf(cur.Id) : Index.DependentsOf(cur.Id);
                foreach (var h in hops.OrderBy(h => d == "down" ? h.To : h.From, StringComparer.Ordinal))
                {
                    var next = d == "down" ? h.To : h.From;
                    if (!visited.Add(next) || !Index.TryGet(next, out var n)) continue;
                    var conf = Min(cur.Conf, h.Confidence);
                    steps.Add(new TraceStep(next, n.Kind, n.Label, d, cur.Depth + 1, cur.Id, h.Kind, conf));
                    queue.Enqueue((next, cur.Depth + 1, conf));
                }
            }
        }
        var cap = Cap(limit);
        return new TraceResult(root, dir, steps.Take(cap).ToList(), steps.Count > cap);
    }

    public ImpactResult ImpactOfChange(string target)
    {
        string start;
        string? field = null;
        try
        {
            start = Index.ResolveAny(target);
        }
        catch (QueryException ex) when (ex.Code == QueryException.NotFound && target.Contains('.'))
        {
            var dot = target.LastIndexOf('.');
            start = Index.ResolveModel(target[..dot]);
            field = target[(dot + 1)..];
            if (Index.Models[start].Fields.All(f => f.Name != field)) throw Index.NotFound(target);
        }

        var best = Relax(start, _ => true);
        best.Remove(start);

        var rolled = new Dictionary<string, Confidence>(best, StringComparer.Ordinal);
        void Bump(string id, Confidence c)
        {
            if (!rolled.TryGetValue(id, out var old) || c > old) rolled[id] = c;
        }
        foreach (var (id, c) in best)
        {
            var n = Index.Get(id);
            var projectId = ProjectIdOf(id);
            if (projectId is not null && projectId != start) Bump(projectId, c);
            if (!string.IsNullOrEmpty(n.Repo) && $"repo:{n.Repo}" != start) Bump($"repo:{n.Repo}", c);
        }

        List<Affected> Of(NodeKind kind) => rolled
            .Where(kv => Index.TryGet(kv.Key, out var n) && n.Kind == kind)
            .Select(kv => { var n = Index.Get(kv.Key); return new Affected(n.Id, n.Label, n.Repo ?? "", kv.Value); })
            .OrderBy(a => a.Id, StringComparer.Ordinal).ToList();

        var kindName = field is not null ? "field" : JsonName(Index.Get(start).Kind);
        return new ImpactResult(start, kindName, field, Of(NodeKind.Repo), Of(NodeKind.Project), Of(NodeKind.Endpoint),
            Of(NodeKind.ClientMethod), Of(NodeKind.CallSite), Of(NodeKind.Model));
    }

    public HowToCallResult HowToCall(string endpoint)
    {
        var ep = Index.Endpoints[Index.ResolveEndpoint(endpoint)];
        var deps = Index.DependenciesOf(ep.Id);
        var request = deps.Where(h => h.Kind == EdgeKind.Accepts).Select(h => ToModelRef(h.To, h.Confidence, h.Edge.Source)).ToList();
        var response = Responses(ep, deps);
        var options = Index.DependentsOf(ep.Id).Where(h => h.Kind == EdgeKind.Targets)
            .Select(h => (Cm: Index.ClientMethods[h.From], Hop: h))
            .Select(x => (x.Cm, x.Hop, Produces: Graph.EdgesOf(EdgeKind.Produces).FirstOrDefault(e => e.From == x.Cm.ProjectId)))
            .Where(x => x.Produces is not null && Index.Packages.ContainsKey(x.Produces.To))
            .Select(x => new CallOption(Index.Packages[x.Produces!.To].PackageId, x.Produces.Version, x.Cm.ProjectId,
                x.Cm.TypeName, x.Cm.MethodName, x.Cm.Signature, x.Hop.Confidence, request, response))
            .OrderBy(o => InterfaceName().IsMatch(o.Type) ? 0 : 1)
            .ThenBy(o => $"{o.ProducerProjectId}:{o.Type}.{o.Method}", StringComparer.Ordinal)
            .ToList();
        return new HowToCallResult(ep.Id, options, options.Count > 0 ? null
            : "No client package method targets this endpoint; call it over HTTP directly or add a method to its client package.");
    }

    public string Overview() => OverviewWriter.Write(this);

    internal static Confidence Min(Confidence a, Confidence b) => a < b ? a : b;

    /// <summary>Best (max over paths of min along path) confidence for every node reachable via dependents.</summary>
    private Dictionary<string, Confidence> Relax(string start, Func<Hop, bool> follow)
    {
        var best = new Dictionary<string, Confidence>(StringComparer.Ordinal) { [start] = Confidence.Certain };
        var queue = new Queue<string>([start]);
        while (queue.TryDequeue(out var cur))
        {
            foreach (var h in Index.DependentsOf(cur).Where(follow))
            {
                var c = Min(best[cur], h.Confidence);
                if (best.TryGetValue(h.From, out var old) && c <= old) continue;
                best[h.From] = c;
                queue.Enqueue(h.From);
            }
        }
        return best;
    }

    private ModelTree Tree(string id, int depth, HashSet<string> path)
    {
        var m = Index.Models[id];
        path.Add(id);
        var childHops = Index.DependenciesOf(id).Where(h => h.Kind == EdgeKind.FieldOf).ToList();
        var fields = m.Fields.Select(f =>
        {
            var hops = childHops.Where(h => h.Edge.FieldName == f.Name).ToList();
            var cross = hops.Any(h => Index.Models[h.To].Repo is { Length: > 0 } r && r != m.Repo);
            List<ModelTree>? types = depth > 0 && hops.Count > 0
                ? hops.Select(h => path.Contains(h.To) ? Stub(h.To) : Tree(h.To, depth - 1, path)).ToList()
                : null;
            return new FieldNode(f.Name, f.TypeName, f.Nullable, f.Collection, cross, types);
        }).ToList();
        path.Remove(id);
        return new ModelTree(m.Id, m.FullName, m.Kind, m.Repo, m.ProjectId, m.Location, m.EnumValues, fields);
    }

    private ModelTree Stub(string id)
    {
        var m = Index.Models[id];
        return new ModelTree(m.Id, m.FullName, m.Kind, m.Repo, m.ProjectId, m.Location, m.EnumValues, []);
    }

    private IEnumerable<string> CallerRepos(string endpointId) =>
        Index.DependentsOf(endpointId).Where(h => h.Kind == EdgeKind.Targets)
            .SelectMany(h => Index.DependentsOf(h.From).Where(x => x.Kind == EdgeKind.Invokes))
            .Select(h => Index.CallSites[h.From].Repo);

    private string? ProjectIdOf(string id) =>
        Index.Endpoints.GetValueOrDefault(id)?.ProjectId
        ?? Index.ClientMethods.GetValueOrDefault(id)?.ProjectId
        ?? Index.CallSites.GetValueOrDefault(id)?.ProjectId
        ?? Index.Models.GetValueOrDefault(id)?.ProjectId;

    private static string JsonName(NodeKind k) => char.ToLowerInvariant(k.ToString()[0]) + k.ToString()[1..];

    [GeneratedRegex("^I[A-Z]")]
    private static partial Regex InterfaceName();
}
```

`src/Depenk.Query/OverviewWriter.cs`:

```csharp
using System.Text;
using Depenk.Core.Model;

namespace Depenk.Query;

public static class OverviewWriter
{
    public static string Write(QueryService q)
    {
        var g = q.Graph;
        var repos = q.ListRepos();
        var sb = new StringBuilder();
        sb.AppendLine("# depenk workspace overview").AppendLine();
        sb.AppendLine($"Scanned {g.GeneratedAt:u}. {g.Repos.Count} repos · {g.Projects.Count} projects · " +
                      $"{g.Endpoints.Count} endpoints · {g.ClientMethods.Count} client methods · " +
                      $"{g.CallSites.Count} call sites · {g.Models.Count} models").AppendLine();

        sb.AppendLine("## Repos").AppendLine();
        sb.AppendLine("| Repo | Projects | Endpoints | Client methods | Depends on | Depended on by |");
        sb.AppendLine("|---|---|---|---|---|---|");
        foreach (var r in repos.Repos)
            sb.AppendLine($"| {r.Name} | {r.Projects} | {r.Endpoints} | {r.ClientMethods} | " +
                          $"{Names(r.DependsOn)} | {Names(r.DependedOnBy)} |");
        sb.AppendLine();

        sb.AppendLine("## Service links").AppendLine();
        if (repos.Links.Count == 0) sb.AppendLine("No cross-repo package dependencies found.");
        foreach (var l in repos.Links)
            sb.AppendLine($"- {Name(l.From)} → {Name(l.To)} (via {string.Join(", ", l.ViaPackages)}; " +
                          $"{l.CallCount} call site{(l.CallCount == 1 ? "" : "s")}{(l.Confidence < Confidence.Certain ? $"; {l.Confidence.ToString().ToLowerInvariant()} confidence" : "")})");
        sb.AppendLine();

        sb.AppendLine("## Hotspots").AppendLine();
        foreach (var r in repos.Repos.Where(r => r.DependedOnBy.Count > 0)
                     .OrderByDescending(r => r.DependedOnBy.Count).ThenBy(r => r.Name, StringComparer.Ordinal).Take(5))
            sb.AppendLine($"- Repo **{r.Name}** is depended on by {r.DependedOnBy.Count} repo(s)");
        foreach (var e in q.FindEndpoints(null, limit: QueryService.MaxLimit).Items.Where(e => e.Callers > 0)
                     .OrderByDescending(e => e.Callers).ThenBy(e => e.Id, StringComparer.Ordinal).Take(5))
            sb.AppendLine($"- Endpoint **{e.Verb} {e.Route}** ({e.Repo}) is targeted by {e.Callers} client method(s)");
        sb.AppendLine();

        sb.AppendLine("## Diagnostics").AppendLine();
        var counts = q.GetDiagnostics(limit: 1).ByKind;
        if (counts.Count == 0) sb.AppendLine("None.");
        else
        {
            sb.AppendLine("| Kind | Count |").AppendLine("|---|---|");
            foreach (var c in counts) sb.AppendLine($"| {c.Kind} | {c.Count} |");
        }
        return sb.ToString();
    }

    private static string Name(string repoId) => repoId.StartsWith("repo:", StringComparison.Ordinal) ? repoId[5..] : repoId;
    private static string Names(List<string> ids) => ids.Count == 0 ? "—" : string.Join(", ", ids.Select(Name));
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet test tests/Depenk.Tests --filter QueryAnalyzeTests`
Expected: PASS (7 tests). Then run `dotnet test tests/Depenk.Tests --filter "Category!=Perf"`; everything should pass. Run `dotnet build Depenk.sln` and expect 0 warnings.

If `ImpactOfModel_ReachesCallSitesInOtherRepos` reports a `ClientMethods` count other than 6, list them before changing anything. The fixture has `IOrdersClient` and `OrdersClient`, each with `GetOrderAsync`, `ListAsync` and `CreateAsync`, all targeting OrderDto endpoints.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(query): model trees, usages, trace, impact analysis, how-to-call and overview"
```

### Task 4: Sandboxed source reader (get_source)

**Files:**
- Create: `src/Depenk.Query/SourceReader.cs`
- Test: `tests/Depenk.Tests/Query/SourceReaderTests.cs`

**Interfaces:**
- Consumes: `GraphIndex`, `QueryException`, `SourceSnippet` (Tasks 1–2).
- Produces: `sealed class SourceReader(string workspace)` with `SourceSnippet Read(GraphIndex index, string nodeId, int context = 10)`.

**Rules:**
- **Source location by node kind:**
  - endpoint, client method, call site and model use `Location`
  - a project uses `(Path, 1)`
  - repos, packages and opaque models (with no `Location`) throw `invalid_argument` ("node has no source location")
- **`context`:** clamped to 0–50.
- **Path checks.** The location path is combined with the workspace root and fully resolved. The read is refused with `outside_workspace` when either check fails:
  1. The result must be the root or under it (case-insensitive prefix match on root + separator).
  2. The file and every existing directory from the file up to the root must not be a link (`LinkTarget`) whose final resolved target falls outside the root. This covers junctions and symlinks.
- **Missing file:** `not_found` ("source file no longer exists").
- **Output:** `Lines` contains each line formatted as `$"{n,5}| {text}"`, from `max(1, line-context)` to `min(lineCount, line+context)`. `StartLine` is the first of those, and `Path` is the graph's relative path.

- [ ] **Step 1: Write failing tests**

`tests/Depenk.Tests/Query/SourceReaderTests.cs`:

```csharp
using System.Diagnostics;
using Depenk.Core.Model;
using Depenk.Query;
using Depenk.Tests.TestUtil;

namespace Depenk.Tests.Query;

public class SourceReaderTests
{
    private static GraphIndex IndexWith(params (string Id, string Path, int Line)[] endpoints)
    {
        var g = new DepGraph();
        g.Repos.Add(new RepoNode("repo:r", "r", "r", null, false));
        foreach (var (id, path, line) in endpoints)
            g.Endpoints.Add(new EndpointNode(id, "r", "proj:r/P", "GET", "/x", "x", "C.M", [], [], new SourceLocation(path, line)));
        return new GraphIndex(g);
    }

    private static string Lines(int n) => string.Join("\n", Enumerable.Range(1, n).Select(i => $"line{i}"));

    [Fact]
    public void ReadsNumberedSnippetAroundLine()
    {
        using var ws = new TempWorkspace().File("r/src/A.cs", Lines(20));
        var s = new SourceReader(ws.Root).Read(IndexWith(("ep:a", "r/src/A.cs", 5)), "ep:a", context: 2);
        Assert.Equal(("r/src/A.cs", 5, 3), (s.Path, s.Line, s.StartLine));
        Assert.Equal(["    3| line3", "    4| line4", "    5| line5", "    6| line6", "    7| line7"], s.Lines);
    }

    [Fact]
    public void ClampsAtFileBoundaries()
    {
        using var ws = new TempWorkspace().File("r/A.cs", Lines(3));
        var s = new SourceReader(ws.Root).Read(IndexWith(("ep:a", "r/A.cs", 1)), "ep:a", context: 10);
        Assert.Equal((1, 3), (s.StartLine, s.Lines.Count));
    }

    [Theory]
    [InlineData("../outside.cs")]
    [InlineData("r/../../outside.cs")]
    [InlineData("C:/Windows/win.ini")]
    [InlineData("/etc/passwd")]
    public void RefusesPathsOutsideWorkspace(string path)
    {
        using var ws = new TempWorkspace().File("r/A.cs", "x");
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(ws.Root)!, "outside.cs"), "secret");
        var ex = Assert.Throws<QueryException>(() => new SourceReader(ws.Root).Read(IndexWith(("ep:a", path, 1)), "ep:a"));
        // "C:/..." is only an absolute path on Windows; elsewhere it is a (missing) relative path inside the workspace
        var expected = !OperatingSystem.IsWindows() && path.StartsWith("C:") ? "not_found" : "outside_workspace";
        Assert.Equal(expected, ex.Code);
    }

    [Fact]
    public void RefusesFilesReachedThroughALinkToOutside()
    {
        using var ws = new TempWorkspace().File("r/A.cs", "x");
        var outside = Path.Combine(Path.GetTempPath(), "depenk-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.cs"), "secret");
        try
        {
            var link = Path.Combine(ws.Root, "r", "linked");
            Assert.True(TryLinkDirectory(link, outside), "could not create a directory junction/symlink for the test");
            var ex = Assert.Throws<QueryException>(() =>
                new SourceReader(ws.Root).Read(IndexWith(("ep:a", "r/linked/secret.cs", 1)), "ep:a"));
            Assert.Equal("outside_workspace", ex.Code);
        }
        finally { Directory.Delete(outside, recursive: true); }
    }

    [Fact]
    public void NodesWithoutLocation_AndMissingFiles()
    {
        using var ws = new TempWorkspace();
        var ix = IndexWith(("ep:gone", "r/Gone.cs", 1));
        Assert.Equal("invalid_argument", Assert.Throws<QueryException>(() => new SourceReader(ws.Root).Read(ix, "repo:r")).Code);
        Assert.Equal("not_found", Assert.Throws<QueryException>(() => new SourceReader(ws.Root).Read(ix, "ep:gone")).Code);
        Assert.Equal("not_found", Assert.Throws<QueryException>(() => new SourceReader(ws.Root).Read(ix, "ep:nope")).Code);
    }

    private static bool TryLinkDirectory(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        if (!OperatingSystem.IsWindows()) return false;
        using var p = Process.Start(new ProcessStartInfo("cmd", $"/c mklink /J \"{link}\" \"{target}\"")
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!;
        p.WaitForExit();
        return p.ExitCode == 0;
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Depenk.Tests --filter SourceReaderTests`
Expected: the build FAILS with `The type or namespace name 'SourceReader' could not be found`.

- [ ] **Step 3: Implement**

`src/Depenk.Query/SourceReader.cs`:

```csharp
using Depenk.Core.Model;

namespace Depenk.Query;

public sealed class SourceReader(string workspace)
{
    private readonly string _root = Path.GetFullPath(workspace).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    public SourceSnippet Read(GraphIndex index, string nodeId, int context = 10)
    {
        index.Get(nodeId); // not_found with suggestions
        var location = LocationOf(index, nodeId)
                       ?? throw new QueryException(QueryException.InvalidArgument, $"{nodeId} has no source location",
                           "get_source works for endpoints, client methods, call sites, models and projects.");
        var full = Path.GetFullPath(Path.Combine(_root, location.Path.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsUnderRoot(full) || !LinksStayInside(full))
            throw new QueryException(QueryException.OutsideWorkspace,
                $"Refusing to read '{location.Path}': it resolves outside the workspace", "The graph may be stale or tampered with; run rescan.");
        if (!File.Exists(full))
            throw new QueryException(QueryException.NotFound, $"Source file no longer exists: {location.Path}", "Run rescan to refresh the graph.");

        var lines = File.ReadAllLines(full);
        var ctx = Math.Clamp(context, 0, 50);
        var line = Math.Clamp(location.Line, 1, Math.Max(1, lines.Length));
        var start = Math.Max(1, line - ctx);
        var end = Math.Min(lines.Length, line + ctx);
        var numbered = Enumerable.Range(start, Math.Max(0, end - start + 1)).Select(n => $"{n,5}| {lines[n - 1]}").ToList();
        return new SourceSnippet(nodeId, location.Path, location.Line, start, numbered);
    }

    private static SourceLocation? LocationOf(GraphIndex ix, string id) =>
        ix.Endpoints.GetValueOrDefault(id)?.Location
        ?? ix.ClientMethods.GetValueOrDefault(id)?.Location
        ?? ix.CallSites.GetValueOrDefault(id)?.Location
        ?? ix.Models.GetValueOrDefault(id)?.Location
        ?? (ix.Projects.GetValueOrDefault(id) is { } p ? new SourceLocation(p.Path, 1) : null);

    private bool IsUnderRoot(string full) =>
        full.Equals(_root, StringComparison.OrdinalIgnoreCase)
        || full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The file and every existing directory below the root must not be a link resolving outside the root.
    /// The root itself may be a link (e.g. ~/code symlinked elsewhere) — it is the trust anchor.
    /// </summary>
    private bool LinksStayInside(string full)
    {
        for (var p = full; p is not null && p.Length > _root.Length && IsUnderRoot(p); p = Path.GetDirectoryName(p))
        {
            FileSystemInfo? fsi = File.Exists(p) ? new FileInfo(p) : Directory.Exists(p) ? new DirectoryInfo(p) : null;
            if (fsi?.LinkTarget is null) continue;
            var target = fsi.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
            if (target is null || !IsUnderRoot(Path.GetFullPath(target))) return false;
        }
        return true;
    }
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet test tests/Depenk.Tests --filter SourceReaderTests`
Expected: PASS (8 tests: 4 facts plus 4 theory cases). Then run `dotnet build Depenk.sln` and expect 0 warnings.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(query): sandboxed get_source snippets that refuse paths and links outside the workspace"
```

### Task 5: Depenk.Mcp project + GraphStore (load, scan-on-first-use, staleness, background refresh, rescan)

**Files:**
- Create: `src/Depenk.Mcp/Depenk.Mcp.csproj`, `src/Depenk.Mcp/GraphStore.cs`
- Test: `tests/Depenk.Tests/Mcp/GraphStoreTests.cs`

**Interfaces:**
- Consumes:
  - from Plan 1: `ScanOrchestrator` (`Scan`, `GraphPath`), `ParseCache`, `WorkspaceManifest` (`Compute`, `Save`, `IsUpToDate`), `GraphJson` (`Save`, `Load`)
  - from Tasks 1–2: `GraphIndex`, `QueryService`
  - test helper: `FixtureScanTests.CopyFixture()`
- Produces:
  - `sealed record GraphSnapshot(DepGraph Graph, GraphIndex Index, QueryService Query, bool Stale)`
  - `sealed class GraphStore(string workspace, ScanOrchestrator? orchestrator = null)` with:
    - `string Workspace`
    - `int ScanCount` (for tests)
    - `GraphSnapshot Current()`
    - `Task? StartBackgroundRefresh()`
    - `GraphSnapshot Rescan()`

**Rules (spec §6 startup):**
- **`Current()`:**
  - On the first call it loads `.depenk/graph.json` if it exists, parses, and has `schemaVersion == 1`, then marks the snapshot stale when `!WorkspaceManifest.IsUpToDate`.
  - Otherwise it scans **synchronously**, saving the graph and the manifest (computed before the scan).
  - It always returns the current snapshot with `Stale = knownStale || refreshing`.
  - It is thread-safe: the first load or scan is serialized with a lock, and later calls take no lock.
- **`StartBackgroundRefresh()`:**
  - It ensures a snapshot exists.
  - If the snapshot is not stale, or a refresh is already running, it returns `null`.
  - Otherwise it starts a `Task.Run` that rescans under the scan lock, swaps the snapshot atomically, and clears `refreshing` in `finally`.
- **`Rescan()`:** synchronous, under the scan lock. It swaps and returns the fresh snapshot.
- **One orchestrator per store.** The `ScanOrchestrator` is not thread-safe, so every scan runs under the same lock. Reuse one orchestrator with a `ParseCache` for the store's lifetime, so rescans are incremental.

- [ ] **Step 1: Create the project**

```bash
cd /c/code/repos/depenk
dotnet new classlib -n Depenk.Mcp -o src/Depenk.Mcp
rm src/Depenk.Mcp/Class1.cs
dotnet sln add src/Depenk.Mcp
dotnet add src/Depenk.Mcp reference src/Depenk.Query src/Depenk.Analysis
dotnet add src/Depenk.Mcp package ModelContextProtocol --version 2.2.0
dotnet add tests/Depenk.Tests reference src/Depenk.Mcp
```

Remove the generated `<TargetFramework>`, `<Nullable>` and `<ImplicitUsings>` lines, leaving no empty `PropertyGroup`.

- [ ] **Step 2: Write failing tests**

`tests/Depenk.Tests/Mcp/GraphStoreTests.cs`:

```csharp
using Depenk.Analysis;
using Depenk.Mcp;

namespace Depenk.Tests.Mcp;

public class GraphStoreTests
{
    private const string NewController = """
        using Microsoft.AspNetCore.Mvc;
        namespace Acme.Orders.Api;
        [Route("api/orders")]
        public class CancelController : ControllerBase { [HttpPost("{id}/cancel")] public Task Cancel(Guid id) => Task.CompletedTask; }
        """;

    [Fact]
    public void NoGraph_ScansSynchronouslyOnFirstUse()
    {
        using var ws = FixtureScanTests.CopyFixture();
        var store = new GraphStore(ws.Root);

        var s = store.Current();

        Assert.Equal(5, s.Graph.Repos.Count);
        Assert.False(s.Stale);
        Assert.Equal(1, store.ScanCount);
        Assert.True(File.Exists(ScanOrchestrator.GraphPath(ws.Root)));
        Assert.True(File.Exists(WorkspaceManifest.ManifestPath(ws.Root)));
        Assert.Same(s.Graph, store.Current().Graph); // cached
    }

    [Fact]
    public void UpToDateGraph_IsLoadedWithoutScanning()
    {
        using var ws = FixtureScanTests.CopyFixture();
        new GraphStore(ws.Root).Current();

        var store = new GraphStore(ws.Root);
        var s = store.Current();

        Assert.Equal((0, false, 5), (store.ScanCount, s.Stale, s.Graph.Repos.Count));
        Assert.Null(store.StartBackgroundRefresh());
    }

    [Fact]
    public async Task StaleGraph_IsServedWhileRefreshing_ThenSwapped()
    {
        using var ws = FixtureScanTests.CopyFixture();
        new GraphStore(ws.Root).Current();
        ws.File("orders/src/Orders.Api/CancelController.cs", NewController);

        var store = new GraphStore(ws.Root);
        var before = store.Current();
        Assert.True(before.Stale);
        Assert.DoesNotContain(before.Graph.Endpoints, e => e.Route == "/api/orders/{id}/cancel");

        var refresh = store.StartBackgroundRefresh();
        Assert.NotNull(refresh);
        Parallel.For(0, 200, _ =>
        {
            var snap = store.Current();
            Assert.Same(snap.Graph, snap.Index.Graph); // never a mixed snapshot
            Assert.True(snap.Graph.Endpoints.Count >= before.Graph.Endpoints.Count);
        });
        await refresh!;

        var after = store.Current();
        Assert.False(after.Stale);
        Assert.Contains(after.Graph.Endpoints, e => e.Route == "/api/orders/{id}/cancel");
        Assert.Equal(1, store.ScanCount);
        Assert.True(WorkspaceManifest.IsUpToDate(ws.Root));
    }

    [Fact]
    public void Rescan_PicksUpChangesSynchronously()
    {
        using var ws = FixtureScanTests.CopyFixture();
        var store = new GraphStore(ws.Root);
        store.Current();
        ws.File("orders/src/Orders.Api/CancelController.cs", NewController);

        var s = store.Rescan();

        Assert.Contains(s.Graph.Endpoints, e => e.Route == "/api/orders/{id}/cancel");
        Assert.Same(s.Graph, store.Current().Graph);
        Assert.Equal(2, store.ScanCount);
    }

    [Fact]
    public void CorruptGraphFile_TriggersAScan()
    {
        using var ws = FixtureScanTests.CopyFixture();
        ws.File(".depenk/graph.json", "{ not json");
        var store = new GraphStore(ws.Root);
        Assert.Equal(5, store.Current().Graph.Repos.Count);
        Assert.Equal(1, store.ScanCount);
    }
}
```

- [ ] **Step 3: Run to verify failure**

Run: `dotnet test tests/Depenk.Tests --filter GraphStoreTests`
Expected: the build FAILS with `The type or namespace name 'GraphStore' could not be found`.

- [ ] **Step 4: Implement**

`src/Depenk.Mcp/GraphStore.cs`:

```csharp
using System.Text.Json;
using Depenk.Analysis;
using Depenk.Core;
using Depenk.Core.Model;
using Depenk.Query;

namespace Depenk.Mcp;

public sealed record GraphSnapshot(DepGraph Graph, GraphIndex Index, QueryService Query, bool Stale);

/// <summary>
/// Owns the workspace graph for a long-lived host (MCP server). Loads the cached graph instantly, scans when there is
/// none, and refreshes stale graphs in the background while continuing to serve the previous snapshot.
/// </summary>
public sealed class GraphStore(string workspace, ScanOrchestrator? orchestrator = null)
{
    private readonly object _scanLock = new();
    private readonly ScanOrchestrator _orchestrator = orchestrator ?? new ScanOrchestrator(new ParseCache());
    private GraphSnapshot? _snapshot;
    private volatile bool _knownStale;
    private int _refreshing;
    private int _scanCount;

    public string Workspace { get; } = Path.GetFullPath(workspace);
    public int ScanCount => Volatile.Read(ref _scanCount);

    public GraphSnapshot Current()
    {
        var s = Volatile.Read(ref _snapshot);
        if (s is null)
        {
            lock (_scanLock)
            {
                s = _snapshot;
                if (s is null)
                {
                    if (TryLoad(out var cached))
                    {
                        _knownStale = !WorkspaceManifest.IsUpToDate(Workspace);
                        s = Wrap(cached);
                    }
                    else
                    {
                        s = ScanLocked();
                    }
                    Volatile.Write(ref _snapshot, s);
                }
            }
        }
        var stale = _knownStale || Volatile.Read(ref _refreshing) == 1;
        return s.Stale == stale ? s : s with { Stale = stale };
    }

    public Task? StartBackgroundRefresh()
    {
        Current();
        if (!_knownStale || Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0) return null;
        return Task.Run(() =>
        {
            try
            {
                lock (_scanLock) Volatile.Write(ref _snapshot, ScanLocked());
            }
            finally
            {
                Volatile.Write(ref _refreshing, 0);
            }
        });
    }

    public GraphSnapshot Rescan()
    {
        lock (_scanLock)
        {
            var s = ScanLocked();
            Volatile.Write(ref _snapshot, s);
            return s;
        }
    }

    /// <summary>Caller must hold <see cref="_scanLock"/>.</summary>
    private GraphSnapshot ScanLocked()
    {
        var manifest = WorkspaceManifest.Compute(Workspace); // before the scan: edits during it stay detectable
        var graph = _orchestrator.Scan(Workspace);
        GraphJson.Save(graph, ScanOrchestrator.GraphPath(Workspace));
        WorkspaceManifest.Save(Workspace, manifest);
        Interlocked.Increment(ref _scanCount);
        _knownStale = false;
        return Wrap(graph);
    }

    private bool TryLoad(out DepGraph graph)
    {
        graph = null!;
        var path = ScanOrchestrator.GraphPath(Workspace);
        if (!File.Exists(path)) return false;
        try
        {
            graph = GraphJson.Load(path);
            return graph.SchemaVersion == 1;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return false;
        }
    }

    private static GraphSnapshot Wrap(DepGraph graph)
    {
        var index = new GraphIndex(graph);
        return new GraphSnapshot(graph, index, new QueryService(index), false);
    }
}
```

- [ ] **Step 5: Run tests**

Run: `dotnet test tests/Depenk.Tests --filter GraphStoreTests`
Expected: PASS (5 tests). Then run `dotnet build Depenk.sln` and expect 0 warnings.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(mcp): graph store with scan-on-first-use, staleness tracking and background refresh"
```

### Task 6: MCP tools, overview resource and server registration

**Files:**
- Create: `src/Depenk.Mcp/ToolJson.cs`, `src/Depenk.Mcp/DepenkTools.cs`, `src/Depenk.Mcp/DepenkResources.cs`, `src/Depenk.Mcp/DepenkMcpServer.cs`
- Create: `tests/Depenk.Tests/TestUtil/McpHarness.cs`
- Test: `tests/Depenk.Tests/Mcp/McpServerTests.cs`

**Interfaces:**
- Consumes: `GraphStore`, `GraphSnapshot` (Task 5); `QueryService` methods (Tasks 2–3); `SourceReader` (Task 4); `QueryException`, `ITruncatable`; and `GraphJson.Options` (Plan 1).
- Produces:
  - `static class ToolJson { string Envelope<T>(string summary, bool stale, T data); string Error(QueryException e); JsonSerializerOptions Compact; }`
  - `[McpServerToolType] sealed class DepenkTools(GraphStore store)` with the **12 tools** listed below
  - `[McpServerResourceType] sealed class DepenkResources(GraphStore store)` with `depenk://overview`
  - `static class DepenkMcpServer`, which has:
    - `const string Instructions`
    - `string Version`
    - `IMcpServerBuilder AddDepenkMcpServer(this IServiceCollection services, GraphStore store)`, which registers the store, server info (name `depenk`), instructions, tools and resources, but **no transport** (callers add stdio or stream)
    - `IReadOnlyList<string> ToolNames`
  - Test helper: `McpHarness`, an `IAsyncDisposable` with:
    - `static Task<McpHarness> StartAsync(string workspace)`
    - `McpClient Client`
    - `Task<(bool IsError, string Text)> CallAsync(string tool, Dictionary<string, object?>? args = null)`
    - `Task<JsonElement> DataAsync(string tool, Dictionary<string, object?>? args = null)`, which parses the envelope and returns `data`, asserting no error

**The 12 tools.** Every parameter gets a `[Description]`. All tools are `ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false` except `rescan` (`ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false`).

| Name | Parameters | Calls | Summary format |
|---|---|---|---|
| `list_repos` | — | `ListRepos()` | `"{n} repos, {m} cross-repo links"` |
| `get_repo` | `repo` | `GetRepo` | `"{name}: {p} projects, publishes {k} package(s), {d} dependencies, {u} dependents"` |
| `find_endpoints` | `query?`, `repo?`, `verb?`, `limit?` | `FindEndpoints` | `"{shown} of {total} endpoints"` |
| `get_endpoint` | `endpoint` | `GetEndpoint` | `"{VERB} {route} in {repo}: {c} client method(s), {s} call site(s)"` |
| `get_model` | `model`, `depth=2` | `GetModel` | `"{FullName} ({kind}) with {f} fields"` |
| `find_model_usages` | `model` | `FindModelUsages` | `"{e} endpoint usage(s) across {r} repo(s)"` |
| `get_source` | `nodeId`, `context=10` | `SourceReader(store.Workspace).Read` | `"{path}:{line}"` |
| `trace` | `node`, `direction="down"`, `depth=3`, `limit?` | `Trace` | `"{n} node(s) {direction} from {root}"` |
| `impact_of_change` | `target` | `ImpactOfChange` | `"Changing {target}[.{field}] affects {r} repo(s), {e} endpoint(s), {c} call site(s)"` |
| `get_diagnostics` | `kind?`, `repo?`, `severity?`, `limit?` | `GetDiagnostics` | `"{shown} of {total} diagnostics"` |
| `how_to_call` | `endpoint` | `HowToCall` | `"{n} way(s) to call {endpointId}"` |
| `rescan` | — | `store.Rescan()` | `"Rescanned: {r} repos, {e} endpoints, {d} diagnostics"` (data: `{repos, projects, endpoints, clientMethods, callSites, models, diagnostics}` counts) |

- [ ] **Step 1: Write the harness and failing tests**

`tests/Depenk.Tests/TestUtil/McpHarness.cs`:

```csharp
using System.IO.Pipelines;
using System.Text.Json;
using Depenk.Mcp;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Depenk.Tests.TestUtil;

public sealed class McpHarness : IAsyncDisposable
{
    private readonly Pipe _c2s = new(), _s2c = new();
    private readonly CancellationTokenSource _cts = new();
    private ServiceProvider _sp = null!;
    private Task _serverTask = Task.CompletedTask;

    public McpClient Client { get; private set; } = null!;
    public GraphStore Store { get; private set; } = null!;

    public static async Task<McpHarness> StartAsync(string workspace)
    {
        var h = new McpHarness { Store = new GraphStore(workspace) };
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDepenkMcpServer(h.Store).WithStreamServerTransport(h._c2s.Reader.AsStream(), h._s2c.Writer.AsStream());
        h._sp = services.BuildServiceProvider();
        h._serverTask = h._sp.GetRequiredService<McpServer>().RunAsync(h._cts.Token);
        h.Client = await McpClient.CreateAsync(new StreamClientTransport(h._c2s.Writer.AsStream(), h._s2c.Reader.AsStream()));
        return h;
    }

    public async Task<(bool IsError, string Text)> CallAsync(string tool, Dictionary<string, object?>? args = null)
    {
        var r = await Client.CallToolAsync(tool, args ?? []);
        return (r.IsError == true, string.Join("\n", r.Content.OfType<TextContentBlock>().Select(b => b.Text)));
    }

    public async Task<JsonElement> DataAsync(string tool, Dictionary<string, object?>? args = null)
    {
        var (isError, text) = await CallAsync(tool, args);
        Assert.False(isError, text);
        using var doc = JsonDocument.Parse(text);
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("summary").GetString()));
        Assert.Equal(JsonValueKind.False, doc.RootElement.GetProperty("stale").ValueKind);
        return doc.RootElement.GetProperty("data").Clone();
    }

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync();
        await _cts.CancelAsync();
        _c2s.Writer.Complete();
        _s2c.Writer.Complete();
        try { await _serverTask; } catch (OperationCanceledException) { }
        await _sp.DisposeAsync();
        _cts.Dispose();
    }
}
```

`tests/Depenk.Tests/Mcp/McpServerTests.cs`:

```csharp
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
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Depenk.Tests --filter McpServerTests`
Expected: the build FAILS with `'IServiceCollection' does not contain a definition for 'AddDepenkMcpServer'`.

- [ ] **Step 3: Implement**

`src/Depenk.Mcp/ToolJson.cs`:

```csharp
using System.Text.Json;
using Depenk.Core;
using Depenk.Query;

namespace Depenk.Mcp;

public static class ToolJson
{
    /// <summary>GraphJson conventions (camelCase, camelCase enums, nulls omitted) without indentation.</summary>
    public static readonly JsonSerializerOptions Compact = new(GraphJson.Options) { WriteIndented = false };

    public static string Envelope<T>(string summary, bool stale, T data) =>
        JsonSerializer.Serialize(new EnvelopeBody<T>(summary, stale, data is ITruncatable t && t.Truncated, data), Compact);

    public static string Error(QueryException e) =>
        JsonSerializer.Serialize(new ErrorBody(e.Code, e.Message, e.Hint, e.Suggestions ?? []), Compact);

    private sealed record EnvelopeBody<T>(string Summary, bool Stale, bool Truncated, T Data);
    private sealed record ErrorBody(string Code, string Message, string? Hint, IReadOnlyList<string> Suggestions);
}
```

`src/Depenk.Mcp/DepenkTools.cs`:

```csharp
using System.ComponentModel;
using Depenk.Query;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Depenk.Mcp;

[McpServerToolType]
public sealed class DepenkTools(GraphStore store)
{
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
        [Description("Package id, endpoint (\"GET /api/orders/{id}\"), model name, or \"Model.Field\" (e.g. \"OrderDto.Lines\").")] string target) =>
        Run(s => s.Query.ImpactOfChange(target),
            r => $"Changing {r.Target}{(r.Field is null ? "" : "." + r.Field)} affects {r.Repos.Count} repo(s), {r.Endpoints.Count} endpoint(s), {r.CallSites.Count} call site(s)");

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
        var s = store.Rescan();
        var g = s.Graph;
        var counts = new RescanCounts(g.Repos.Count, g.Projects.Count, g.Endpoints.Count, g.ClientMethods.Count,
            g.CallSites.Count, g.Models.Count, g.Diagnostics.Count);
        return ToolJson.Envelope($"Rescanned: {counts.Repos} repos, {counts.Endpoints} endpoints, {counts.Diagnostics} diagnostics",
            false, counts);
    }

    private string Run<T>(Func<GraphSnapshot, T> query, Func<T, string> summary)
    {
        var snapshot = store.Current();
        try
        {
            var data = query(snapshot);
            return ToolJson.Envelope(summary(data), snapshot.Stale, data);
        }
        catch (QueryException e)
        {
            throw new McpException(ToolJson.Error(e));
        }
    }

    private sealed record RescanCounts(int Repos, int Projects, int Endpoints, int ClientMethods, int CallSites, int Models,
        int Diagnostics);
}
```

`src/Depenk.Mcp/DepenkResources.cs`:

```csharp
using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Depenk.Mcp;

[McpServerResourceType]
public sealed class DepenkResources(GraphStore store)
{
    [McpServerResource(UriTemplate = "depenk://overview", Name = "overview", MimeType = "text/markdown")]
    [Description("Markdown summary of the workspace: repos, service links, hotspots and diagnostic counts. Read it at the start of a session.")]
    public string Overview() => store.Current().Query.Overview();
}
```

`src/Depenk.Mcp/DepenkMcpServer.cs`:

```csharp
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Depenk.Mcp;

public static class DepenkMcpServer
{
    public const string Instructions =
        "depenk answers cross-repo questions about C# services that call each other through API client NuGet packages. " +
        "Read depenk://overview first. Before changing a controller, route, DTO/model field or client package, call impact_of_change. " +
        "To find who calls an endpoint use get_endpoint (callers) or trace with direction \"up\". To integrate with another service use how_to_call. " +
        "Ids look like repo:orders, ep:orders:GET:/api/orders/{id}, model:Orders.Client:Acme.Orders.Client.OrderDto; endpoints also accept \"VERB /route\" and models a simple name. " +
        "Every result is {summary, stale, truncated, data}; low/medium confidence links are heuristic. If stale is true a background rescan is running.";

    public static string Version =>
        typeof(DepenkMcpServer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    public static IReadOnlyList<string> ToolNames { get; } = typeof(DepenkTools).GetMethods()
        .Select(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name).OfType<string>().ToList();

    public static IMcpServerBuilder AddDepenkMcpServer(this IServiceCollection services, GraphStore store)
    {
        services.AddSingleton(store);
        return services
            .AddMcpServer(o =>
            {
                o.ServerInfo = new Implementation { Name = "depenk", Version = Version };
                o.ServerInstructions = Instructions;
            })
            .WithTools<DepenkTools>()
            .WithResources<DepenkResources>();
    }
}
```

If a ModelContextProtocol 2.2.0 member name differs from the one used above (for example the resource attribute's URI property, `WithResources<T>`, or `McpClientTool.ProtocolTool`), check the SDK's XML docs or source with `dotnet` IntelliSense or the context7 docs. Then use the 2.2.0 name, keeping the same behaviour, and record the rename in your report. The tool names, annotations, envelope and error JSON are fixed by this brief.

- [ ] **Step 4: Run tests**

Run: `dotnet test tests/Depenk.Tests --filter McpServerTests`
Expected: PASS (4 tests). Then run `dotnet test tests/Depenk.Tests --filter "Category!=Perf"`; everything should pass. Run `dotnet build Depenk.sln` and expect 0 warnings.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(mcp): 12 depenk tools, overview resource and server registration"
```

### Task 7: Workspace resolution + `depenk mcp` (stdio) + `depenk query` (in-memory client)

**Files:**
- Create: `src/Depenk.Scanning/WorkspaceResolver.cs`, `src/Depenk.Mcp/QueryRunner.cs`
- Modify: `src/depenk/depenk.csproj` (Hosting package, reference Depenk.Mcp, version 0.2.0), `src/depenk/Program.cs`
- Modify: `tests/Depenk.Tests/Depenk.Tests.csproj` (build-order reference to the CLI)
- Test: `tests/Depenk.Tests/Scanning/WorkspaceResolverTests.cs`, `tests/Depenk.Tests/Mcp/QueryRunnerTests.cs`, `tests/Depenk.Tests/Mcp/McpStdioTests.cs`

**Interfaces:**
- Consumes: `DepenkMcpServer.AddDepenkMcpServer`, `GraphStore` (Tasks 5–6); `ScanOrchestrator`, `WorkspaceManifest` (Plan 1).
- Produces:
  - `static class WorkspaceResolver`:
    - `const string EnvVar = "DEPENK_WORKSPACE"`
    - `string Resolve(string? explicitPath, string currentDirectory, Func<string, string?> getEnv)`
  - `static class QueryRunner`:
    - `Task<int> RunAsync(string workspace, string tool, string? json, TextWriter stdout, TextWriter stderr, CancellationToken ct = default)`
    - exit codes: `0` ok, `1` tool error or unknown tool, `2` `--json` is not a JSON object
    - tool `tools` lists `name<TAB>description`
  - CLI:
    - `depenk scan [--workspace] [--force]` (unchanged behaviour, new resolution)
    - `depenk mcp [--workspace]`
    - `depenk query <tool> [--json <object>] [--workspace]`
    - shared exit code `3` = workspace folder not found
    - `public partial class Program { }` so tests can locate the built `depenk.dll`

**Workspace resolution (`WorkspaceResolver.Resolve`), first match wins:**
1. **Explicit `--workspace`:** use it, full path.
2. **`DEPENK_WORKSPACE` env var**, if non-blank: use it, full path.
3. **Current folder is marked:** if `cwd` contains `depenk.yml` or `.depenk/`, use `cwd`.
4. **Inside one repo of a multi-repo folder:** if `cwd` is a git repo (it has a `.git` dir or file) and its parent contains `depenk.yml` or `.depenk/`, or at least 2 git repos (counting `cwd`), use the **parent**. This is the case where Claude Code is opened inside one service repo whose siblings sit next to it.
5. **Otherwise:** use `cwd`.

Unreadable directories while counting siblings count as zero (no exception). `scan` and `query` print `Workspace: <path>` to **stderr**. `mcp` prints nothing to stdout except protocol.

- [ ] **Step 1: Write failing tests**

`tests/Depenk.Tests/Scanning/WorkspaceResolverTests.cs`:

```csharp
using Depenk.Scanning;
using Depenk.Tests.TestUtil;

namespace Depenk.Tests.Scanning;

public class WorkspaceResolverTests
{
    private static string? NoEnv(string _) => null;

    [Fact]
    public void ExplicitPath_Wins()
    {
        using var ws = new TempWorkspace().Repo("a");
        Assert.Equal(Path.Combine(ws.Root, "a"),
            WorkspaceResolver.Resolve(Path.Combine(ws.Root, "a"), ws.Root, _ => "/elsewhere"));
    }

    [Fact]
    public void EnvVar_WinsOverCurrentDirectory()
    {
        using var ws = new TempWorkspace().Repo("a");
        Assert.Equal(ws.Root, WorkspaceResolver.Resolve(null, Path.Combine(ws.Root, "a"),
            k => k == WorkspaceResolver.EnvVar ? ws.Root : null));
    }

    [Fact]
    public void RepoWithSiblingRepos_ResolvesToParent()
    {
        using var ws = new TempWorkspace().Repo("orders").Repo("billing");
        Assert.Equal(ws.Root, WorkspaceResolver.Resolve(null, Path.Combine(ws.Root, "orders"), NoEnv));
    }

    [Fact]
    public void LoneRepo_StaysPut_UnlessParentIsMarked()
    {
        using var ws = new TempWorkspace().Repo("orders").File("notes/readme.md", "x");
        var orders = Path.Combine(ws.Root, "orders");
        Assert.Equal(orders, WorkspaceResolver.Resolve(null, orders, NoEnv));

        ws.File("depenk.yml", "repos: {}\n");
        Assert.Equal(ws.Root, WorkspaceResolver.Resolve(null, orders, NoEnv));
    }

    [Fact]
    public void MarkedCurrentDirectory_StaysPut()
    {
        using var ws = new TempWorkspace().Repo("orders").Repo("billing").File("orders/depenk.yml", "repos: {}\n");
        var orders = Path.Combine(ws.Root, "orders");
        Assert.Equal(orders, WorkspaceResolver.Resolve(null, orders, NoEnv));
    }
}
```

`tests/Depenk.Tests/Mcp/QueryRunnerTests.cs`:

```csharp
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
```

`tests/Depenk.Tests/Mcp/McpStdioTests.cs` (end-to-end against the real executable, which also proves stdout carries only protocol):

```csharp
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

    private static Task<McpClient> Connect(IList<string> args, Dictionary<string, string?>? env = null) =>
        McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "depenk", Command = "dotnet", Arguments = [CliDll(), .. args], EnvironmentVariables = env,
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
}
```

In `tests/Depenk.Tests/Depenk.Tests.csproj`, add a build-order-only reference so `dotnet test` builds the CLI first:

```xml
<ItemGroup>
  <ProjectReference Include="..\..\src\depenk\depenk.csproj" ReferenceOutputAssembly="false" />
</ItemGroup>
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Depenk.Tests --filter "WorkspaceResolverTests|QueryRunnerTests|McpStdioTests"`
Expected: the build FAILS with `The name 'WorkspaceResolver' does not exist in the current context`.

- [ ] **Step 3: Implement WorkspaceResolver**

`src/Depenk.Scanning/WorkspaceResolver.cs`:

```csharp
namespace Depenk.Scanning;

public static class WorkspaceResolver
{
    public const string EnvVar = "DEPENK_WORKSPACE";

    public static string Resolve(string? explicitPath, string currentDirectory, Func<string, string?> getEnv)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath)) return Path.GetFullPath(explicitPath);
        var fromEnv = getEnv(EnvVar);
        if (!string.IsNullOrWhiteSpace(fromEnv)) return Path.GetFullPath(fromEnv);

        var cwd = Path.GetFullPath(currentDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (IsMarked(cwd)) return cwd;
        var parent = Path.GetDirectoryName(cwd);
        if (parent is not null && IsGitRepo(cwd) && (IsMarked(parent) || CountGitRepos(parent) >= 2)) return parent;
        return cwd;
    }

    private static bool IsMarked(string dir) =>
        File.Exists(Path.Combine(dir, "depenk.yml")) || Directory.Exists(Path.Combine(dir, ".depenk"));

    private static bool IsGitRepo(string dir) =>
        Directory.Exists(Path.Combine(dir, ".git")) || File.Exists(Path.Combine(dir, ".git"));

    private static int CountGitRepos(string dir)
    {
        try { return Directory.EnumerateDirectories(dir).Count(IsGitRepo); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0; }
    }
}
```

- [ ] **Step 4: Implement QueryRunner**

`src/Depenk.Mcp/QueryRunner.cs`:

```csharp
using System.IO.Pipelines;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Depenk.Mcp;

/// <summary>`depenk query`: hosts the MCP server in-process and calls one tool through a real MCP client.</summary>
public static class QueryRunner
{
    public static async Task<int> RunAsync(string workspace, string tool, string? json, TextWriter stdout, TextWriter stderr,
        CancellationToken ct = default)
    {
        Dictionary<string, object?> args;
        try
        {
            args = string.IsNullOrWhiteSpace(json)
                ? []
                : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!.ToDictionary(kv => kv.Key, kv => (object?)kv.Value);
        }
        catch (JsonException ex)
        {
            await stderr.WriteLineAsync($"depenk: --json must be a JSON object: {ex.Message}");
            return 2;
        }

        Pipe c2s = new(), s2c = new();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDepenkMcpServer(new GraphStore(workspace)).WithStreamServerTransport(c2s.Reader.AsStream(), s2c.Writer.AsStream());
        await using var sp = services.BuildServiceProvider();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var serverTask = sp.GetRequiredService<McpServer>().RunAsync(cts.Token);
        try
        {
            await using var client = await McpClient.CreateAsync(
                new StreamClientTransport(c2s.Writer.AsStream(), s2c.Reader.AsStream()), cancellationToken: ct);
            if (tool == "tools")
            {
                foreach (var t in (await client.ListToolsAsync(cancellationToken: ct)).OrderBy(t => t.Name, StringComparer.Ordinal))
                    await stdout.WriteLineAsync($"{t.Name}\t{t.Description}");
                return 0;
            }
            var result = await client.CallToolAsync(tool, args, cancellationToken: ct);
            var text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(b => b.Text));
            if (result.IsError == true)
            {
                await stderr.WriteLineAsync(text);
                return 1;
            }
            await stdout.WriteLineAsync(text);
            return 0;
        }
        catch (McpException ex)
        {
            await stderr.WriteLineAsync($"depenk: {ex.Message} (run `depenk query tools` to list tools)");
            return 1;
        }
        finally
        {
            await cts.CancelAsync();
            c2s.Writer.Complete();
            s2c.Writer.Complete();
            try { await serverTask; } catch (OperationCanceledException) { }
        }
    }
}
```

- [ ] **Step 5: Wire up the CLI**

```bash
dotnet add src/depenk package Microsoft.Extensions.Hosting --version 10.0.12
dotnet add src/depenk reference src/Depenk.Mcp
```

In `src/depenk/depenk.csproj` set `<Version>0.2.0</Version>`.

Replace `src/depenk/Program.cs` with:

```csharp
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

    var builder = Host.CreateApplicationBuilder();
    builder.Logging.ClearProviders();
    builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace); // stdout is the protocol channel
    builder.Logging.SetMinimumLevel(LogLevel.Warning);
    var store = new GraphStore(ws);
    builder.Services.AddDepenkMcpServer(store).WithStdioServerTransport();
    using var host = builder.Build();
    _ = Task.Run(() => { store.StartBackgroundRefresh(); }); // first scan / staleness refresh off the handshake path
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
```

- [ ] **Step 6: Run tests**

Run: `dotnet test tests/Depenk.Tests --filter "WorkspaceResolverTests|QueryRunnerTests|McpStdioTests"`
Expected: PASS (11 tests). Then run the full fast suite with `dotnet test tests/Depenk.Tests --filter "Category!=Perf"` and confirm `dotnet build Depenk.sln` has 0 warnings.

Manual smoke test, recording the output in your report:

```bash
dotnet run --project src/depenk -- query tools --workspace /c/code/repos
dotnet run --project src/depenk -- query list_repos --workspace /c/code/repos
```

Expected: the first prints 12 tab-separated lines. The second prints one JSON envelope whose `data.repos` lists the repos under `/c/code/repos`, and exits 0.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat(cli): depenk mcp (stdio) and depenk query, with workspace auto-detection"
```

### Task 8: Shared query conformance cases (C# runner now, TypeScript in Plan 3)

**Files:**
- Create: `tests/query-cases/README.md`, `tests/query-cases/neighbors-billing-down.json`, `neighbors-orders-up.json`, `neighbors-endpoint-up.json`, `neighbors-orderdto-down.json`, `trace-billing-down-2.json`, `search-getorderasync.json`
- Test: `tests/Depenk.Tests/QueryConformanceTests.cs`

**Interfaces:**
- Consumes: `GraphIndex` (`DependenciesOf`, `DependentsOf`, `Suggest`), `QueryService.Trace` (Tasks 1–3); `GraphJson.Deserialize` (Plan 1).
- Produces: a language-neutral case format that Plan 3's TypeScript UI queries must also pass.

**Case format** (one JSON object per file):

```json
{
  "name": "human readable",
  "graph": "../Depenk.Tests/Snapshots/FixtureScanTests.Snapshot.verified.json",
  "op": "neighbors | trace | search",
  "args": { },
  "expected": [ ]
}
```

`graph` is relative to the case file. The ops are:
- **`neighbors`** — args `{id, direction: "down"|"up"}`. Expected: the distinct far-end IDs of `DependenciesOf` (down) or `DependentsOf` (up), sorted ordinally.
- **`trace`** — args `{id, direction, depth}`. Expected: `"{id}@{depth}"` for each `TraceStep`, **in result order** (BFS, ordinal by far-end ID).
- **`search`** — args `{query, limit}`. Expected: `Suggest(query, limit)` **in rank order**.

- [ ] **Step 1: Write the cases**

`tests/query-cases/neighbors-billing-down.json`:

```json
{
  "name": "billing depends on customers and orders",
  "graph": "../Depenk.Tests/Snapshots/FixtureScanTests.Snapshot.verified.json",
  "op": "neighbors",
  "args": { "id": "repo:billing", "direction": "down" },
  "expected": ["repo:customers", "repo:orders"]
}
```

`tests/query-cases/neighbors-orders-up.json`:

```json
{
  "name": "orders is depended on by billing and gateway",
  "graph": "../Depenk.Tests/Snapshots/FixtureScanTests.Snapshot.verified.json",
  "op": "neighbors",
  "args": { "id": "repo:orders", "direction": "up" },
  "expected": ["repo:billing", "repo:gateway"]
}
```

`tests/query-cases/neighbors-endpoint-up.json`:

```json
{
  "name": "both the interface and the implementation target GET /api/orders/{id}",
  "graph": "../Depenk.Tests/Snapshots/FixtureScanTests.Snapshot.verified.json",
  "op": "neighbors",
  "args": { "id": "ep:orders:GET:/api/orders/{id}", "direction": "up" },
  "expected": ["cm:Orders.Client:IOrdersClient.GetOrderAsync", "cm:Orders.Client:OrdersClient.GetOrderAsync"]
}
```

`tests/query-cases/neighbors-orderdto-down.json`:

```json
{
  "name": "OrderDto's nested model types, including one from another repo",
  "graph": "../Depenk.Tests/Snapshots/FixtureScanTests.Snapshot.verified.json",
  "op": "neighbors",
  "args": { "id": "model:Orders.Client:Acme.Orders.Client.OrderDto", "direction": "down" },
  "expected": [
    "model:Customers.Client:Acme.Customers.Client.CustomerDto",
    "model:Orders.Client:Acme.Orders.Client.OrderLineDto",
    "model:Orders.Client:Acme.Orders.Client.OrderStatus"
  ]
}
```

`tests/query-cases/trace-billing-down-2.json`:

```json
{
  "name": "two hops of repo dependencies from billing",
  "graph": "../Depenk.Tests/Snapshots/FixtureScanTests.Snapshot.verified.json",
  "op": "trace",
  "args": { "id": "repo:billing", "direction": "down", "depth": 2 },
  "expected": ["repo:customers@1", "repo:orders@1", "repo:shared@2"]
}
```

`tests/query-cases/search-getorderasync.json`:

```json
{
  "name": "substring matches rank first, then by edit distance to the label",
  "graph": "../Depenk.Tests/Snapshots/FixtureScanTests.Snapshot.verified.json",
  "op": "search",
  "args": { "query": "GetOrderAsync", "limit": 2 },
  "expected": ["cm:Orders.Client:OrdersClient.GetOrderAsync", "cm:Orders.Client:IOrdersClient.GetOrderAsync"]
}
```

`tests/query-cases/README.md`:

```markdown
# Query conformance cases

Language-neutral test cases for depenk's graph queries. The C# query layer (`Depenk.Query`) runs them in
`tests/Depenk.Tests/QueryConformanceTests.cs`; the browser UI's TypeScript queries (Plan 3) must pass the same files.

Each file: `{ name, graph, op, args, expected }`, `graph` relative to the case file (it may start with a UTF-8 BOM — strip it before parsing).

Semantics every implementation must share:

- **Normalized adjacency.** Every edge is a dependency hop *dependent → dependency* with its original direction,
  except `produces` (project → package), which is flipped to package → project.
- **neighbors** — distinct far-end ids of the node's dependencies (`down`) or dependents (`up`), sorted ordinally.
- **trace** — breadth-first from `id`, at most `depth` hops, each node visited once; at each node visit hops in ordinal
  order of the far-end id. Output `"{id}@{depth}"` in visit order. `both` = all of `down`, then all of `up`.
- **search** — rank all nodes by (1) query is a case-insensitive substring of the id or label, (2) the smaller
  Levenshtein distance of the lowercased query to the lowercased id or label, (3) ordinal id. Labels: repo/project name,
  PackageId, `"VERB /route"`, `"Type.Method"`, the call site's containing member, the model's full name.
```

- [ ] **Step 2: Write the runner**

`tests/Depenk.Tests/QueryConformanceTests.cs`:

```csharp
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
```

- [ ] **Step 3: Run tests**

Run: `dotnet test tests/Depenk.Tests --filter QueryConformanceTests`
Expected: PASS (6 cases).

If a case fails, print the actual list and check it against the snapshot JSON by hand. Change an `expected` value only if the snapshot proves the query is right and the case was wrong, and note the change in your report. The query semantics must match this README exactly.

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "test(query): shared conformance cases for neighbors, trace and search"
```

---

### Task 9: JSON Schema for `depenk.yml`

**Files:**
- Create: `schemas/depenk.schema.json`
- Modify: `README.md` (add the `yaml-language-server` schema line to the config example)
- Test: `tests/Depenk.Tests/Scanning/ConfigSchemaTests.cs`

**Interfaces:**
- Consumes: `DepenkConfig` and nested option classes (Plan 1).
- Produces: `schemas/depenk.schema.json`, served at `https://raw.githubusercontent.com/kgkylegilhooly/depenk/master/schemas/depenk.schema.json`.

- [ ] **Step 1: Write the failing test**

`tests/Depenk.Tests/Scanning/ConfigSchemaTests.cs`:

```csharp
using System.Reflection;
using System.Text.Json;
using Depenk.Scanning.Config;

namespace Depenk.Tests.Scanning;

/// <summary>The published schema must describe exactly the properties DepenkConfig binds — no more, no fewer.</summary>
public class ConfigSchemaTests
{
    private static JsonElement Schema()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Depenk.sln"))) dir = dir.Parent;
        return JsonDocument.Parse(File.ReadAllText(Path.Combine(dir!.FullName, "schemas", "depenk.schema.json"))).RootElement;
    }

    private static string Camel(string s) => char.ToLowerInvariant(s[0]) + s[1..];

    private static string[] PropertyNames(Type t) =>
        t.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => Camel(p.Name)).Order().ToArray();

    private static string[] SchemaNames(JsonElement obj) =>
        obj.GetProperty("properties").EnumerateObject().Select(p => p.Name).Order().ToArray();

    [Fact]
    public void TopLevel_MatchesDepenkConfig()
    {
        var s = Schema();
        Assert.Equal("https://json-schema.org/draft/2020-12/schema", s.GetProperty("$schema").GetString());
        Assert.False(s.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(PropertyNames(typeof(DepenkConfig)), SchemaNames(s));
    }

    [Theory]
    [InlineData("repos", typeof(RepoFilter))]
    [InlineData("projects", typeof(ProjectOptions))]
    [InlineData("packages", typeof(PackageOptions))]
    [InlineData("routes", typeof(RouteOptions))]
    public void Sections_MatchOptionClasses(string section, Type type)
    {
        var obj = Schema().GetProperty("properties").GetProperty(section);
        Assert.False(obj.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(PropertyNames(type), SchemaNames(obj));
    }

    [Fact]
    public void HttpWrapperItems_MatchHttpWrapperConfig()
    {
        var items = Schema().GetProperty("properties").GetProperty("httpWrappers").GetProperty("items");
        Assert.Equal(PropertyNames(typeof(HttpWrapperConfig)), SchemaNames(items));
        Assert.Equal(["methods", "type"], items.GetProperty("required").EnumerateArray().Select(e => e.GetString()!).Order());
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Depenk.Tests --filter ConfigSchemaTests`
Expected: FAIL with `FileNotFoundException` for `schemas/depenk.schema.json`.

- [ ] **Step 3: Write the schema**

`schemas/depenk.schema.json`:

```json
{
  "$schema": "https://json-schema.org/draft/2020-12/schema",
  "$id": "https://raw.githubusercontent.com/kgkylegilhooly/depenk/master/schemas/depenk.schema.json",
  "title": "depenk.yml",
  "description": "Optional configuration for depenk, placed in the workspace root (the folder containing your repo clones).",
  "type": "object",
  "additionalProperties": false,
  "properties": {
    "repos": {
      "description": "Which folders are scanned as repos.",
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "paths": { "description": "Explicit repo folders (relative to the workspace or absolute). When set, child-folder discovery is skipped.", "type": "array", "items": { "type": "string" } },
        "include": { "description": "Glob patterns on repo names to include (default [\"*\"]).", "type": "array", "items": { "type": "string" } },
        "exclude": { "description": "Glob patterns on repo names to exclude.", "type": "array", "items": { "type": "string" } }
      }
    },
    "projects": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "kindOverrides": {
          "description": "Force a project's kind when the heuristics are wrong.",
          "type": "object",
          "additionalProperties": { "enum": ["Api", "Client", "Library", "Test", "Other", "api", "client", "library", "test", "other"] }
        },
        "ignore": { "description": "Glob patterns on project names to skip entirely.", "type": "array", "items": { "type": "string" } }
      }
    },
    "packages": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "producers": {
          "description": "PackageId → repo name that produces it, to settle ambiguous producers.",
          "type": "object",
          "additionalProperties": { "type": "string" }
        }
      }
    },
    "httpWrappers": {
      "description": "Teach depenk your in-house HTTP wrapper so its calls are linked with high confidence.",
      "type": "array",
      "items": {
        "type": "object",
        "additionalProperties": false,
        "required": ["type", "methods"],
        "properties": {
          "type": { "description": "Glob on the wrapper's declared type name, e.g. \"*.IApiHttpClient\".", "type": "string" },
          "methods": {
            "description": "Method-name glob → HTTP verb.",
            "type": "object",
            "additionalProperties": { "enum": ["GET", "POST", "PUT", "DELETE", "PATCH", "HEAD", "get", "post", "put", "delete", "patch", "head"] }
          },
          "routeArgument": { "description": "Zero-based index of the argument holding the route.", "type": "integer", "minimum": 0 }
        }
      }
    },
    "routes": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "prefixes": {
          "description": "Client project name → route prefix its base address adds (e.g. \"/api\").",
          "type": "object",
          "additionalProperties": { "type": "string" }
        }
      }
    }
  }
}
```

In `README.md`, make the first line inside the ```` ```yaml ```` block of the **Configuration** section:

```yaml
# yaml-language-server: $schema=https://raw.githubusercontent.com/kgkylegilhooly/depenk/master/schemas/depenk.schema.json
```

Under the block add: "Editors with the YAML language server (VS Code's Red Hat YAML extension, JetBrains IDEs) will autocomplete and validate `depenk.yml` from that line."

- [ ] **Step 4: Run tests**

Run: `dotnet test tests/Depenk.Tests --filter ConfigSchemaTests`
Expected: PASS (6 tests: 2 facts plus 4 theory cases).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: JSON Schema for depenk.yml, kept in sync with DepenkConfig by tests"
```

---

### Task 10: Claude Code skill, plugin + marketplace manifests, README

**Files:**
- Create: `skills/depenk/SKILL.md`, `.claude-plugin/plugin.json`, `.claude-plugin/marketplace.json`, `.mcp.json`
- Modify: `README.md` (Claude Code / MCP section, `query` command, workspace detection, roadmap tick)
- Test: `tests/Depenk.Tests/PackagingTests.cs`

**Interfaces:**
- Consumes: `DepenkMcpServer.ToolNames` (Task 6).
- Produces: a repo that works as a Claude Code plugin marketplace. Users run `/plugin marketplace add kgkylegilhooly/depenk` then `/plugin install depenk@depenk`, which gives them the skill plus the `depenk` MCP server, launched as `depenk mcp`. That requires the `depenk` global tool on PATH.

- [ ] **Step 1: Write the failing test**

`tests/Depenk.Tests/PackagingTests.cs`:

```csharp
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
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Depenk.Tests --filter PackagingTests`
Expected: FAIL with `FileNotFoundException` / `DirectoryNotFoundException` for `.claude-plugin/plugin.json`.

- [ ] **Step 3: Write the files**

`.claude-plugin/plugin.json`:

```json
{
  "name": "depenk",
  "version": "0.2.0",
  "description": "Cross-repo dependency graph for C# services connected by API client NuGet packages: impact analysis, endpoint/model contracts and callers via MCP.",
  "author": { "name": "depenk contributors" },
  "homepage": "https://github.com/kgkylegilhooly/depenk",
  "repository": "https://github.com/kgkylegilhooly/depenk",
  "license": "MIT",
  "keywords": ["csharp", "dotnet", "dependencies", "microservices", "mcp", "impact-analysis"]
}
```

`.claude-plugin/marketplace.json`:

```json
{
  "name": "depenk",
  "owner": { "name": "depenk contributors" },
  "plugins": [
    {
      "name": "depenk",
      "source": "./",
      "description": "Cross-repo C# dependency explorer: skill + MCP server (requires the depenk .NET tool on PATH)."
    }
  ]
}
```

`.mcp.json`:

```json
{
  "mcpServers": {
    "depenk": {
      "command": "depenk",
      "args": ["mcp"]
    }
  }
}
```

`skills/depenk/SKILL.md`:

```markdown
---
name: depenk
description: Use when working in a C# service that talks to other services through API client NuGet packages — before changing a controller, route, DTO/model field or *.Client package, when asked who calls an endpoint or what depends on a service, or when integrating with another service. Uses the depenk MCP server's cross-repo dependency graph.
---

# depenk: cross-repo impact for C# services

depenk has already mapped which service calls which (through client NuGet packages), down to
call site → client method → HTTP endpoint → request/response models → fields, across every repo in the workspace.
Ask it instead of grepping sibling repos.

## At the start of a session

Read the `depenk://overview` resource: repos, service links, hotspots and diagnostics in one page.

## Before you change things

| You are about to… | Call |
|---|---|
| Change a controller action, route or verb | `impact_of_change` with `"VERB /route"` |
| Rename/remove/retype a DTO field | `impact_of_change` with `"Model.Field"` (e.g. `"OrderDto.Lines"`) |
| Change a model or a `*.Client` package | `impact_of_change` with the model name or package id |

Report every affected repo and call site to the user before editing. `low`/`medium` confidence means the link was
inferred heuristically — verify those with `get_source`.

## Answering questions

- Who calls this endpoint? → `get_endpoint` (its `callers`), or `trace` with `direction: "up"`.
- What does this service depend on? → `get_repo`, or `trace` with `direction: "down"`.
- What does this model look like, including nested types from other repos? → `get_model`.
- Where is this model used? → `find_model_usages`.
- Find an endpoint by route or handler text → `find_endpoints`.
- Show me the code in another repo → `get_source` with any node id from a result.
- Dead endpoints, version drift, cycles, ambiguous links → `get_diagnostics`.
- Everything at a glance → `list_repos`.

## Integrating with another service

Call `how_to_call` with the endpoint: it returns the client package and version to reference, the client
interface/method signature to inject, and the request/response models. Prefer the interface it lists first.

## Reading results

- Every tool returns `{summary, stale, truncated, data}`. Lead with `summary`.
- `truncated: true` → narrow the query (`repo`, `verb`, `query`, `limit`).
- `stale: true` → a background rescan is running; results may lag recent edits. After you edit code, call `rescan`.
- Errors carry `code` (`not_found`, `ambiguous`, …) and `suggestions` — retry with a suggested id.
- Ids: `repo:orders`, `ep:orders:GET:/api/orders/{id}`, `cm:Orders.Client:IOrdersClient.GetOrderAsync`,
  `model:Orders.Client:Acme.Orders.Client.OrderDto`. Endpoints also accept `"GET /api/orders/{id}"`, models a simple name.

## If the depenk tools are missing

The MCP server runs the `depenk` .NET global tool. Install it (see https://github.com/kgkylegilhooly/depenk#getting-started),
then restart Claude Code. Without MCP, the same tools work from the shell: `depenk query <tool> --json '{...}'`.
```

- [ ] **Step 4: Update the README**

In `README.md`:

1. Replace the status line with: `> **Status: early (v0.2).** Scanning engine and MCP server are done; the interactive diagram and change history are on the [roadmap](#roadmap).`

2. Add a section **"Use it from Claude Code (MCP)"** after "Getting started", containing:

   - As a plugin, which gives you the skill plus the MCP server:
     ```
     /plugin marketplace add kgkylegilhooly/depenk
     /plugin install depenk@depenk
     ```
   - Or register just the MCP server: `claude mcp add depenk -- depenk mcp --workspace ~/code/repos`.
   - Then add this table of the 12 tools, followed by the `depenk://overview` resource:

     | Tool | Answers |
     |---|---|
     | `list_repos` | what's in the workspace and how repos connect |
     | `get_repo` | a repo's projects, packages and dependents |
     | `find_endpoints` | endpoints by route or handler text |
     | `get_endpoint` | an endpoint's contract, client methods and callers |
     | `get_model` | a model's field tree across repos |
     | `find_model_usages` | where a model travels |
     | `trace` | dependencies up or down from any node |
     | `impact_of_change` | what breaks if a package, endpoint, model or field changes |
     | `get_diagnostics` | drift, cycles, unused, ambiguous |
     | `how_to_call` | which package and method to use |
     | `get_source` | a code snippet from any repo |
     | `rescan` | refresh the graph |

   - Add one line on the envelope `{summary, stale, truncated, data}`, and that errors carry `code` + `suggestions`.
   - **Workspace detection**, in this order: `--workspace`, `$DEPENK_WORKSPACE`, a folder with `depenk.yml` / `.depenk/`, and the parent of the current repo when it has sibling repos.
   - **Without MCP:** `depenk query tools`, and `depenk query impact_of_change --json '{"target":"OrderDto.Lines"}'`.

3. Update the exit-code line to: `0` success · `1` unexpected or tool error · `2` invalid `depenk.yml` or `--json` · `3` workspace not found.

4. Under Roadmap, tick `- [x] **MCP server + Claude Code skill**` and note: "`compare_snapshots` and `check_contract_changes` arrive with History; `export_diagram`/`open_diagram` with the diagram."

- [ ] **Step 5: Run tests and the full suite**

Run: `dotnet test tests/Depenk.Tests --filter PackagingTests`, then `dotnet test tests/Depenk.Tests` (full suite, including perf).
Expected: everything passes. Run `dotnet build Depenk.sln` and expect 0 warnings.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat: Claude Code skill, plugin and marketplace manifests; README for MCP usage"
```

---

## Spec Coverage (Plan 2 scope)

| Spec section | Task(s) |
|---|---|
| §5 query layer: lookup/search/fuzzy, neighbors, trace, impact (package/endpoint/model/field), lowest confidence along paths | 1, 2, 3 |
| §5 parity: shared query conformance cases | 8 (C# side; TS runner in Plan 3) |
| §6 transport stdio, no Docker; startup loads cache, background incremental rescan, `stale: true` | 5, 7 |
| §6 compact JSON envelope + one-line summary, capped lists with `truncated` | 2, 6 |
| §6 structured errors `{code, message, hint, suggestions}` | 1, 6 |
| §6 tools: list_repos, get_repo, find_endpoints, get_endpoint, get_model, find_model_usages, get_source, trace, impact_of_change, get_diagnostics, how_to_call, rescan; resource depenk://overview | 2, 3, 4, 6 |
| §7 `depenk mcp`, `claude mcp add`, plugin with skill, `.mcp.json`, `depenk query` CLI | 7, 10 |
| §2 JSON Schema for depenk.yml | 9 |
| §9 get_source reads only inside the workspace | 4 |

**Deferred, on purpose:**
- `export_diagram` and `open_diagram` (Plan 3, which needs the UI)
- `compare_snapshots` and `check_contract_changes` (Plan 4, History)
- NuGet `McpServer` package type, `dnx`, and the container image (need the .NET 10 SDK, or later)
- `rescan(repos?)` filtering (scans are already incremental)
- the TypeScript conformance runner (Plan 3)

## Execution Notes

- Fast suite: `dotnet test tests/Depenk.Tests --filter "Category!=Perf"`. The full suite includes the 50-repo perf test.
- The MCP SDK API names in this plan follow ModelContextProtocol 2.x docs. If 2.2.0 differs (attribute property names, builder extension names), keep the behaviour and the tool contract fixed and record the rename in the task report.
- Never write to stdout from code reachable in `depenk mcp` (no `Console.WriteLine` in Query/Mcp). `McpStdioTests` would catch a stray write as a protocol failure.
