# depenk Plan 1 — Graph Engine Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `depenk scan --workspace <dir>` reads a folder of local C# repo clones without building them and writes `<dir>/.depenk/graph.json`. The graph contains repos, projects, packages, endpoints, client methods, call sites, models, edges and diagnostics.

**Architecture:** Three class libraries plus a CLI:
- `Depenk.Core`: graph model and JSON
- `Depenk.Scanning`: repo discovery, config and MSBuild XML
- `Depenk.Analysis`: syntax-only Roslyn finders and the linker
- `depenk` (CLI): runs `ScanOrchestrator`

Each analyzer is a small class behind an interface, fed parsed `SyntaxTree`s. Nothing restores or compiles the scanned repos.

**Tech Stack:** .NET 9 (`net9.0`), C# 13, Microsoft.CodeAnalysis.CSharp 4.12, System.Text.Json, YamlDotNet 16, System.CommandLine 2.0-beta4, xUnit 2.9, Verify.Xunit 28.

**Spec:** `docs/superpowers/specs/2026-09-30-depenk-design.md` (this plan covers §2, §3.1 steps 1–10, §4, §9 scan-side items, §10 fixture/golden/unit/perf tests). Later plans: 2 = Query + MCP + skill (§5–7), 3 = Frontend + export/serve (§8), 4 = History (snapshots, compare, contract changes).

## Global Constraints

- Target framework `net9.0` for every project; `<Nullable>enable</Nullable>`, `<ImplicitUsings>enable</ImplicitUsings>`, `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`.
- Never invoke MSBuild, `dotnet restore` or `dotnet build` on scanned repos. Read XML and parse syntax only.
- Graph JSON: camelCase property names, enums as camelCase strings, `schemaVersion: 1`.
- Node ID formats (exact): `repo:{repo}`, `proj:{repo}/{ProjectName}`, `pkg:{PackageId}`, `ep:{repo}:{VERB}:{route}`, `cm:{ProjectName}:{Type}.{Method}`, `cs:{repo}/{ProjectName}:{Type}.{Member}:{line}`, `model:{ProjectName}:{FullName}`.
- All paths stored in the graph are **relative to the workspace**, using `/` separators.
- Confidence values: `low`, `medium`, `high`, `certain`.
- A partial result beats a failed scan: any per-file or per-project exception becomes a `parseError` diagnostic, and scanning continues.
- Ambiguity is never settled silently: keep every candidate, mark it `low`, and emit a diagnostic.
- Skip directories named `bin`, `obj`, `node_modules`, `.git`, `.depenk` everywhere.
- License MIT; the repo root has `LICENSE` and `README.md`.

## Review Focus

1. **Malformed `.csproj` XML, or a `.cs` file that doesn't parse cleanly:** the scan still completes, and a `parseError` diagnostic names the file (tested in Tasks 3, 4 and 12).
2. **The same `PackageId` produced in two repos:** both producers are kept, and an `ambiguousProducer` diagnostic is emitted. It must not throw on a duplicate dictionary key (tested in Task 4).
3. **A client route with a query string or absolute URL** (`$"api/orders?page={p}"`, `"https://x/api/orders"`): the query string and scheme/host are stripped before matching, so it still links (tested in Task 5).
4. **`$(Prop)` versions defined in no props file:** recorded as `unresolved($(Prop))` with an `unresolvedVersion` diagnostic, and the version is not left blank (tested in Task 3).
5. **Windows paths:** IDs and `location.path` never contain `\` or absolute drive paths, so the graph is the same on every OS (tested in Task 12's portability test and Task 2).

---

## File Structure

```
depenk/
├─ Depenk.sln
├─ Directory.Build.props                 # shared TFM / nullable / warnings
├─ LICENSE, README.md
├─ src/
│  ├─ Depenk.Core/
│  │  ├─ Model/Enums.cs                  # Confidence, ProjectKind, ModelKind, EdgeKind
│  │  ├─ Model/Nodes.cs                  # node records
│  │  ├─ Model/Edge.cs, Diagnostic.cs
│  │  ├─ Model/DepGraph.cs               # container + lookup helpers
│  │  ├─ Ids.cs                          # ID builders
│  │  └─ GraphJson.cs                    # serializer options, Save/Load
│  ├─ Depenk.Scanning/
│  │  ├─ Config/DepenkConfig.cs, ConfigLoader.cs, Glob.cs
│  │  ├─ RepoDiscovery.cs                # finds repos + git HEAD
│  │  ├─ PathUtil.cs                     # workspace-relative '/' paths
│  │  ├─ ProjectFile.cs                  # parsed csproj DTO
│  │  ├─ PropsResolver.cs                # Directory.Build.props / Packages.props chain
│  │  ├─ ProjectParser.cs                # csproj → ProjectFile
│  │  ├─ ProjectClassifier.cs
│  │  └─ PackageGraphBuilder.cs          # projects/packages/references/produces/dependsOn
│  ├─ Depenk.Analysis/
│  │  ├─ SourceSet.cs                    # parsed trees per project (SourceDoc + hash)
│  │  ├─ SyntaxHelpers.cs                # attribute names, string/const evaluation, lines
│  │  ├─ DeclaredTypes.cs                # declared type of a receiver expression
│  │  ├─ ParseCache.cs                   # hash-keyed SyntaxTree cache (incremental)
│  │  ├─ WorkspaceManifest.cs            # .depenk/manifest.json, up-to-date check
│  │  ├─ Routes/RouteNormalizer.cs
│  │  ├─ Endpoints/IEndpointFinder.cs, EndpointParameters.cs,
│  │  │   ControllerEndpointFinder.cs, MinimalApiEndpointFinder.cs
│  │  ├─ Clients/RouteHit.cs (+IRouteStrategy, Verbs), RefitStrategy.cs, GeneratedClientStrategy.cs,
│  │  │   ConfiguredWrapperStrategy.cs, GenericHttpStrategy.cs, ClientMethodFinder.cs
│  │  ├─ Linking/ClientEndpointLinker.cs
│  │  ├─ Models/TypeUnwrapper.cs, TypeIndex.cs, ModelExtractor.cs
│  │  ├─ CallSites/CallSiteFinder.cs
│  │  ├─ Diagnostics/GraphDiagnostics.cs # drift, cycles, unused
│  │  └─ ScanOrchestrator.cs
│  └─ depenk/                            # CLI (PackAsTool, ToolCommandName=depenk)
│     └─ Program.cs
└─ tests/
   ├─ Depenk.Tests/                      # xUnit; one test file per unit, under Core/, Scanning/, Analysis/
   │  ├─ TestUtil/TempWorkspace.cs       # temp dirs + fake .git/HEAD
   │  ├─ TestUtil/Src.cs                 # build SourceSets from strings
   │  ├─ TestUtil/SyntheticWorkspace.cs  # 50-repo perf generator
   │  ├─ FixtureScanTests.cs, IncrementalScanTests.cs, PerfSmokeTests.cs
   │  └─ Snapshots/                      # Verify golden files
   └─ fixtures/workspace/                # fake repos (Task 12)
```

### Task 1: Solution scaffold + Core graph model + JSON round-trip

**Files:**
- Create: `Directory.Build.props`, `Depenk.sln`, `LICENSE`, `README.md`
- Create: `src/Depenk.Core/Depenk.Core.csproj`, `src/Depenk.Core/Model/Enums.cs`, `Model/Nodes.cs`, `Model/Edge.cs`, `Model/Diagnostic.cs`, `Model/DepGraph.cs`, `Ids.cs`, `GraphJson.cs`
- Create: `tests/Depenk.Tests/Depenk.Tests.csproj`, `tests/Depenk.Tests/Core/GraphJsonTests.cs`

**Interfaces:**
- Consumes: nothing
- Produces: every type below; `GraphJson.Serialize(DepGraph) : string`, `GraphJson.Deserialize(string) : DepGraph`, `GraphJson.Save(DepGraph, string path)`, `GraphJson.Load(string path) : DepGraph`; the `Ids.*` builders.

- [ ] **Step 1: Scaffold solution**

```bash
cd /c/code/repos/depenk
dotnet new sln -n Depenk
dotnet new classlib -n Depenk.Core -o src/Depenk.Core
dotnet new classlib -n Depenk.Scanning -o src/Depenk.Scanning
dotnet new classlib -n Depenk.Analysis -o src/Depenk.Analysis
dotnet new console -n depenk -o src/depenk
dotnet new xunit -n Depenk.Tests -o tests/Depenk.Tests
rm src/*/Class1.cs tests/Depenk.Tests/UnitTest1.cs
dotnet sln add src/Depenk.Core src/Depenk.Scanning src/Depenk.Analysis src/depenk tests/Depenk.Tests
dotnet add src/Depenk.Scanning reference src/Depenk.Core
dotnet add src/Depenk.Analysis reference src/Depenk.Core src/Depenk.Scanning
dotnet add src/depenk reference src/Depenk.Analysis
dotnet add tests/Depenk.Tests reference src/Depenk.Core src/Depenk.Scanning src/Depenk.Analysis
dotnet add src/Depenk.Analysis package Microsoft.CodeAnalysis.CSharp --version 4.12.0
dotnet add src/Depenk.Scanning package YamlDotNet --version 16.2.1
dotnet add src/depenk package System.CommandLine --version 2.0.0-beta4.22272.1
dotnet add tests/Depenk.Tests package Verify.Xunit --version 28.9.0
```

`Directory.Build.props` (repo root):

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <LangVersion>latest</LangVersion>
  </PropertyGroup>
</Project>
```

Remove the `<TargetFramework>`, `<Nullable>` and `<ImplicitUsings>` lines that `dotnet new` put in each generated csproj, since `Directory.Build.props` now owns them. `LICENSE` contains the standard MIT text with `Copyright (c) 2026 depenk contributors`. `README.md` has one paragraph taken from spec §1.

- [ ] **Step 2: Write the failing round-trip test**

`tests/Depenk.Tests/Core/GraphJsonTests.cs`:

```csharp
using Depenk.Core;
using Depenk.Core.Model;

namespace Depenk.Tests.Core;

public class GraphJsonTests
{
    [Fact]
    public void RoundTrips_AllNodeKinds_AndUsesCamelCaseEnums()
    {
        var g = new DepGraph();
        g.Repos.Add(new RepoNode(Ids.Repo("orders"), "orders", "orders", "abc123", false));
        g.Projects.Add(new ProjectNode(Ids.Project("orders", "Orders.Api"), "orders", "Orders.Api",
            "orders/src/Orders.Api/Orders.Api.csproj", ProjectKind.Api, "Microsoft.NET.Sdk.Web", null, null, false));
        g.Packages.Add(new PackageNode(Ids.Package("Orders.Client"), "Orders.Client", [Ids.Project("orders", "Orders.Client")]));
        g.Endpoints.Add(new EndpointNode(Ids.Endpoint("orders", "GET", "/api/orders/{id}"), "orders",
            Ids.Project("orders", "Orders.Api"), "GET", "/api/orders/{id}", "api/orders/{}", "OrdersController.Get",
            [new EndpointParameter("id", "route", "Guid", true, null)], [new ResponseType(200, "OrderDto")],
            new SourceLocation("orders/src/Orders.Api/OrdersController.cs", 12)));
        g.Edges.Add(new Edge(EdgeKind.References, Ids.Project("billing", "Billing.Api"), Ids.Package("Orders.Client"),
            Confidence.Certain) { Version = "3.4.1" });
        g.Diagnostics.Add(new Diagnostic("versionDrift", "warning", [Ids.Package("Orders.Client")], "drift"));

        var json = GraphJson.Serialize(g);
        var back = GraphJson.Deserialize(json);

        Assert.Contains("\"schemaVersion\": 1", json);
        Assert.Contains("\"kind\": \"api\"", json);
        Assert.Contains("\"confidence\": \"certain\"", json);
        Assert.DoesNotContain("\"external\"", json);
        Assert.Equal("api/orders/{}", back.Endpoints[0].NormalizedRoute);
        Assert.Equal("3.4.1", back.Edges[0].Version);
        Assert.Equal(EdgeKind.References, back.Edges[0].Kind);
        Assert.Equal(g.Endpoints[0].Parameters, back.Endpoints[0].Parameters);
    }

    [Fact]
    public void Ids_AreStableAndReadable()
    {
        Assert.Equal("repo:orders", Ids.Repo("orders"));
        Assert.Equal("proj:orders/Orders.Api", Ids.Project("orders", "Orders.Api"));
        Assert.Equal("pkg:Orders.Client", Ids.Package("Orders.Client"));
        Assert.Equal("ep:orders:GET:/api/orders/{id}", Ids.Endpoint("orders", "get", "/api/orders/{id}"));
        Assert.Equal("cm:Orders.Client:IOrdersClient.GetOrderAsync", Ids.ClientMethod("Orders.Client", "IOrdersClient", "GetOrderAsync"));
        Assert.Equal("cs:billing/Billing.Api:InvoiceBuilder.Build:118", Ids.CallSite("billing", "Billing.Api", "InvoiceBuilder", "Build", 118));
        Assert.Equal("model:Orders.Client:Acme.Orders.OrderDto", Ids.Model("Orders.Client", "Acme.Orders.OrderDto"));
    }
}
```

- [ ] **Step 3: Run to verify it fails**

Run: `dotnet test tests/Depenk.Tests --filter GraphJsonTests`
Expected: build FAILS: `The type or namespace name 'Model' does not exist in the namespace 'Depenk.Core'`.

- [ ] **Step 4: Implement the model**

`src/Depenk.Core/Model/Enums.cs`:

```csharp
namespace Depenk.Core.Model;

public enum Confidence { Low, Medium, High, Certain }
public enum ProjectKind { Api, Client, Library, Test, Other }
public enum ModelKind { Class, Record, Struct, Enum, Opaque }
public enum EdgeKind { References, Produces, Targets, Invokes, Accepts, Returns, FieldOf, DependsOn }
```

`src/Depenk.Core/Model/Nodes.cs`:

```csharp
using System.Text.Json.Serialization;

namespace Depenk.Core.Model;

public sealed record SourceLocation(string Path, int Line);

public sealed record RepoNode(string Id, string Name, string Path, string? HeadSha, bool Dirty);

public sealed record ProjectNode(string Id, string Repo, string Name, string Path, ProjectKind Kind,
    string? Sdk, string? PackageId, string? Version, bool IsPackable);

/// <summary>ProducerProjectIds empty = external; more than one = ambiguous.</summary>
public sealed record PackageNode(string Id, string PackageId, List<string> ProducerProjectIds)
{
    [JsonIgnore] public bool External => ProducerProjectIds.Count == 0;
}

public sealed record EndpointParameter(string Name, string Source, string TypeName, bool Required, string? Default);
public sealed record ResponseType(int StatusCode, string TypeName);

public sealed record EndpointNode(string Id, string Repo, string ProjectId, string Verb, string Route,
    string NormalizedRoute, string Handler, List<EndpointParameter> Parameters, List<ResponseType> Responses,
    SourceLocation Location);

public sealed record ClientMethodNode(string Id, string Repo, string ProjectId, string TypeName, string MethodName,
    string Signature, string? Verb, string? Route, string? NormalizedRoute, string Strategy, Confidence Confidence,
    SourceLocation Location);

public sealed record CallSiteNode(string Id, string Repo, string ProjectId, string ContainingMember,
    Confidence Confidence, SourceLocation Location);

public sealed record ModelField(string Name, string TypeName, bool Nullable, bool Collection);

public sealed record ModelNode(string Id, string Repo, string? ProjectId, string FullName, ModelKind Kind,
    List<ModelField> Fields, List<string>? EnumValues, SourceLocation? Location);
```

Records holding `List<>` compare lists by reference. The test compares `Parameters` with `Assert.Equal` on the lists, which xUnit compares element by element, and the element records compare by value. So the test is correct as written.

`src/Depenk.Core/Model/Edge.cs`:

```csharp
namespace Depenk.Core.Model;

public sealed record Edge(EdgeKind Kind, string From, string To, Confidence Confidence)
{
    public string? Version { get; init; }           // references / produces
    public string? Strategy { get; init; }          // targets
    public string? Source { get; init; }            // accepts: body|query|route|header
    public int? StatusCode { get; init; }           // returns
    public string? FieldName { get; init; }         // fieldOf
    public List<string>? ViaPackages { get; init; } // dependsOn
    public int? CallCount { get; init; }            // dependsOn
}
```

`src/Depenk.Core/Model/Diagnostic.cs`:

```csharp
namespace Depenk.Core.Model;

public sealed record Diagnostic(string Kind, string Severity, List<string> NodeIds, string Message);

public static class DiagnosticKinds
{
    public const string VersionDrift = "versionDrift", UnresolvedClientMethod = "unresolvedClientMethod",
        AmbiguousRoute = "ambiguousRoute", AmbiguousProducer = "ambiguousProducer",
        UnresolvedVersion = "unresolvedVersion", Cycle = "cycle", UnusedEndpoint = "unusedEndpoint",
        UnusedModel = "unusedModel", UnusedClientMethod = "unusedClientMethod", ParseError = "parseError";
}
```

`src/Depenk.Core/Model/DepGraph.cs`:

```csharp
namespace Depenk.Core.Model;

public sealed class DepGraph
{
    public int SchemaVersion { get; init; } = 1;
    public string? Workspace { get; set; }
    public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<RepoNode> Repos { get; init; } = [];
    public List<ProjectNode> Projects { get; init; } = [];
    public List<PackageNode> Packages { get; init; } = [];
    public List<EndpointNode> Endpoints { get; init; } = [];
    public List<ClientMethodNode> ClientMethods { get; init; } = [];
    public List<CallSiteNode> CallSites { get; init; } = [];
    public List<ModelNode> Models { get; init; } = [];
    public List<Edge> Edges { get; init; } = [];
    public List<Diagnostic> Diagnostics { get; init; } = [];

    public IEnumerable<Edge> EdgesOf(EdgeKind kind) => Edges.Where(e => e.Kind == kind);
}
```

`src/Depenk.Core/Ids.cs`:

```csharp
namespace Depenk.Core;

public static class Ids
{
    public static string Repo(string repo) => $"repo:{repo}";
    public static string Project(string repo, string project) => $"proj:{repo}/{project}";
    public static string Package(string packageId) => $"pkg:{packageId}";
    public static string Endpoint(string repo, string verb, string route) => $"ep:{repo}:{verb.ToUpperInvariant()}:{route}";
    public static string ClientMethod(string project, string type, string method) => $"cm:{project}:{type}.{method}";
    public static string CallSite(string repo, string project, string type, string member, int line) =>
        $"cs:{repo}/{project}:{type}.{member}:{line}";
    public static string Model(string project, string fullName) => $"model:{project}:{fullName}";
}
```

`src/Depenk.Core/GraphJson.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using Depenk.Core.Model;

namespace Depenk.Core;

public static class GraphJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string Serialize(DepGraph g) => JsonSerializer.Serialize(g, Options);

    public static DepGraph Deserialize(string json) =>
        JsonSerializer.Deserialize<DepGraph>(json, Options) ?? throw new InvalidDataException("Empty graph JSON");

    public static void Save(DepGraph g, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, Serialize(g));
    }

    public static DepGraph Load(string path) => Deserialize(File.ReadAllText(path));
}
```

- [ ] **Step 5: Run tests**

Run: `dotnet test tests/Depenk.Tests --filter GraphJsonTests`
Expected: PASS (2 tests). Then run `dotnet build Depenk.sln` and expect `0 Warning(s)`.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(core): graph model, ids and JSON serialization"
```

### Task 2: Config loading + repo discovery

**Files:**
- Create: `src/Depenk.Scanning/Config/DepenkConfig.cs`, `Config/ConfigLoader.cs`, `Config/Glob.cs`, `PathUtil.cs`, `RepoDiscovery.cs`
- Create: `tests/Depenk.Tests/TestUtil/TempWorkspace.cs`, `tests/Depenk.Tests/Scanning/ConfigLoaderTests.cs`, `tests/Depenk.Tests/Scanning/RepoDiscoveryTests.cs`

**Interfaces:**
- Consumes: `Depenk.Core.Model.*` (Task 1)
- Produces:
  - `DepenkConfig` (below)
  - `ConfigLoader.Load(string workspace) : DepenkConfig` (throws `ConfigException`)
  - `Glob.IsMatch(string pattern, string value) : bool`
  - `PathUtil.Rel(string workspace, string absolutePath) : string`
  - `RepoDiscovery.Discover(string workspace, DepenkConfig config) : List<DiscoveredRepo>`
  - `record DiscoveredRepo(string Name, string AbsolutePath, string RelativePath, string? HeadSha, bool Dirty)`
  - `TempWorkspace` test helper

- [ ] **Step 1: Write the test helper**

`tests/Depenk.Tests/TestUtil/TempWorkspace.cs`:

```csharp
namespace Depenk.Tests.TestUtil;

public sealed class TempWorkspace : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "depenk-tests", Guid.NewGuid().ToString("N"));

    public TempWorkspace() => Directory.CreateDirectory(Root);

    public TempWorkspace File(string relativePath, string content)
    {
        var full = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllText(full, content);
        return this;
    }

    /// <summary>Creates a fake git repo: .git/HEAD pointing at a branch ref with a fixed sha.</summary>
    public TempWorkspace Repo(string name, string sha = "0123456789abcdef0123456789abcdef01234567")
    {
        File($"{name}/.git/HEAD", "ref: refs/heads/main\n");
        File($"{name}/.git/refs/heads/main", sha + "\n");
        return this;
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { /* best effort */ }
    }
}
```

- [ ] **Step 2: Write failing tests**

`tests/Depenk.Tests/Scanning/ConfigLoaderTests.cs`:

```csharp
using Depenk.Scanning.Config;
using Depenk.Tests.TestUtil;

namespace Depenk.Tests.Scanning;

public class ConfigLoaderTests
{
    [Fact]
    public void MissingFile_ReturnsDefaults()
    {
        using var ws = new TempWorkspace();
        var cfg = ConfigLoader.Load(ws.Root);
        Assert.Equal(["*"], cfg.Repos.Include);
        Assert.Empty(cfg.HttpWrappers);
    }

    [Fact]
    public void ParsesAllSections()
    {
        using var ws = new TempWorkspace().File("depenk.yml", """
            repos:
              include: ["*"]
              exclude: ["legacy-*"]
            projects:
              kindOverrides:
                Orders.Contracts: Client
              ignore: ["*.Benchmarks"]
            packages:
              producers:
                Acme.Orders.Client: orders
            httpWrappers:
              - type: "*.IApiHttpClient"
                methods: { "Get*": GET, "Post*": POST }
                routeArgument: 0
            routes:
              prefixes:
                Orders.Client: /api
            """);
        var cfg = ConfigLoader.Load(ws.Root);
        Assert.Equal(["legacy-*"], cfg.Repos.Exclude);
        Assert.Equal("Client", cfg.Projects.KindOverrides["Orders.Contracts"]);
        Assert.Equal("orders", cfg.Packages.Producers["Acme.Orders.Client"]);
        Assert.Equal("GET", cfg.HttpWrappers[0].Methods["Get*"]);
        Assert.Equal("/api", cfg.Routes.Prefixes["Orders.Client"]);
    }

    [Fact]
    public void InvalidYaml_ThrowsWithLineNumber()
    {
        using var ws = new TempWorkspace().File("depenk.yml", "repos:\n  include: [\"*\"\n  exclude: x\n");
        var ex = Assert.Throws<ConfigException>(() => ConfigLoader.Load(ws.Root));
        Assert.Matches(@"depenk\.yml\(\d+\)", ex.Message);
    }

    [Theory]
    [InlineData("*.IApiHttpClient", "Acme.Http.IApiHttpClient", true)]
    [InlineData("legacy-*", "legacy-billing", true)]
    [InlineData("legacy-*", "billing", false)]
    [InlineData("Get*", "getasync", true)]
    [InlineData("Order?", "Orders", true)]
    public void Glob_Matches(string pattern, string value, bool expected) =>
        Assert.Equal(expected, Glob.IsMatch(pattern, value));
}
```

`tests/Depenk.Tests/Scanning/RepoDiscoveryTests.cs`:

```csharp
using Depenk.Scanning;
using Depenk.Scanning.Config;
using Depenk.Tests.TestUtil;

namespace Depenk.Tests.Scanning;

public class RepoDiscoveryTests
{
    [Fact]
    public void FindsChildGitRepos_AppliesFilters_ReadsHead()
    {
        using var ws = new TempWorkspace().Repo("orders", "aaaa000000000000000000000000000000000000")
            .Repo("billing").Repo("legacy-crm").File("notarepo/readme.md", "x");
        var cfg = new DepenkConfig { Repos = new RepoFilter { Exclude = ["legacy-*"] } };

        var repos = RepoDiscovery.Discover(ws.Root, cfg);

        Assert.Equal(["billing", "orders"], repos.Select(r => r.Name));
        var orders = repos.Single(r => r.Name == "orders");
        Assert.Equal("aaaa000000000000000000000000000000000000", orders.HeadSha);
        Assert.Equal("orders", orders.RelativePath);
    }

    [Fact]
    public void ReadsPackedRefs_WhenLooseRefMissing()
    {
        using var ws = new TempWorkspace()
            .File("orders/.git/HEAD", "ref: refs/heads/main\n")
            .File("orders/.git/packed-refs", "# pack-refs\nbbbb000000000000000000000000000000000000 refs/heads/main\n");
        var repo = RepoDiscovery.Discover(ws.Root, new DepenkConfig()).Single();
        Assert.Equal("bbbb000000000000000000000000000000000000", repo.HeadSha);
    }

    [Fact]
    public void ExplicitPaths_ReplaceDiscovery_AndMayBeNestedOrNonGit()
    {
        using var ws = new TempWorkspace().Repo("orders").File("group/billing/readme.md", "x");
        var cfg = new DepenkConfig { Repos = new RepoFilter { Paths = ["group/billing", "missing"] } };

        var repo = RepoDiscovery.Discover(ws.Root, cfg).Single();

        Assert.Equal(("billing", "group/billing", (string?)null), (repo.Name, repo.RelativePath, repo.HeadSha));
    }

    [Fact]
    public void WorkspaceItselfIsRepo_WhenNoChildRepos()
    {
        using var ws = new TempWorkspace().File(".git/HEAD", "0123456789abcdef0123456789abcdef01234567\n");
        var repo = RepoDiscovery.Discover(ws.Root, new DepenkConfig()).Single();
        Assert.Equal(".", repo.RelativePath);
        Assert.Equal(Path.GetFileName(ws.Root), repo.Name);
    }

    [Fact]
    public void RelativePaths_UseForwardSlashes()
    {
        Assert.Equal("orders/src/A.csproj",
            PathUtil.Rel(@"C:\ws", Path.Combine(@"C:\ws", "orders", "src", "A.csproj")));
    }
}
```

- [ ] **Step 3: Run to verify failure**

Run: `dotnet test tests/Depenk.Tests --filter "ConfigLoaderTests|RepoDiscoveryTests"`
Expected: build FAILS: `The type or namespace name 'Config' does not exist in the namespace 'Depenk.Scanning'`.

- [ ] **Step 4: Implement**

`src/Depenk.Scanning/Config/DepenkConfig.cs`:

```csharp
namespace Depenk.Scanning.Config;

public sealed class DepenkConfig
{
    public RepoFilter Repos { get; set; } = new();
    public ProjectOptions Projects { get; set; } = new();
    public PackageOptions Packages { get; set; } = new();
    public List<HttpWrapperConfig> HttpWrappers { get; set; } = [];
    public RouteOptions Routes { get; set; } = new();
}

public sealed class RepoFilter
{
    /// <summary>Explicit repo folders (relative to the workspace or absolute). When set, child-folder discovery is skipped.</summary>
    public List<string> Paths { get; set; } = [];
    public List<string> Include { get; set; } = ["*"];
    public List<string> Exclude { get; set; } = [];
}

public sealed class ProjectOptions
{
    /// <summary>Project name → ProjectKind name (case-insensitive), parsed by ProjectClassifier.</summary>
    public Dictionary<string, string> KindOverrides { get; set; } = [];
    public List<string> Ignore { get; set; } = [];
}

public sealed class PackageOptions
{
    /// <summary>PackageId → repo name that produces it.</summary>
    public Dictionary<string, string> Producers { get; set; } = [];
}

public sealed class HttpWrapperConfig
{
    public string Type { get; set; } = "";
    /// <summary>Method-name glob → HTTP verb.</summary>
    public Dictionary<string, string> Methods { get; set; } = [];
    public int RouteArgument { get; set; }
}

public sealed class RouteOptions
{
    /// <summary>Client project name → route prefix prepended to its routes.</summary>
    public Dictionary<string, string> Prefixes { get; set; } = [];
}
```

`src/Depenk.Scanning/Config/ConfigLoader.cs`:

```csharp
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Depenk.Scanning.Config;

public sealed class ConfigException(string message) : Exception(message);

public static class ConfigLoader
{
    public const string FileName = "depenk.yml";

    public static DepenkConfig Load(string workspace)
    {
        var path = Path.Combine(workspace, FileName);
        if (!File.Exists(path)) return new DepenkConfig();

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();
        try
        {
            return deserializer.Deserialize<DepenkConfig>(File.ReadAllText(path)) ?? new DepenkConfig();
        }
        catch (YamlException ex)
        {
            throw new ConfigException($"{FileName}({ex.Start.Line}): {ex.InnerException?.Message ?? ex.Message}");
        }
    }
}
```

`src/Depenk.Scanning/Config/Glob.cs`:

```csharp
using System.Text.RegularExpressions;

namespace Depenk.Scanning.Config;

public static class Glob
{
    public static bool IsMatch(string pattern, string value)
    {
        var regex = "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
        return Regex.IsMatch(value, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    public static bool Any(IEnumerable<string> patterns, string value) => patterns.Any(p => IsMatch(p, value));
}
```

`src/Depenk.Scanning/PathUtil.cs`:

```csharp
namespace Depenk.Scanning;

public static class PathUtil
{
    public static readonly HashSet<string> SkippedDirs =
        new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", "node_modules", ".git", ".depenk" };

    public static string Rel(string workspace, string absolutePath) =>
        Path.GetRelativePath(workspace, absolutePath).Replace('\\', '/');

    /// <summary>Recursively enumerates files matching the pattern, skipping SkippedDirs.</summary>
    public static IEnumerable<string> EnumerateFiles(string root, string searchPattern)
    {
        var stack = new Stack<string>([root]);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            foreach (var f in Directory.EnumerateFiles(dir, searchPattern)) yield return f;
            foreach (var d in Directory.EnumerateDirectories(dir))
                if (!SkippedDirs.Contains(Path.GetFileName(d))) stack.Push(d);
        }
    }
}
```

`src/Depenk.Scanning/RepoDiscovery.cs`:

```csharp
using Depenk.Scanning.Config;

namespace Depenk.Scanning;

public sealed record DiscoveredRepo(string Name, string AbsolutePath, string RelativePath, string? HeadSha, bool Dirty);

public static class RepoDiscovery
{
    public static List<DiscoveredRepo> Discover(string workspace, DepenkConfig config)
    {
        workspace = Path.GetFullPath(workspace);
        var candidates = config.Repos.Paths.Count > 0
            ? config.Repos.Paths.Select(p => Path.GetFullPath(Path.Combine(workspace, p))).Where(Directory.Exists)
            : Directory.EnumerateDirectories(workspace).Where(IsGitRepo);
        var repos = candidates
            .Select(d => Make(workspace, d, Path.GetFileName(d)))
            .Where(r => Glob.Any(config.Repos.Include, r.Name) && !Glob.Any(config.Repos.Exclude, r.Name))
            .OrderBy(r => r.Name, StringComparer.Ordinal)
            .ToList();

        if (repos.Count == 0 && config.Repos.Paths.Count == 0 && IsGitRepo(workspace))
            repos.Add(Make(workspace, workspace, Path.GetFileName(workspace.TrimEnd(Path.DirectorySeparatorChar))));
        return repos;
    }

    private static bool IsGitRepo(string dir) =>
        Directory.Exists(Path.Combine(dir, ".git")) || File.Exists(Path.Combine(dir, ".git"));

    private static DiscoveredRepo Make(string workspace, string dir, string name) =>
        new(name, dir, PathUtil.Rel(workspace, dir), ReadHead(dir), Dirty: false);

    /// <summary>Reads HEAD without shelling out. Handles detached HEAD, loose refs and packed-refs.</summary>
    internal static string? ReadHead(string repoDir)
    {
        var gitDir = Path.Combine(repoDir, ".git");
        if (File.Exists(gitDir)) // worktree/submodule: "gitdir: <path>"
        {
            var pointer = File.ReadAllText(gitDir).Trim();
            if (!pointer.StartsWith("gitdir:")) return null;
            gitDir = Path.GetFullPath(Path.Combine(repoDir, pointer["gitdir:".Length..].Trim()));
        }
        var headFile = Path.Combine(gitDir, "HEAD");
        if (!File.Exists(headFile)) return null;
        var head = File.ReadAllText(headFile).Trim();
        if (!head.StartsWith("ref:")) return head;

        var refName = head["ref:".Length..].Trim();
        var loose = Path.Combine(gitDir, refName.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(loose)) return File.ReadAllText(loose).Trim();

        var packed = Path.Combine(gitDir, "packed-refs");
        if (!File.Exists(packed)) return null;
        return File.ReadLines(packed)
            .Select(l => l.Split(' ', 2))
            .FirstOrDefault(p => p.Length == 2 && p[1].Trim() == refName)?[0];
    }
}
```

`Dirty` is always `false` in Plan 1. Plan 4 (History) sets it by running `git status --porcelain`. That's the only feature that needs it, and it keeps Plan 1 free of process calls.

- [ ] **Step 5: Run tests**

Run: `dotnet test tests/Depenk.Tests --filter "ConfigLoaderTests|RepoDiscoveryTests"`
Expected: PASS (12 tests, including 5 Glob theory cases).

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(scanning): depenk.yml config, glob matching and repo discovery"
```

### Task 3: csproj parsing with Directory.Build.props / central package management

**Files:**
- Create: `src/Depenk.Scanning/ProjectFile.cs`, `src/Depenk.Scanning/PropsResolver.cs`, `src/Depenk.Scanning/ProjectParser.cs`
- Test: `tests/Depenk.Tests/Scanning/ProjectParserTests.cs`

**Interfaces:**
- Consumes: `PathUtil` (Task 2), `TempWorkspace` (Task 2)
- Produces:
  - `record PackageRef(string Id, string? Version)`: `Version` is the resolved string, `"unresolved($(Prop))"`, or `null` when no version is declared anywhere
  - `record ProjectFile(string AbsolutePath, string Name, string? Sdk, string EffectivePackageId, bool ExplicitPackageId, string? Version, bool IsPackable, bool IsTestProject, List<PackageRef> PackageReferences, List<string> ProjectReferences)`, with the helper `bool IsUnresolved(string? v)`
  - `ProjectParser.Parse(string csprojPath, string repoRoot) : ProjectFile` (throws `ProjectParseException` on bad XML)
  - `PropsResolver.Collect(string projectDir, string repoRoot) : (Dictionary<string,string> Properties, Dictionary<string,string> CentralVersions)`

- [ ] **Step 1: Write failing tests**

`tests/Depenk.Tests/Scanning/ProjectParserTests.cs`:

```csharp
using Depenk.Scanning;
using Depenk.Tests.TestUtil;

namespace Depenk.Tests.Scanning;

public class ProjectParserTests
{
    private static string P(TempWorkspace ws, string rel) => Path.Combine(ws.Root, rel);

    [Fact]
    public void ParsesSdk_PackageId_Version_AndReferences()
    {
        using var ws = new TempWorkspace().File("orders/src/Orders.Client/Orders.Client.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <PackageId>Acme.Orders.Client</PackageId>
                <Version>3.4.1</Version>
                <IsPackable>true</IsPackable>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Refit" Version="7.2.1" />
                <PackageReference Include="Acme.Http"><Version>1.0.0</Version></PackageReference>
                <ProjectReference Include="..\Orders.Models\Orders.Models.csproj" />
              </ItemGroup>
            </Project>
            """);
        var pf = ProjectParser.Parse(P(ws, "orders/src/Orders.Client/Orders.Client.csproj"), P(ws, "orders"));

        Assert.Equal("Orders.Client", pf.Name);
        Assert.Equal("Microsoft.NET.Sdk", pf.Sdk);
        Assert.Equal("Acme.Orders.Client", pf.EffectivePackageId);
        Assert.True(pf.ExplicitPackageId);
        Assert.Equal("3.4.1", pf.Version);
        Assert.True(pf.IsPackable);
        Assert.Equal([new PackageRef("Refit", "7.2.1"), new PackageRef("Acme.Http", "1.0.0")], pf.PackageReferences);
        Assert.EndsWith(Path.Combine("Orders.Models", "Orders.Models.csproj"), pf.ProjectReferences.Single());
    }

    [Fact]
    public void ResolvesProperties_FromDirectoryBuildProps_AndCentralVersions()
    {
        using var ws = new TempWorkspace()
            .File("billing/Directory.Build.props", """
                <Project><PropertyGroup><AcmeVersion>2.0.0</AcmeVersion><Version>9.9.9</Version></PropertyGroup></Project>
                """)
            .File("billing/Directory.Packages.props", """
                <Project><ItemGroup>
                  <PackageVersion Include="Acme.Orders.Client" Version="3.2.0" />
                  <PackageVersion Include="Acme.Http" Version="$(AcmeVersion)" />
                </ItemGroup></Project>
                """)
            .File("billing/src/Billing.Api/Billing.Api.csproj", """
                <Project Sdk="Microsoft.NET.Sdk.Web">
                  <PropertyGroup><Version>1.2.3</Version></PropertyGroup>
                  <ItemGroup>
                    <PackageReference Include="Acme.Orders.Client" />
                    <PackageReference Include="Acme.Http" />
                    <PackageReference Include="Serilog" VersionOverride="4.0.0" />
                    <PackageReference Include="Missing.Pkg" Version="$(NotDefinedAnywhere)" />
                  </ItemGroup>
                </Project>
                """);
        var pf = ProjectParser.Parse(P(ws, "billing/src/Billing.Api/Billing.Api.csproj"), P(ws, "billing"));

        Assert.Equal("1.2.3", pf.Version); // project overrides props
        Assert.Equal("3.2.0", pf.PackageReferences.Single(r => r.Id == "Acme.Orders.Client").Version);
        Assert.Equal("2.0.0", pf.PackageReferences.Single(r => r.Id == "Acme.Http").Version);
        Assert.Equal("4.0.0", pf.PackageReferences.Single(r => r.Id == "Serilog").Version);
        Assert.Equal("unresolved($(NotDefinedAnywhere))", pf.PackageReferences.Single(r => r.Id == "Missing.Pkg").Version);
        Assert.True(ProjectFile.IsUnresolved(pf.PackageReferences.Single(r => r.Id == "Missing.Pkg").Version));
    }

    [Fact]
    public void DefaultsPackageId_ToAssemblyName_ThenFileName_AndDetectsTests()
    {
        using var ws = new TempWorkspace()
            .File("r/A/A.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><AssemblyName>Acme.A</AssemblyName></PropertyGroup></Project>")
            .File("r/B/B.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>")
            .File("r/T/T.csproj", """
                <Project Sdk="Microsoft.NET.Sdk"><ItemGroup><PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.0.0" /></ItemGroup></Project>
                """);
        Assert.Equal("Acme.A", ProjectParser.Parse(P(ws, "r/A/A.csproj"), P(ws, "r")).EffectivePackageId);
        var b = ProjectParser.Parse(P(ws, "r/B/B.csproj"), P(ws, "r"));
        Assert.Equal("B", b.EffectivePackageId);
        Assert.False(b.ExplicitPackageId);
        Assert.True(ProjectParser.Parse(P(ws, "r/T/T.csproj"), P(ws, "r")).IsTestProject);
    }

    [Fact]
    public void HandlesLegacyMsbuildNamespace()
    {
        using var ws = new TempWorkspace().File("r/L/L.csproj", """
            <Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <ItemGroup><PackageReference Include="Newtonsoft.Json" Version="13.0.3" /></ItemGroup>
            </Project>
            """);
        var pf = ProjectParser.Parse(P(ws, "r/L/L.csproj"), P(ws, "r"));
        Assert.Null(pf.Sdk);
        Assert.Equal("Newtonsoft.Json", pf.PackageReferences.Single().Id);
    }

    [Fact]
    public void MalformedXml_ThrowsProjectParseException_WithPath()
    {
        using var ws = new TempWorkspace().File("r/Bad/Bad.csproj", "<Project><PropertyGroup></Project>");
        var ex = Assert.Throws<ProjectParseException>(() => ProjectParser.Parse(P(ws, "r/Bad/Bad.csproj"), P(ws, "r")));
        Assert.Contains("Bad.csproj", ex.Message);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Depenk.Tests --filter ProjectParserTests`
Expected: build FAILS: `The type or namespace name 'ProjectParser' could not be found`.

- [ ] **Step 3: Implement**

`src/Depenk.Scanning/ProjectFile.cs`:

```csharp
namespace Depenk.Scanning;

public sealed record PackageRef(string Id, string? Version);

public sealed record ProjectFile(
    string AbsolutePath, string Name, string? Sdk, string EffectivePackageId, bool ExplicitPackageId,
    string? Version, bool IsPackable, bool IsTestProject,
    List<PackageRef> PackageReferences, List<string> ProjectReferences)
{
    public static bool IsUnresolved(string? v) => v is not null && v.StartsWith("unresolved(", StringComparison.Ordinal);
}

public sealed class ProjectParseException(string path, Exception inner)
    : Exception($"Could not parse {path}: {inner.Message}", inner)
{
    public string ProjectPath { get; } = path;
}
```

`src/Depenk.Scanning/PropsResolver.cs`:

```csharp
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Depenk.Scanning;

public static partial class PropsResolver
{
    /// <summary>
    /// Walks from projectDir up to repoRoot (inclusive). Properties from every Directory.Build.props are merged
    /// with nearer files winning; central versions come from the nearest Directory.Packages.props.
    /// </summary>
    public static (Dictionary<string, string> Properties, Dictionary<string, string> CentralVersions) Collect(
        string projectDir, string repoRoot)
    {
        var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var central = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var dirs = new List<string>();
        var root = Path.GetFullPath(repoRoot).TrimEnd(Path.DirectorySeparatorChar);
        for (var d = Path.GetFullPath(projectDir); d is not null; d = Path.GetDirectoryName(d))
        {
            dirs.Add(d);
            if (string.Equals(d.TrimEnd(Path.DirectorySeparatorChar), root, StringComparison.OrdinalIgnoreCase)) break;
        }

        var centralFound = false;
        foreach (var dir in dirs) // nearest first
        {
            var buildProps = Path.Combine(dir, "Directory.Build.props");
            if (File.Exists(buildProps))
                foreach (var (k, v) in ReadProperties(XDocument.Load(buildProps)))
                    props.TryAdd(k, v); // nearer already added → wins

            var pkgProps = Path.Combine(dir, "Directory.Packages.props");
            if (!centralFound && File.Exists(pkgProps))
            {
                centralFound = true;
                foreach (var e in XDocument.Load(pkgProps).Descendants().Where(e => e.Name.LocalName == "PackageVersion"))
                {
                    var id = (string?)e.Attribute("Include");
                    var ver = (string?)e.Attribute("Version");
                    if (id is not null && ver is not null) central[id] = ver;
                }
            }
        }
        return (props, central);
    }

    public static IEnumerable<(string Key, string Value)> ReadProperties(XDocument doc) =>
        doc.Descendants().Where(e => e.Name.LocalName == "PropertyGroup")
            .SelectMany(pg => pg.Elements())
            .Select(e => (e.Name.LocalName, e.Value.Trim()));

    /// <summary>Substitutes $(Name) up to 5 levels deep; leaves "unresolved($(X))" if any reference is unknown.</summary>
    public static string? Expand(string? value, IReadOnlyDictionary<string, string> props)
    {
        if (value is null) return null;
        for (var i = 0; i < 5 && value.Contains("$("); i++)
            value = PropRef().Replace(value, m => props.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);
        var leftover = PropRef().Match(value);
        return leftover.Success ? $"unresolved({leftover.Value})" : value;
    }

    [GeneratedRegex(@"\$\(([A-Za-z_][A-Za-z0-9_.-]*)\)")]
    private static partial Regex PropRef();
}
```

`src/Depenk.Scanning/ProjectParser.cs`:

```csharp
using System.Xml;
using System.Xml.Linq;

namespace Depenk.Scanning;

public static class ProjectParser
{
    /// <summary>Malformed XML in the csproj or any props file becomes a ProjectParseException.</summary>
    public static ProjectFile Parse(string csprojPath, string repoRoot)
    {
        try { return ParseCore(csprojPath, repoRoot); }
        catch (XmlException ex) { throw new ProjectParseException(csprojPath, ex); }
    }

    private static ProjectFile ParseCore(string csprojPath, string repoRoot)
    {
        var doc = XDocument.Load(csprojPath);
        var (props, central) = PropsResolver.Collect(Path.GetDirectoryName(csprojPath)!, repoRoot);
        foreach (var (k, v) in PropsResolver.ReadProperties(doc)) props[k] = v; // project wins

        string? Prop(string name) => props.TryGetValue(name, out var v) && v.Length > 0 ? PropsResolver.Expand(v, props) : null;

        var name = Path.GetFileNameWithoutExtension(csprojPath);
        var sdk = (string?)doc.Root?.Attribute("Sdk")
                  ?? doc.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "Sdk")?.Attribute("Name")?.Value;
        var explicitId = Prop("PackageId");

        var packageRefs = doc.Descendants().Where(e => e.Name.LocalName == "PackageReference")
            .Select(e =>
            {
                var id = (string?)e.Attribute("Include");
                if (id is null) return null;
                var raw = (string?)e.Attribute("VersionOverride")
                          ?? (string?)e.Attribute("Version")
                          ?? e.Elements().FirstOrDefault(c => c.Name.LocalName == "Version")?.Value
                          ?? (central.TryGetValue(id, out var cv) ? cv : null);
                return new PackageRef(id, PropsResolver.Expand(raw, props));
            })
            .OfType<PackageRef>()
            .ToList();

        var projectRefs = doc.Descendants().Where(e => e.Name.LocalName == "ProjectReference")
            .Select(e => (string?)e.Attribute("Include"))
            .OfType<string>()
            .Select(p => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(csprojPath)!,
                p.Replace('\\', Path.DirectorySeparatorChar))))
            .ToList();

        var isTest = string.Equals(Prop("IsTestProject"), "true", StringComparison.OrdinalIgnoreCase)
                     || packageRefs.Any(r => r.Id.Equals("Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase));
        var packableProp = Prop("IsPackable");
        var isPackable = packableProp is not null
            ? packableProp.Equals("true", StringComparison.OrdinalIgnoreCase)
            : explicitId is not null
              || string.Equals(Prop("GeneratePackageOnBuild"), "true", StringComparison.OrdinalIgnoreCase);

        return new ProjectFile(
            AbsolutePath: Path.GetFullPath(csprojPath),
            Name: name,
            Sdk: sdk,
            EffectivePackageId: explicitId ?? Prop("AssemblyName") ?? name,
            ExplicitPackageId: explicitId is not null,
            Version: Prop("Version") ?? Prop("PackageVersion") ?? Prop("VersionPrefix"),
            IsPackable: isPackable && !isTest,
            IsTestProject: isTest,
            PackageReferences: packageRefs,
            ProjectReferences: projectRefs);
    }
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet test tests/Depenk.Tests --filter ProjectParserTests`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(scanning): csproj parser with props and central package version resolution"
```

### Task 4: Project classification + package graph (repos, projects, packages, references, dependsOn)

**Files:**
- Create: `src/Depenk.Scanning/ProjectClassifier.cs`, `src/Depenk.Scanning/PackageGraphBuilder.cs`
- Test: `tests/Depenk.Tests/Scanning/ProjectClassifierTests.cs`, `tests/Depenk.Tests/Scanning/PackageGraphBuilderTests.cs`

**Interfaces:**
- Consumes: `ProjectFile`, `ProjectParser` (Task 3); `DiscoveredRepo`, `RepoDiscovery`, `DepenkConfig`, `Glob`, `PathUtil` (Task 2); Core model (Task 1)
- Produces:
  - `ProjectClassifier.Classify(ProjectFile pf, ProjectSignals signals, DepenkConfig config) : ProjectKind`
  - `record ProjectSignals(bool HasEndpoints, bool HasHttpClientCode)`: filled in by the analysis layer (Task 11); tests construct it directly
  - `sealed class ScannedProject(DiscoveredRepo Repo, ProjectFile File)` with property `string Id`
  - `PackageGraphBuilder.LoadProjects(string workspace, IEnumerable<DiscoveredRepo> repos, DepenkConfig config, DepGraph graph) : List<ScannedProject>`: parses every csproj, and a parse failure becomes a `parseError` diagnostic
  - `PackageGraphBuilder.Build(string workspace, IReadOnlyList<ScannedProject> projects, IReadOnlyDictionary<string, ProjectKind> kinds, DepenkConfig config, DepGraph graph)`: adds ProjectNodes, PackageNodes, `references`/`produces`/`dependsOn` edges, and `ambiguousProducer`/`unresolvedVersion` diagnostics

**Rules (from spec §3.1 steps 3–4 and 10):**
- Classification order:
  1. config override
  2. `Test` if `IsTestProject`
  3. `Api` if the SDK is `Microsoft.NET.Sdk.Web` or `signals.HasEndpoints`
  4. `Client` if (packable, or the name ends in `.Client`, `.Contracts` or `.Sdk`) and `signals.HasHttpClientCode`
  5. `Library` if packable
  6. otherwise `Other`
- A `PackageReference` links to every non-test project in the workspace whose `EffectivePackageId` equals the reference ID (case-insensitive).
  - `packages.producers` in config pins the producing repo.
  - More than one producer left over means `ambiguousProducer`, and each `produces` edge gets `Confidence.Low`.
  - Otherwise the `produces` edge is `Certain`.
- `dependsOn` (Repo A → Repo B) exists when a project in A references a package produced in B, with A ≠ B. `ViaPackages` is the sorted distinct package IDs. `CallCount` is left null here (Task 11 fills it in).

- [ ] **Step 1: Write failing tests**

`tests/Depenk.Tests/Scanning/ProjectClassifierTests.cs`:

```csharp
using Depenk.Core.Model;
using Depenk.Scanning;
using Depenk.Scanning.Config;

namespace Depenk.Tests.Scanning;

public class ProjectClassifierTests
{
    private static ProjectFile Pf(string name, string? sdk = "Microsoft.NET.Sdk", bool packable = false, bool test = false) =>
        new($"/x/{name}.csproj", name, sdk, name, false, null, packable, test, [], []);

    [Theory]
    [InlineData("Orders.Api", "Microsoft.NET.Sdk.Web", false, false, false, false, ProjectKind.Api)]
    [InlineData("Orders.Functions", "Microsoft.NET.Sdk", false, false, true, false, ProjectKind.Api)]
    [InlineData("Orders.Client", "Microsoft.NET.Sdk", false, false, false, true, ProjectKind.Client)]
    [InlineData("Orders.Sdk2", "Microsoft.NET.Sdk", true, false, false, true, ProjectKind.Client)]
    [InlineData("Orders.Client", "Microsoft.NET.Sdk", false, false, false, false, ProjectKind.Other)]
    [InlineData("Shared.Kernel", "Microsoft.NET.Sdk", true, false, false, false, ProjectKind.Library)]
    [InlineData("Orders.Tests", "Microsoft.NET.Sdk", false, true, true, true, ProjectKind.Test)]
    public void Classifies(string name, string sdk, bool packable, bool test, bool hasEndpoints, bool hasHttp, ProjectKind expected)
    {
        var kind = ProjectClassifier.Classify(Pf(name, sdk, packable, test), new ProjectSignals(hasEndpoints, hasHttp), new DepenkConfig());
        Assert.Equal(expected, kind);
    }

    [Fact]
    public void ConfigOverrideWins_CaseInsensitive()
    {
        var cfg = new DepenkConfig();
        cfg.Projects.KindOverrides["Orders.Contracts"] = "client";
        Assert.Equal(ProjectKind.Client,
            ProjectClassifier.Classify(Pf("Orders.Contracts", packable: true), new ProjectSignals(false, false), cfg));
    }
}
```

`tests/Depenk.Tests/Scanning/PackageGraphBuilderTests.cs`:

```csharp
using Depenk.Core;
using Depenk.Core.Model;
using Depenk.Scanning;
using Depenk.Scanning.Config;
using Depenk.Tests.TestUtil;

namespace Depenk.Tests.Scanning;

public class PackageGraphBuilderTests
{
    private static string Csproj(string sdk, string props = "", string refs = "") =>
        $"<Project Sdk=\"{sdk}\"><PropertyGroup>{props}</PropertyGroup><ItemGroup>{refs}</ItemGroup></Project>";

    private static (DepGraph Graph, List<ScannedProject> Projects) Run(TempWorkspace ws, DepenkConfig? cfg = null)
    {
        cfg ??= new DepenkConfig();
        var g = new DepGraph();
        var repos = RepoDiscovery.Discover(ws.Root, cfg);
        g.Repos.AddRange(repos.Select(r => new RepoNode(Ids.Repo(r.Name), r.Name, r.RelativePath, r.HeadSha, r.Dirty)));
        var projects = PackageGraphBuilder.LoadProjects(ws.Root, repos, cfg, g);
        var kinds = projects.ToDictionary(p => p.Id,
            p => ProjectClassifier.Classify(p.File, new ProjectSignals(false, p.File.Name.EndsWith(".Client")), cfg));
        PackageGraphBuilder.Build(ws.Root, projects, kinds, cfg, g);
        return (g, projects);
    }

    [Fact]
    public void LinksConsumerToProducer_AndDerivesRepoDependency()
    {
        using var ws = new TempWorkspace().Repo("orders").Repo("billing")
            .File("orders/src/Orders.Client/Orders.Client.csproj",
                Csproj("Microsoft.NET.Sdk", "<IsPackable>true</IsPackable><Version>3.4.1</Version>"))
            .File("billing/src/Billing.Api/Billing.Api.csproj",
                Csproj("Microsoft.NET.Sdk.Web", "", "<PackageReference Include=\"Orders.Client\" Version=\"3.2.0\" /><PackageReference Include=\"Serilog\" Version=\"4.0.0\" />"));

        var (g, _) = Run(ws);

        Assert.Contains(g.Projects, p => p.Id == "proj:orders/Orders.Client" && p.Kind == ProjectKind.Client
                                         && p.Path == "orders/src/Orders.Client/Orders.Client.csproj");
        var pkg = g.Packages.Single(p => p.PackageId == "Orders.Client");
        Assert.Equal(["proj:orders/Orders.Client"], pkg.ProducerProjectIds);
        Assert.True(g.Packages.Single(p => p.PackageId == "Serilog").External);

        var refEdge = g.EdgesOf(EdgeKind.References).Single(e => e.To == "pkg:Orders.Client");
        Assert.Equal(("proj:billing/Billing.Api", "3.2.0", Confidence.Certain), (refEdge.From, refEdge.Version, refEdge.Confidence));
        var produces = g.EdgesOf(EdgeKind.Produces).Single();
        Assert.Equal(("proj:orders/Orders.Client", "pkg:Orders.Client", "3.4.1"), (produces.From, produces.To, produces.Version));

        var dep = g.EdgesOf(EdgeKind.DependsOn).Single();
        Assert.Equal(("repo:billing", "repo:orders"), (dep.From, dep.To));
        Assert.Equal(["Orders.Client"], dep.ViaPackages);
    }

    [Fact]
    public void DuplicateProducers_AreKeptAsLowConfidence_WithDiagnostic()
    {
        using var ws = new TempWorkspace().Repo("a").Repo("b").Repo("c")
            .File("a/Shared/Shared.csproj", Csproj("Microsoft.NET.Sdk", "<PackageId>Acme.Shared</PackageId>"))
            .File("b/Shared/Shared.csproj", Csproj("Microsoft.NET.Sdk", "<PackageId>Acme.Shared</PackageId>"))
            .File("c/App/App.csproj", Csproj("Microsoft.NET.Sdk", "", "<PackageReference Include=\"Acme.Shared\" Version=\"1.0.0\" />"));

        var (g, _) = Run(ws);

        Assert.Equal(2, g.Packages.Single(p => p.PackageId == "Acme.Shared").ProducerProjectIds.Count);
        Assert.All(g.EdgesOf(EdgeKind.Produces), e => Assert.Equal(Confidence.Low, e.Confidence));
        Assert.Contains(g.Diagnostics, d => d.Kind == DiagnosticKinds.AmbiguousProducer && d.NodeIds.Contains("pkg:Acme.Shared"));
    }

    [Fact]
    public void ConfigProducerPin_ResolvesAmbiguity()
    {
        using var ws = new TempWorkspace().Repo("a").Repo("b")
            .File("a/Shared/Shared.csproj", Csproj("Microsoft.NET.Sdk", "<PackageId>Acme.Shared</PackageId>"))
            .File("b/Shared/Shared.csproj", Csproj("Microsoft.NET.Sdk", "<PackageId>Acme.Shared</PackageId>"));
        var cfg = new DepenkConfig();
        cfg.Packages.Producers["Acme.Shared"] = "b";

        var (g, _) = Run(ws, cfg);

        Assert.Equal(["proj:b/Shared"], g.Packages.Single().ProducerProjectIds);
        Assert.DoesNotContain(g.Diagnostics, d => d.Kind == DiagnosticKinds.AmbiguousProducer);
    }

    [Fact]
    public void BadCsproj_BecomesParseErrorDiagnostic_AndScanContinues()
    {
        using var ws = new TempWorkspace().Repo("r")
            .File("r/Bad/Bad.csproj", "<Project><oops></Project>")
            .File("r/Good/Good.csproj", Csproj("Microsoft.NET.Sdk"));

        var (g, projects) = Run(ws);

        Assert.Equal(["Good"], projects.Select(p => p.File.Name));
        var diag = g.Diagnostics.Single(d => d.Kind == DiagnosticKinds.ParseError);
        Assert.Contains("r/Bad/Bad.csproj", diag.Message);
        Assert.DoesNotContain(":\\", diag.Message); // no absolute Windows paths leak
    }

    [Fact]
    public void UnresolvedVersion_EmitsDiagnostic()
    {
        using var ws = new TempWorkspace().Repo("r")
            .File("r/App/App.csproj", Csproj("Microsoft.NET.Sdk", "", "<PackageReference Include=\"X\" Version=\"$(Nope)\" />"));
        var (g, _) = Run(ws);
        Assert.Equal("unresolved($(Nope))", g.EdgesOf(EdgeKind.References).Single().Version);
        Assert.Contains(g.Diagnostics, d => d.Kind == DiagnosticKinds.UnresolvedVersion);
    }

    [Fact]
    public void IgnoredProjects_AreSkipped()
    {
        using var ws = new TempWorkspace().Repo("r")
            .File("r/App.Benchmarks/App.Benchmarks.csproj", Csproj("Microsoft.NET.Sdk"))
            .File("r/App/App.csproj", Csproj("Microsoft.NET.Sdk"));
        var cfg = new DepenkConfig();
        cfg.Projects.Ignore.Add("*.Benchmarks");
        var (_, projects) = Run(ws, cfg);
        Assert.Equal(["App"], projects.Select(p => p.File.Name));
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Depenk.Tests --filter "ProjectClassifierTests|PackageGraphBuilderTests"`
Expected: build FAILS: `The name 'ProjectClassifier' does not exist in the current context`.

- [ ] **Step 3: Implement**

`src/Depenk.Scanning/ProjectClassifier.cs`:

```csharp
using Depenk.Core.Model;
using Depenk.Scanning.Config;

namespace Depenk.Scanning;

public sealed record ProjectSignals(bool HasEndpoints, bool HasHttpClientCode);

public static class ProjectClassifier
{
    private static readonly string[] ClientSuffixes = [".Client", ".Contracts", ".Sdk"];

    public static ProjectKind Classify(ProjectFile pf, ProjectSignals signals, DepenkConfig config)
    {
        if (config.Projects.KindOverrides.TryGetValue(pf.Name, out var over)
            && Enum.TryParse<ProjectKind>(over, ignoreCase: true, out var forced))
            return forced;
        if (pf.IsTestProject) return ProjectKind.Test;
        if (string.Equals(pf.Sdk, "Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase) || signals.HasEndpoints)
            return ProjectKind.Api;
        var clientNamed = ClientSuffixes.Any(s => pf.Name.EndsWith(s, StringComparison.OrdinalIgnoreCase));
        if ((pf.IsPackable || clientNamed) && signals.HasHttpClientCode) return ProjectKind.Client;
        return pf.IsPackable ? ProjectKind.Library : ProjectKind.Other;
    }
}
```

`src/Depenk.Scanning/PackageGraphBuilder.cs`:

```csharp
using Depenk.Core;
using Depenk.Core.Model;
using Depenk.Scanning.Config;

namespace Depenk.Scanning;

public sealed class ScannedProject(DiscoveredRepo repo, ProjectFile file)
{
    public DiscoveredRepo Repo { get; } = repo;
    public ProjectFile File { get; } = file;
    public string Id { get; } = Ids.Project(repo.Name, file.Name);
    public string Directory => Path.GetDirectoryName(File.AbsolutePath)!;
}

public static class PackageGraphBuilder
{
    public static List<ScannedProject> LoadProjects(string workspace, IEnumerable<DiscoveredRepo> repos,
        DepenkConfig config, DepGraph graph)
    {
        var result = new List<ScannedProject>();
        foreach (var repo in repos)
        foreach (var csproj in PathUtil.EnumerateFiles(repo.AbsolutePath, "*.csproj").Order(StringComparer.Ordinal))
        {
            if (Glob.Any(config.Projects.Ignore, Path.GetFileNameWithoutExtension(csproj))) continue;
            try
            {
                result.Add(new ScannedProject(repo, ProjectParser.Parse(csproj, repo.AbsolutePath)));
            }
            catch (Exception ex) when (ex is ProjectParseException or IOException or UnauthorizedAccessException)
            {
                var rel = PathUtil.Rel(workspace, csproj);
                graph.Diagnostics.Add(new Diagnostic(DiagnosticKinds.ParseError, "warning", [Ids.Repo(repo.Name)],
                    $"{rel}: {(ex.InnerException ?? ex).Message}"));
            }
        }
        return result;
    }

    public static void Build(string workspace, IReadOnlyList<ScannedProject> projects,
        IReadOnlyDictionary<string, ProjectKind> kinds, DepenkConfig config, DepGraph graph)
    {
        foreach (var p in projects)
            graph.Projects.Add(new ProjectNode(p.Id, p.Repo.Name, p.File.Name, PathUtil.Rel(workspace, p.File.AbsolutePath),
                kinds[p.Id], p.File.Sdk, p.File.IsPackable || p.File.ExplicitPackageId ? p.File.EffectivePackageId : null,
                p.File.Version, p.File.IsPackable));

        // candidate producers: every non-test project, keyed by effective package id
        var producersById = projects.Where(p => !p.File.IsTestProject)
            .GroupBy(p => p.File.EffectivePackageId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var referencedIds = projects.SelectMany(p => p.File.PackageReferences.Select(r => r.Id))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        // packages = everything referenced + everything explicitly packable
        var packageIds = referencedIds
            .Concat(projects.Where(p => p.File.IsPackable).Select(p => p.File.EffectivePackageId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase);

        var producerRepoByPackage = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in packageIds)
        {
            var producers = producersById.TryGetValue(id, out var list) ? list : [];
            if (config.Packages.Producers.TryGetValue(id, out var pinnedRepo))
                producers = producers.Where(p => p.Repo.Name.Equals(pinnedRepo, StringComparison.OrdinalIgnoreCase)).ToList();

            var pkgId = Ids.Package(id);
            graph.Packages.Add(new PackageNode(pkgId, id, producers.Select(p => p.Id).ToList()));
            producerRepoByPackage[id] = producers.Select(p => p.Repo.Name).Distinct().ToList();

            var confidence = producers.Count > 1 ? Confidence.Low : Confidence.Certain;
            foreach (var prod in producers)
                graph.Edges.Add(new Edge(EdgeKind.Produces, prod.Id, pkgId, confidence) { Version = prod.File.Version });
            if (producers.Count > 1)
                graph.Diagnostics.Add(new Diagnostic(DiagnosticKinds.AmbiguousProducer, "warning",
                    [pkgId, .. producers.Select(p => p.Id)],
                    $"Package {id} is produced by {producers.Count} projects: {string.Join(", ", producers.Select(p => p.Id))}. Pin one with packages.producers in depenk.yml."));
        }

        var dependsOn = new Dictionary<(string From, string To), SortedSet<string>>();
        foreach (var p in projects)
        foreach (var r in p.File.PackageReferences)
        {
            graph.Edges.Add(new Edge(EdgeKind.References, p.Id, Ids.Package(r.Id), Confidence.Certain) { Version = r.Version });
            if (ProjectFile.IsUnresolved(r.Version))
                graph.Diagnostics.Add(new Diagnostic(DiagnosticKinds.UnresolvedVersion, "info", [p.Id, Ids.Package(r.Id)],
                    $"{p.Id} references {r.Id} with a version that could not be resolved: {r.Version}"));

            foreach (var producerRepo in producerRepoByPackage.GetValueOrDefault(r.Id, []))
            {
                if (producerRepo == p.Repo.Name) continue;
                var key = (Ids.Repo(p.Repo.Name), Ids.Repo(producerRepo));
                if (!dependsOn.TryGetValue(key, out var via)) dependsOn[key] = via = new SortedSet<string>(StringComparer.Ordinal);
                via.Add(r.Id);
            }
        }
        foreach (var ((from, to), via) in dependsOn.OrderBy(k => k.Key.From).ThenBy(k => k.Key.To))
            graph.Edges.Add(new Edge(EdgeKind.DependsOn, from, to, Confidence.Certain) { ViaPackages = [.. via] });
    }
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet test tests/Depenk.Tests --filter "ProjectClassifierTests|PackageGraphBuilderTests"`
Expected: PASS (8 classifier cases + 6 builder tests).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(scanning): project classification and package-level dependency graph"
```

### Task 5: Route normalizer

**Files:**
- Create: `src/Depenk.Analysis/Routes/RouteNormalizer.cs`
- Test: `tests/Depenk.Tests/Analysis/RouteNormalizerTests.cs`

**Interfaces:**
- Consumes: nothing
- Produces:
  - `RouteNormalizer.Normalize(string route) : string`: the comparison key
  - `RouteNormalizer.Combine(string? prefix, string? template) : string`: the display route with a leading `/`
  - `RouteNormalizer.ReplaceTokens(string template, string controllerName, string actionName) : string`

**Normalization rules (spec §3.1 step 7):**
- strip scheme and host (`https://x:5001/api/a` becomes `api/a`), then the query string and fragment
- strip a leading `~`
- every `{…}` segment piece becomes `{}` (`{id}`, `{id:int}`, `{*rest}`, `{orderId}`, `{id?}`)
- remove empty segments and leading or trailing `/`, and lowercase everything

`Combine`: a template starting with `/` or `~/` is absolute and ignores the prefix; otherwise it's `prefix/template`. The result always starts with `/`, and null or empty parts are skipped.

- [ ] **Step 1: Write failing tests**

`tests/Depenk.Tests/Analysis/RouteNormalizerTests.cs`:

```csharp
using Depenk.Analysis.Routes;

namespace Depenk.Tests.Analysis;

public class RouteNormalizerTests
{
    [Theory]
    [InlineData("/api/orders/{id}", "api/orders/{}")]
    [InlineData("api/Orders/{id:int}", "api/orders/{}")]
    [InlineData("api/orders/{orderId}/lines/{lineId?}", "api/orders/{}/lines/{}")]
    [InlineData("api/files/{*path}", "api/files/{}")]
    [InlineData("api/orders?page={p}&size=10", "api/orders")]
    [InlineData("https://orders.internal:5001/api/orders/{id}", "api/orders/{}")]
    [InlineData("~/api//orders/", "api/orders")]
    [InlineData("  /api/orders#frag ", "api/orders")]
    [InlineData("api/orders/{id}.json", "api/orders/{}.json")]
    [InlineData("", "")]
    public void Normalize(string input, string expected) => Assert.Equal(expected, RouteNormalizer.Normalize(input));

    [Theory]
    [InlineData("api/orders", "{id}", "/api/orders/{id}")]
    [InlineData("api/orders", "/health", "/health")]
    [InlineData("api/orders", "~/v2/orders", "/v2/orders")]
    [InlineData(null, "api/x", "/api/x")]
    [InlineData("api/orders/", null, "/api/orders")]
    [InlineData(null, null, "/")]
    public void Combine(string? prefix, string? template, string expected) =>
        Assert.Equal(expected, RouteNormalizer.Combine(prefix, template));

    [Fact]
    public void ReplacesControllerAndActionTokens()
    {
        Assert.Equal("api/orders/list", RouteNormalizer.ReplaceTokens("api/[controller]/[action]", "OrdersController", "List"));
        Assert.Equal("api/orders", RouteNormalizer.ReplaceTokens("api/[Controller]", "Orders", "Get"));
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Depenk.Tests --filter RouteNormalizerTests`
Expected: build FAILS: `The type or namespace name 'Routes' does not exist in the namespace 'Depenk.Analysis'`.

- [ ] **Step 3: Implement**

`src/Depenk.Analysis/Routes/RouteNormalizer.cs`:

```csharp
using System.Text.RegularExpressions;

namespace Depenk.Analysis.Routes;

public static partial class RouteNormalizer
{
    public static string Normalize(string route)
    {
        var r = route.Trim();
        var scheme = r.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
        {
            var pathStart = r.IndexOf('/', scheme + 3);
            r = pathStart < 0 ? "" : r[pathStart..];
        }
        var cut = r.IndexOfAny(['?', '#']);
        if (cut >= 0) r = r[..cut];
        r = r.TrimStart('~');
        r = Placeholder().Replace(r, "{}");
        var segments = r.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join('/', segments).ToLowerInvariant();
    }

    public static string Combine(string? prefix, string? template)
    {
        var t = template?.Trim() ?? "";
        if (t.StartsWith("~/", StringComparison.Ordinal)) t = t[1..];
        string[] parts = t.StartsWith('/') ? [t] : [prefix ?? "", t];
        var joined = string.Join('/', parts.SelectMany(p => p.Split('/', StringSplitOptions.RemoveEmptyEntries)));
        return "/" + joined;
    }

    public static string ReplaceTokens(string template, string controllerName, string actionName)
    {
        var controller = controllerName.EndsWith("Controller", StringComparison.Ordinal)
            ? controllerName[..^"Controller".Length] : controllerName;
        var withController = Regex.Replace(template, @"\[controller\]", controller.ToLowerInvariant(), RegexOptions.IgnoreCase);
        return Regex.Replace(withController, @"\[action\]", actionName.ToLowerInvariant(), RegexOptions.IgnoreCase);
    }

    [GeneratedRegex(@"\{[^{}]*\}")]
    private static partial Regex Placeholder();
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet test tests/Depenk.Tests --filter RouteNormalizerTests`
Expected: PASS (18 cases).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(analysis): route normalization, combination and token replacement"
```

### Task 6: SourceSet + endpoint finders (controllers, minimal APIs)

**Files:**
- Create: `src/Depenk.Analysis/SourceSet.cs`, `src/Depenk.Analysis/SyntaxHelpers.cs`
- Create: `src/Depenk.Analysis/Endpoints/IEndpointFinder.cs`, `Endpoints/EndpointParameters.cs`, `Endpoints/ControllerEndpointFinder.cs`, `Endpoints/MinimalApiEndpointFinder.cs`
- Create: `tests/Depenk.Tests/TestUtil/Src.cs`
- Test: `tests/Depenk.Tests/Analysis/EndpointFinderTests.cs`

**Interfaces:**
- Consumes: `RouteNormalizer` (Task 5); Core model (Task 1)
- Produces:
  - `sealed record SourceDoc(string RelativePath, SyntaxTree Tree)`
  - `sealed class SourceSet(string Repo, string ProjectId, string ProjectName, IReadOnlyList<SourceDoc> Docs)` with `static SourceSet Load(string workspace, string repo, string projectId, string projectName, string projectDir, Action<string, Exception> onError)` and `IEnumerable<(SourceDoc Doc, T Node)> All<T>() where T : SyntaxNode`
  - `SyntaxHelpers`:
    - `AttrName(AttributeSyntax) : string` (without the `Attribute` suffix or namespace)
    - `FirstStringArg(AttributeSyntax) : string?`
    - `Line(SyntaxNode) : int` and `Line(SyntaxToken) : int` (1-based)
    - `TypeName(TypeSyntax) : string` (the type's source text, without `global::`)
    - `StringValue(ExpressionSyntax) : string?`: literal, interpolated (holes become `{name}`), `+` concatenation (non-literal parts become `{}`), and `const` field lookup in the same document
  - `interface IEndpointFinder { IEnumerable<EndpointNode> Find(SourceSet src); }`
  - `ControllerEndpointFinder`, `MinimalApiEndpointFinder`
  - Test helper: `Src.Set(params (string Path, string Code)[] files) : SourceSet` (repo `"orders"`, project `"proj:orders/Orders.Api"`, name `"Orders.Api"`)

**Endpoint rules:**
- **Controller** = a class whose name ends with `Controller`, or that has `[ApiController]`, or whose base type is `ControllerBase` or `Controller`.
  - class `[Route]` = the prefix, with tokens replaced
  - each public method with `[HttpGet|Post|Put|Delete|Patch("tmpl")]` (template optional) or `[Route]` + `[Http*]` becomes one endpoint per HTTP attribute
  - handler = `{Class}.{Method}`
- **Parameters:**
  - source = the `[FromBody|FromQuery|FromRoute|FromHeader|FromForm]` attribute, or `route` if the name appears as `{name}` or `{name:…}` in the route, or `body` if a POST/PUT/PATCH method has a non-simple type, otherwise `query`
  - skip `CancellationToken`, `[FromServices]` and `HttpContext`
  - required = no default value and the type is not nullable (`?`)
- **Responses:**
  - `[ProducesResponseType(typeof(T), 200)]` / `[ProducesResponseType<T>(200)]` gives a status and type
  - otherwise the method's return type text becomes `ResponseType(200, returnTypeText)`
  - `void`, `Task` and `IActionResult` without attributes produce no response entry
  - unwrapping happens later, in the model extractor (Task 9)
- **Minimal APIs:** `x.MapGet|MapPost|MapPut|MapDelete|MapPatch("tmpl", handler)`.
  - A prefix comes from `var g = app.MapGroup("/api/x")` when the receiver is the identifier `g` in the same method, and chained `MapGroup` calls stack.
  - handler = `{EnclosingType}.lambda@{line}`, or the method group name.
  - parameters come from the lambda's parameters, with the same rules as controllers.
  - responses come from lambda return types, `TypedResults.Ok<T>` or `Results.Ok(x)`, but only when the lambda declares a return type; otherwise the list is empty.

- [ ] **Step 1: Write the test helper**

`tests/Depenk.Tests/TestUtil/Src.cs`:

```csharp
using Depenk.Analysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Depenk.Tests.TestUtil;

public static class Src
{
    public static SourceSet Set(params (string Path, string Code)[] files) =>
        SetFor("orders", "Orders.Api", files);

    public static SourceSet SetFor(string repo, string project, params (string Path, string Code)[] files) =>
        new(repo, $"proj:{repo}/{project}", project,
            files.Select(f => new SourceDoc(f.Path, CSharpSyntaxTree.ParseText(f.Code, path: f.Path))).ToList());
}
```

- [ ] **Step 2: Write failing tests**

`tests/Depenk.Tests/Analysis/EndpointFinderTests.cs`:

```csharp
using Depenk.Analysis.Endpoints;
using Depenk.Core.Model;
using Depenk.Tests.TestUtil;

namespace Depenk.Tests.Analysis;

public class EndpointFinderTests
{
    private const string OrdersController = """
        using Microsoft.AspNetCore.Mvc;
        namespace Acme.Orders.Api;

        [ApiController]
        [Route("api/[controller]")]
        public class OrdersController : ControllerBase
        {
            [HttpGet("{id:guid}")]
            [ProducesResponseType(typeof(OrderDto), 200)]
            [ProducesResponseType(404)]
            public async Task<IActionResult> Get(Guid id, bool includeLines = true, CancellationToken ct = default) => Ok();

            [HttpGet]
            public Task<ActionResult<List<OrderDto>>> List([FromQuery] int page, string? status) => null!;

            [HttpPost]
            public Task<ActionResult<OrderDto>> Create(CreateOrderRequest request, [FromServices] IClock clock) => null!;

            [HttpPut("/v2/orders/{id}/status")]
            public Task SetStatus(Guid id, [FromBody] OrderStatus status) => Task.CompletedTask;

            private void Helper() { }
        }
        """;

    [Fact]
    public void FindsControllerEndpoints_WithRoutesParamsAndResponses()
    {
        var eps = new ControllerEndpointFinder().Find(Src.Set(("orders/src/Orders.Api/OrdersController.cs", OrdersController)))
            .ToDictionary(e => $"{e.Verb} {e.Route}");

        Assert.Equal(["GET /api/orders/{id:guid}", "GET /api/orders", "POST /api/orders", "PUT /v2/orders/{id}/status"], eps.Keys);

        var get = eps["GET /api/orders/{id:guid}"];
        Assert.Equal("ep:orders:GET:/api/orders/{id:guid}", get.Id);
        Assert.Equal("api/orders/{}", get.NormalizedRoute);
        Assert.Equal("OrdersController.Get", get.Handler);
        Assert.Equal(new SourceLocation("orders/src/Orders.Api/OrdersController.cs", 11), get.Location);
        Assert.Equal([new EndpointParameter("id", "route", "Guid", true, null),
                      new EndpointParameter("includeLines", "query", "bool", false, "true")], get.Parameters);
        Assert.Equal([new ResponseType(200, "OrderDto"), new ResponseType(404, "")], get.Responses);

        var list = eps["GET /api/orders"];
        Assert.Equal([new EndpointParameter("page", "query", "int", true, null),
                      new EndpointParameter("status", "query", "string?", false, null)], list.Parameters);
        Assert.Equal([new ResponseType(200, "Task<ActionResult<List<OrderDto>>>")], list.Responses);

        var create = eps["POST /api/orders"];
        Assert.Equal([new EndpointParameter("request", "body", "CreateOrderRequest", true, null)], create.Parameters);

        var put = eps["PUT /v2/orders/{id}/status"];
        Assert.Equal("body", put.Parameters.Single(p => p.Name == "status").Source);
        Assert.Empty(put.Responses);
    }

    [Fact]
    public void IgnoresNonControllerClasses()
    {
        var eps = new ControllerEndpointFinder().Find(Src.Set(("a.cs", """
            public class PlainService { [HttpGet("x")] public void X() {} }
            """)));
        Assert.Empty(eps);
    }

    [Fact]
    public void FindsMinimalApis_WithGroups()
    {
        var eps = new MinimalApiEndpointFinder().Find(Src.Set(("orders/src/Orders.Api/Program.cs", """
            var app = WebApplication.Create();
            var api = app.MapGroup("/api");
            var refunds = api.MapGroup("refunds");
            refunds.MapPost("/", (CreateRefund body) => Results.Ok());
            refunds.MapGet("{id}", (Guid id) => TypedResults.Ok(new RefundDto()));
            app.MapGet("/health", () => "ok");
            app.MapDelete("/api/refunds/{id}", RefundHandlers.Delete);
            """))).ToList();

        Assert.Equal(["POST /api/refunds", "GET /api/refunds/{id}", "GET /health", "DELETE /api/refunds/{id}"],
            eps.Select(e => $"{e.Verb} {e.Route}"));
        Assert.Equal([new EndpointParameter("body", "body", "CreateRefund", true, null)], eps[0].Parameters);
        Assert.Equal("route", eps[1].Parameters.Single().Source);
        Assert.StartsWith("Program.lambda@", eps[0].Handler);
        Assert.Equal("RefundHandlers.Delete", eps[3].Handler);
    }

    [Fact]
    public void ResolvesConstStringRoutes()
    {
        var eps = new ControllerEndpointFinder().Find(Src.Set(("c.cs", """
            [Route(Routes.Base)]
            public class PingController : ControllerBase
            {
                [HttpGet(Routes.Ping)] public string Ping() => "";
            }
            public static class Routes { public const string Base = "api"; public const string Ping = "ping/" + "{n}"; }
            """)));
        Assert.Equal("/api/ping/{n}", eps.Single().Route);
    }
}
```

- [ ] **Step 3: Run to verify failure**

Run: `dotnet test tests/Depenk.Tests --filter EndpointFinderTests`
Expected: build FAILS: `The type or namespace name 'SourceSet' could not be found`.

- [ ] **Step 4: Implement SourceSet and SyntaxHelpers**

`src/Depenk.Analysis/SourceSet.cs`:

```csharp
using Depenk.Scanning;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Depenk.Analysis;

public sealed record SourceDoc(string RelativePath, SyntaxTree Tree);

public sealed class SourceSet(string repo, string projectId, string projectName, IReadOnlyList<SourceDoc> docs)
{
    public string Repo { get; } = repo;
    public string ProjectId { get; } = projectId;
    public string ProjectName { get; } = projectName;
    public IReadOnlyList<SourceDoc> Docs { get; } = docs;

    public static SourceSet Load(string workspace, string repo, string projectId, string projectName, string projectDir,
        Action<string, Exception> onError)
    {
        var docs = new List<SourceDoc>();
        foreach (var file in PathUtil.EnumerateFiles(projectDir, "*.cs").Order(StringComparer.Ordinal))
        {
            var rel = PathUtil.Rel(workspace, file);
            try { docs.Add(new SourceDoc(rel, CSharpSyntaxTree.ParseText(File.ReadAllText(file), path: rel))); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { onError(rel, ex); }
        }
        return new SourceSet(repo, projectId, projectName, docs);
    }

    public IEnumerable<(SourceDoc Doc, T Node)> All<T>() where T : SyntaxNode =>
        Docs.SelectMany(d => d.Tree.GetRoot().DescendantNodes().OfType<T>().Select(n => (d, n)));
}
```

Roslyn always returns a tree, even for invalid code. Files with syntax errors still yield whatever nodes did parse, and Task 11 reports a `parseError` diagnostic for any document with error-severity diagnostics.

`src/Depenk.Analysis/SyntaxHelpers.cs`:

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Depenk.Analysis;

public static class SyntaxHelpers
{
    public static string AttrName(AttributeSyntax a)
    {
        var name = a.Name switch
        {
            QualifiedNameSyntax q => q.Right.Identifier.Text,
            GenericNameSyntax g => g.Identifier.Text,
            AliasQualifiedNameSyntax al => al.Name.Identifier.Text,
            _ => a.Name.ToString(),
        };
        return name.EndsWith("Attribute", StringComparison.Ordinal) ? name[..^"Attribute".Length] : name;
    }

    public static IEnumerable<AttributeSyntax> Attrs(MemberDeclarationSyntax m) =>
        m.AttributeLists.SelectMany(l => l.Attributes);

    public static IEnumerable<AttributeSyntax> Attrs(ParameterSyntax p) => p.AttributeLists.SelectMany(l => l.Attributes);

    public static string? FirstStringArg(AttributeSyntax a) =>
        a.ArgumentList?.Arguments.Where(x => x.NameEquals is null && x.NameColon is null)
            .Select(x => StringValue(x.Expression)).FirstOrDefault(s => s is not null);

    public static int Line(SyntaxNode n) => n.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
    public static int Line(SyntaxToken t) => t.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    public static string TypeName(TypeSyntax t) => t.ToString().Replace("global::", "");

    /// <summary>Best-effort static string value of an expression.</summary>
    public static string? StringValue(ExpressionSyntax e) => e switch
    {
        LiteralExpressionSyntax l when l.IsKind(SyntaxKind.StringLiteralExpression) => l.Token.ValueText,
        InterpolatedStringExpressionSyntax i => string.Concat(i.Contents.Select(c => c switch
        {
            InterpolatedStringTextSyntax t => t.TextToken.ValueText,
            InterpolationSyntax h => "{" + HoleName(h.Expression) + "}",
            _ => "",
        })),
        BinaryExpressionSyntax b when b.IsKind(SyntaxKind.AddExpression) =>
            (StringValue(b.Left) ?? "{}") + (StringValue(b.Right) ?? "{}"),
        IdentifierNameSyntax or MemberAccessExpressionSyntax => ConstValue(e),
        _ => null,
    };

    private static string HoleName(ExpressionSyntax e) => e switch
    {
        IdentifierNameSyntax id => id.Identifier.Text,
        MemberAccessExpressionSyntax m => m.Name.Identifier.Text,
        _ => "",
    };

    /// <summary>Resolves `Name` or `Type.Name` to a const string declared in the same syntax tree.</summary>
    private static string? ConstValue(ExpressionSyntax e)
    {
        var name = e switch
        {
            IdentifierNameSyntax id => id.Identifier.Text,
            MemberAccessExpressionSyntax m => m.Name.Identifier.Text,
            _ => null,
        };
        if (name is null) return null;
        var declarator = e.SyntaxTree.GetRoot().DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .FirstOrDefault(v => v.Identifier.Text == name
                                 && v.Parent?.Parent is FieldDeclarationSyntax f
                                 && f.Modifiers.Any(SyntaxKind.ConstKeyword));
        return declarator?.Initializer?.Value is { } init && !ReferenceEquals(init, e) ? StringValue(init) : null;
    }
}
```

- [ ] **Step 5: Implement the endpoint finders**

`src/Depenk.Analysis/Endpoints/IEndpointFinder.cs`:

```csharp
using Depenk.Core.Model;

namespace Depenk.Analysis.Endpoints;

public interface IEndpointFinder
{
    IEnumerable<EndpointNode> Find(SourceSet src);
}
```

`src/Depenk.Analysis/Endpoints/EndpointParameters.cs` (shared by both finders):

```csharp
using Depenk.Core.Model;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Depenk.Analysis.SyntaxHelpers;

namespace Depenk.Analysis.Endpoints;

internal static class EndpointParameters
{
    private static readonly HashSet<string> Skipped = ["CancellationToken", "HttpContext", "HttpRequest", "ClaimsPrincipal"];
    private static readonly HashSet<string> Simple =
        ["string", "int", "long", "short", "bool", "decimal", "double", "float", "Guid", "DateTime", "DateTimeOffset",
         "DateOnly", "TimeOnly", "byte", "char", "uint", "ulong"];
    private static readonly Dictionary<string, string> BindingAttrs = new()
    {
        ["FromBody"] = "body", ["FromQuery"] = "query", ["FromRoute"] = "route",
        ["FromHeader"] = "header", ["FromForm"] = "form",
    };

    public static List<EndpointParameter> From(IEnumerable<ParameterSyntax> ps, string verb, string route)
    {
        var list = new List<EndpointParameter>();
        foreach (var p in ps)
        {
            if (p.Type is null) continue;
            var type = TypeName(p.Type);
            var attrs = Attrs(p).Select(AttrName).ToList();
            if (attrs.Contains("FromServices") || Skipped.Contains(type.TrimEnd('?'))) continue;

            var name = p.Identifier.Text;
            var source = attrs.Select(a => BindingAttrs.GetValueOrDefault(a)).FirstOrDefault(s => s is not null)
                         ?? (RouteHas(route, name) ? "route"
                             : verb is "POST" or "PUT" or "PATCH" && !Simple.Contains(type.TrimEnd('?')) ? "body"
                             : "query");
            var required = p.Default is null && !type.EndsWith('?');
            list.Add(new EndpointParameter(name, source, type, required, p.Default?.Value.ToString()));
        }
        return list;
    }

    private static bool RouteHas(string route, string name) =>
        route.Contains("{" + name + "}", StringComparison.OrdinalIgnoreCase)
        || route.Contains("{" + name + ":", StringComparison.OrdinalIgnoreCase)
        || route.Contains("{" + name + "?}", StringComparison.OrdinalIgnoreCase);
}
```

`src/Depenk.Analysis/Endpoints/ControllerEndpointFinder.cs`:

```csharp
using Depenk.Analysis.Routes;
using Depenk.Core;
using Depenk.Core.Model;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Depenk.Analysis.SyntaxHelpers;

namespace Depenk.Analysis.Endpoints;

public sealed class ControllerEndpointFinder : IEndpointFinder
{
    private static readonly Dictionary<string, string> VerbAttrs = new()
    {
        ["HttpGet"] = "GET", ["HttpPost"] = "POST", ["HttpPut"] = "PUT", ["HttpDelete"] = "DELETE", ["HttpPatch"] = "PATCH",
    };

    public IEnumerable<EndpointNode> Find(SourceSet src)
    {
        foreach (var (doc, cls) in src.All<ClassDeclarationSyntax>())
        {
            if (!IsController(cls)) continue;
            var className = cls.Identifier.Text;
            var classRoute = Attrs(cls).Where(a => AttrName(a) == "Route").Select(FirstStringArg).FirstOrDefault();

            foreach (var m in cls.Members.OfType<MethodDeclarationSyntax>())
            {
                if (!m.Modifiers.Any(SyntaxKind.PublicKeyword)) continue;
                var attrs = Attrs(m).ToList();
                var methodRoute = attrs.Where(a => AttrName(a) == "Route").Select(FirstStringArg).FirstOrDefault();
                foreach (var verbAttr in attrs.Where(a => VerbAttrs.ContainsKey(AttrName(a))))
                {
                    var verb = VerbAttrs[AttrName(verbAttr)];
                    var template = FirstStringArg(verbAttr) ?? methodRoute;
                    var prefix = classRoute is null ? null : RouteNormalizer.ReplaceTokens(classRoute, className, m.Identifier.Text);
                    var tmpl = template is null ? null : RouteNormalizer.ReplaceTokens(template, className, m.Identifier.Text);
                    var route = RouteNormalizer.Combine(prefix, tmpl);

                    yield return new EndpointNode(
                        Ids.Endpoint(src.Repo, verb, route), src.Repo, src.ProjectId, verb, route,
                        RouteNormalizer.Normalize(route), $"{className}.{m.Identifier.Text}",
                        EndpointParameters.From(m.ParameterList.Parameters, verb, route),
                        Responses(m, attrs),
                        new SourceLocation(doc.RelativePath, Line(m.Identifier)));
                }
            }
        }
    }

    private static bool IsController(ClassDeclarationSyntax c) =>
        c.Identifier.Text.EndsWith("Controller", StringComparison.Ordinal)
        || Attrs(c).Any(a => AttrName(a) == "ApiController")
        || c.BaseList?.Types.Any(t => t.Type.ToString() is "ControllerBase" or "Controller") == true;

    private static List<ResponseType> Responses(MethodDeclarationSyntax m, List<AttributeSyntax> attrs)
    {
        var declared = attrs.Where(a => AttrName(a) == "ProducesResponseType").Select(a =>
        {
            var args = a.ArgumentList?.Arguments ?? default;
            var status = args.Select(x => x.Expression).OfType<LiteralExpressionSyntax>()
                .Where(l => l.IsKind(SyntaxKind.NumericLiteralExpression))
                .Select(l => (int)l.Token.Value!).DefaultIfEmpty(200).First();
            var type = args.Select(x => x.Expression).OfType<TypeOfExpressionSyntax>().Select(t => TypeName(t.Type)).FirstOrDefault()
                       ?? (a.Name as GenericNameSyntax)?.TypeArgumentList.Arguments.Select(TypeName).FirstOrDefault()
                       ?? "";
            return new ResponseType(status, type);
        }).ToList();
        if (declared.Count > 0) return declared;

        var ret = TypeName(m.ReturnType);
        return ret is "void" or "Task" or "ValueTask" or "IActionResult" or "Task<IActionResult>" or "ActionResult"
            or "Task<ActionResult>" or "IResult" or "Task<IResult>"
            ? [] : [new ResponseType(200, ret)];
    }
}
```

`src/Depenk.Analysis/Endpoints/MinimalApiEndpointFinder.cs`:

```csharp
using Depenk.Analysis.Routes;
using Depenk.Core;
using Depenk.Core.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Depenk.Analysis.SyntaxHelpers;

namespace Depenk.Analysis.Endpoints;

public sealed class MinimalApiEndpointFinder : IEndpointFinder
{
    private static readonly Dictionary<string, string> MapMethods = new()
    {
        ["MapGet"] = "GET", ["MapPost"] = "POST", ["MapPut"] = "PUT", ["MapDelete"] = "DELETE", ["MapPatch"] = "PATCH",
    };

    public IEnumerable<EndpointNode> Find(SourceSet src)
    {
        foreach (var (doc, inv) in src.All<InvocationExpressionSyntax>())
        {
            if (inv.Expression is not MemberAccessExpressionSyntax ma
                || !MapMethods.TryGetValue(ma.Name.Identifier.Text, out var verb)) continue;
            var args = inv.ArgumentList.Arguments;
            if (args.Count < 2 || StringValue(args[0].Expression) is not { } template) continue;

            // inside a group, "/" and "/x" are relative to the group prefix
            var prefix = GroupPrefix(ma.Expression, inv);
            var route = RouteNormalizer.Combine(prefix, prefix is null ? template : template.TrimStart('/'));
            var handlerExpr = args[1].Expression;
            var (handler, parameters) = handlerExpr switch
            {
                ParenthesizedLambdaExpressionSyntax l =>
                    ($"{EnclosingTypeName(inv, doc)}.lambda@{Line(l)}", l.ParameterList.Parameters.AsEnumerable()),
                SimpleLambdaExpressionSyntax s => ($"{EnclosingTypeName(inv, doc)}.lambda@{Line(s)}", new[] { s.Parameter }.AsEnumerable()),
                _ => (handlerExpr.ToString(), Enumerable.Empty<ParameterSyntax>()),
            };

            yield return new EndpointNode(
                Ids.Endpoint(src.Repo, verb, route), src.Repo, src.ProjectId, verb, route,
                RouteNormalizer.Normalize(route), handler, EndpointParameters.From(parameters, verb, route),
                LambdaResponses(handlerExpr), new SourceLocation(doc.RelativePath, Line(inv)));
        }
    }

    /// <summary>Resolves the receiver to a MapGroup prefix: chained calls, or a local assigned from MapGroup.</summary>
    private static string? GroupPrefix(ExpressionSyntax receiver, SyntaxNode context, int depth = 0)
    {
        if (depth > 10) return null;
        switch (receiver)
        {
            case InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "MapGroup" } gm } g:
                var own = g.ArgumentList.Arguments.Count > 0 ? StringValue(g.ArgumentList.Arguments[0].Expression) : null;
                var outer = GroupPrefix(gm.Expression, context, depth + 1);
                return RouteNormalizer.Combine(outer, own is null ? null : own.TrimStart('/')).TrimStart('/');
            case IdentifierNameSyntax id:
                var scope = context.Ancestors().FirstOrDefault(a => a is BlockSyntax or CompilationUnitSyntax) ?? context.SyntaxTree.GetRoot();
                var decl = scope.DescendantNodes().OfType<VariableDeclaratorSyntax>()
                    .FirstOrDefault(v => v.Identifier.Text == id.Identifier.Text);
                return decl?.Initializer?.Value is { } init ? GroupPrefix(init, decl, depth + 1) : null;
            default:
                return null;
        }
    }

    private static string EnclosingTypeName(SyntaxNode n, SourceDoc doc) =>
        n.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()?.Identifier.Text
        ?? Path.GetFileNameWithoutExtension(doc.RelativePath);

    private static List<ResponseType> LambdaResponses(ExpressionSyntax handler) =>
        handler is ParenthesizedLambdaExpressionSyntax { ReturnType: { } rt } ? [new ResponseType(200, TypeName(rt))] : [];
}
```

- [ ] **Step 6: Run tests**

Run: `dotnet test tests/Depenk.Tests --filter EndpointFinderTests`
Expected: PASS (4 tests). Note: `Location.Line` is the line of the method **name** (`Line(m.Identifier)`), not the first attribute. That's why `Get` is on line 11.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat(analysis): source sets and controller/minimal API endpoint finders"
```

### Task 7: Client method finder + route strategies (Refit, generated, configured wrapper, generic)

**Files:**
- Create: `src/Depenk.Analysis/DeclaredTypes.cs`
- Create: `src/Depenk.Analysis/Clients/RouteHit.cs`, `Clients/IRouteStrategy.cs`, `Clients/RefitStrategy.cs`, `Clients/GeneratedClientStrategy.cs`, `Clients/ConfiguredWrapperStrategy.cs`, `Clients/GenericHttpStrategy.cs`, `Clients/ClientMethodFinder.cs`
- Test: `tests/Depenk.Tests/Analysis/DeclaredTypesTests.cs`, `tests/Depenk.Tests/Analysis/ClientMethodFinderTests.cs`

**Interfaces:**
- Consumes: `SourceSet`, `SourceDoc`, `SyntaxHelpers` (Task 6); `RouteNormalizer` (Task 5); `DepenkConfig`, `HttpWrapperConfig`, `Glob` (Task 2)
- Produces:
  - `DeclaredTypes.Of(ExpressionSyntax receiver, SyntaxNode context) : string?`: the declared type text of `x`, `_x` or `this.x`, taken from a parameter, local (explicit type, `new T(...)`, or `GetRequiredService<T>()`/`GetService<T>()`), field, property or primary-constructor parameter (Task 10 reuses this)
  - `DeclaredTypes.BaseTypes(TypeDeclarationSyntax) : List<string>`
  - `sealed record RouteHit(string Verb, string Route, string Strategy, Confidence Confidence)`
  - `interface IRouteStrategy { RouteHit? Match(MethodDeclarationSyntax method, TypeDeclarationSyntax owner); }`
  - `sealed record ClientScanResult(List<ClientMethodNode> Methods, bool AnyHit)`
  - `ClientMethodFinder(DepenkConfig config)` with `ClientScanResult Find(SourceSet src)`

**Rules:**
- **Strategies**, run in this order (the first hit wins):
  1. `RefitStrategy`: `[Get|Post|Put|Delete|Patch|Head("route")]` on the method. `high`.
  2. `GeneratedClientStrategy`:
     - **NSwag:** the body uses `urlBuilder_.Append(x)`. Literal pieces are kept and non-literal pieces become `{}`. The verb comes from `new HttpMethod("GET")` or `HttpMethod.Get`. `high`.
     - **Kiota:** the owner calls `base(requestAdapter, "{+baseurl}/api/orders{?page}", …)`, and the method name starts with a verb (`GetAsync`, `PostAsync`…). `{+baseurl}` and `{?…}` are stripped. `high`.
  3. `ConfiguredWrapperStrategy`: calls whose receiver's declared type, or (for calls with no receiver) the owner's base type, matches `httpWrappers[].type`, and whose method name matches a `methods` glob. The route is `routeArgument`. `high`.
  4. `GenericHttpStrategy`: `medium`. Either:
     - a call named `Get*|Post*|Put*|Delete*|Patch*` with a route-like argument, or
     - `new HttpRequestMessage(HttpMethod.X, route)`

     The route argument may be a local whose initializer is a string.
- **Route-like** means a non-empty string with no whitespace that contains `/` or `{`. A string that looks like a media type (`application/…`, `text/…`, `multipart/…`, `image/…`) is **not** route-like.
- **Types scanned:** classes and interfaces. Class methods must be `public` and non-static; every interface method counts.
- **Second pass:**
  - An interface method with no hit takes the hit of the same-named method in a class that implements the interface (the class's base list contains the interface name).
  - A class method with no hit whose body calls a same-named method of the same class takes that method's hit (this covers overload delegation).
- **Route prefix:** if `routes.prefixes[projectName]` is set, route = `Combine(prefix, route.TrimStart('/'))`. Otherwise route = `Combine(null, route)`.
- **What gets emitted:** a node for every method with a hit. Also a node with `Verb = null`, `Strategy = "none"`, `Confidence.Low` for any public method returning `Task`/`Task<…>`/`ValueTask…` in a type that has at least one hit. The linker turns those into `unresolvedClientMethod` diagnostics. IDs are deduplicated per `Ids.ClientMethod`, preferring the node that has a hit.
- Signature = `"{ReturnType} {Name}({parameter list text})"`.

- [ ] **Step 1: Write failing tests**

`tests/Depenk.Tests/Analysis/DeclaredTypesTests.cs`:

```csharp
using Depenk.Analysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Depenk.Tests.Analysis;

public class DeclaredTypesTests
{
    private static string? TypeOfReceiver(string code, string receiverText)
    {
        var root = CSharpSyntaxTree.ParseText(code).GetRoot();
        var inv = root.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .First(i => i.Expression is MemberAccessExpressionSyntax m && m.Expression.ToString() == receiverText);
        return DeclaredTypes.Of(((MemberAccessExpressionSyntax)inv.Expression).Expression, inv);
    }

    [Theory]
    [InlineData("class A { private readonly IOrdersClient _orders; void M() { _orders.Go(); } }", "_orders", "IOrdersClient")]
    [InlineData("class A { IOrdersClient Orders { get; } void M() { this.Orders.Go(); } }", "this.Orders", "IOrdersClient")]
    [InlineData("class A { void M(IOrdersClient c) { c.Go(); } }", "c", "IOrdersClient")]
    [InlineData("class A(IOrdersClient orders) { void M() { orders.Go(); } }", "orders", "IOrdersClient")]
    [InlineData("class A { void M() { var c = new OrdersClient(null); c.Go(); } }", "c", "OrdersClient")]
    [InlineData("class A { void M(IServiceProvider sp) { var c = sp.GetRequiredService<IOrdersClient>(); c.Go(); } }", "c", "IOrdersClient")]
    [InlineData("class A { void M() { IOrdersClient c = Make(); c.Go(); } }", "c", "IOrdersClient")]
    [InlineData("class A { void M() { var c = Make(); c.Go(); } }", "c", null)]
    public void ResolvesDeclaredType(string code, string receiver, string? expected) =>
        Assert.Equal(expected, TypeOfReceiver(code, receiver));
}
```

`tests/Depenk.Tests/Analysis/ClientMethodFinderTests.cs`:

```csharp
using Depenk.Analysis.Clients;
using Depenk.Core.Model;
using Depenk.Scanning.Config;
using Depenk.Tests.TestUtil;

namespace Depenk.Tests.Analysis;

public class ClientMethodFinderTests
{
    private static Dictionary<string, ClientMethodNode> Find(string code, DepenkConfig? cfg = null) =>
        new ClientMethodFinder(cfg ?? new DepenkConfig())
            .Find(Src.SetFor("orders", "Orders.Client", ("orders/src/Orders.Client/Client.cs", code)))
            .Methods.ToDictionary(m => $"{m.TypeName}.{m.MethodName}");

    [Fact]
    public void Refit()
    {
        var m = Find("""
            public interface IOrdersApi
            {
                [Get("/api/orders/{id}")] Task<OrderDto> GetOrder(Guid id);
                [Post("/api/orders")] Task<OrderDto> Create([Body] CreateOrderRequest r);
            }
            """);
        Assert.Equal(("GET", "/api/orders/{id}", "api/orders/{}", "refit", Confidence.High),
            (m["IOrdersApi.GetOrder"].Verb, m["IOrdersApi.GetOrder"].Route, m["IOrdersApi.GetOrder"].NormalizedRoute,
             m["IOrdersApi.GetOrder"].Strategy, m["IOrdersApi.GetOrder"].Confidence));
        Assert.Equal("cm:Orders.Client:IOrdersApi.GetOrder", m["IOrdersApi.GetOrder"].Id);
        Assert.Equal("POST", m["IOrdersApi.Create"].Verb);
        Assert.Equal("Task<OrderDto> GetOrder(Guid id)", m["IOrdersApi.GetOrder"].Signature);
    }

    [Fact]
    public void Generic_HttpClient_Interpolated_LocalVariable_AndRequestMessage_WithInterfaceMapping()
    {
        var m = Find("""
            public interface IOrdersClient
            {
                Task<OrderDto> GetOrderAsync(Guid id);
                Task<List<OrderDto>> ListAsync(int page);
                Task SetStatusAsync(Guid id, OrderStatus s);
                Task<Stats> StatsAsync();
            }
            public class OrdersClient(HttpClient http) : IOrdersClient
            {
                public Task<OrderDto> GetOrderAsync(Guid id) => http.GetFromJsonAsync<OrderDto>($"api/orders/{id}")!;
                public Task<List<OrderDto>> ListAsync(int page)
                {
                    var url = "api/orders?page=" + page;
                    return http.GetFromJsonAsync<List<OrderDto>>(url)!;
                }
                public async Task SetStatusAsync(Guid id, OrderStatus s)
                {
                    using var req = new HttpRequestMessage(HttpMethod.Put, $"api/orders/{id}/status");
                    req.Content = new StringContent("", null, "application/json");
                    await http.SendAsync(req);
                }
                public Task<Stats> StatsAsync() => Compute();
                private Task<Stats> Compute() => null!;
            }
            """);
        Assert.Equal(("GET", "/api/orders/{id}", "generic-http", Confidence.Medium),
            (m["OrdersClient.GetOrderAsync"].Verb, m["OrdersClient.GetOrderAsync"].Route,
             m["OrdersClient.GetOrderAsync"].Strategy, m["OrdersClient.GetOrderAsync"].Confidence));
        Assert.Equal("api/orders", m["OrdersClient.ListAsync"].NormalizedRoute);
        Assert.Equal(("PUT", "api/orders/{}/status"), (m["OrdersClient.SetStatusAsync"].Verb, m["OrdersClient.SetStatusAsync"].NormalizedRoute));
        // interface methods inherit the implementation's hit
        Assert.Equal("api/orders/{}", m["IOrdersClient.GetOrderAsync"].NormalizedRoute);
        // unresolved async method is still emitted, low confidence
        Assert.Equal((null, "none", Confidence.Low), (m["OrdersClient.StatsAsync"].Verb, m["OrdersClient.StatsAsync"].Strategy, m["OrdersClient.StatsAsync"].Confidence));
        Assert.False(m.ContainsKey("OrdersClient.Compute"));
    }

    [Fact]
    public void ConfiguredWrapper_ByFieldType_AndByBaseClass()
    {
        var cfg = new DepenkConfig();
        cfg.HttpWrappers.Add(new HttpWrapperConfig
        {
            Type = "*.IApiHttpClient", RouteArgument = 0,
            Methods = new() { ["Fetch*"] = "GET", ["Send*"] = "POST" },
        });
        cfg.HttpWrappers.Add(new HttpWrapperConfig
        {
            Type = "ApiClientBase", RouteArgument = 1, Methods = new() { ["Remove"] = "DELETE" },
        });
        var m = Find("""
            public class OrdersClient
            {
                private readonly Acme.Http.IApiHttpClient _api;
                public Task<OrderDto> Get(Guid id) => _api.FetchAsync<OrderDto>($"orders/{id}");
                public Task Create(CreateOrderRequest r) => _api.SendAsync("orders", r);
            }
            public class RefundsClient : ApiClientBase
            {
                public Task Delete(Guid id) => Remove(CancellationToken.None, $"refunds/{id}");
            }
            """, cfg);
        Assert.Equal(("GET", "orders/{}", "configured-wrapper", Confidence.High),
            (m["OrdersClient.Get"].Verb, m["OrdersClient.Get"].NormalizedRoute, m["OrdersClient.Get"].Strategy, m["OrdersClient.Get"].Confidence));
        Assert.Equal("POST", m["OrdersClient.Create"].Verb);
        Assert.Equal(("DELETE", "refunds/{}"), (m["RefundsClient.Delete"].Verb, m["RefundsClient.Delete"].NormalizedRoute));
    }

    [Fact]
    public void NSwagGenerated_WithOverloadDelegation()
    {
        var m = Find("""
            public partial class OrdersClient
            {
                public virtual Task<OrderDto> GetOrderAsync(Guid id) => GetOrderAsync(id, CancellationToken.None);
                public virtual async Task<OrderDto> GetOrderAsync(Guid id, CancellationToken cancellationToken)
                {
                    var urlBuilder_ = new System.Text.StringBuilder();
                    urlBuilder_.Append("api/orders/");
                    urlBuilder_.Append(Uri.EscapeDataString(ConvertToString(id)));
                    using var request_ = new HttpRequestMessage();
                    request_.Method = new System.Net.Http.HttpMethod("GET");
                    return null!;
                }
            }
            """);
        var hit = m["OrdersClient.GetOrderAsync"];
        Assert.Equal(("GET", "api/orders/{}", "generated", Confidence.High), (hit.Verb, hit.NormalizedRoute, hit.Strategy, hit.Confidence));
    }

    [Fact]
    public void KiotaGenerated()
    {
        var m = Find("""
            public class OrdersItemRequestBuilder : BaseRequestBuilder
            {
                public OrdersItemRequestBuilder(IRequestAdapter requestAdapter) : base(requestAdapter, "{+baseurl}/api/orders/{id}{?includeLines}", null) { }
                public async Task<OrderDto?> GetAsync(CancellationToken ct = default) => null;
                public async Task DeleteAsync(CancellationToken ct = default) { }
            }
            """);
        Assert.Equal(("GET", "api/orders/{}"), (m["OrdersItemRequestBuilder.GetAsync"].Verb, m["OrdersItemRequestBuilder.GetAsync"].NormalizedRoute));
        Assert.Equal("DELETE", m["OrdersItemRequestBuilder.DeleteAsync"].Verb);
    }

    [Fact]
    public void ConfiguredPrefix_IsApplied_AndMediaTypesAreNotRoutes()
    {
        var cfg = new DepenkConfig();
        cfg.Routes.Prefixes["Orders.Client"] = "/api";
        var m = Find("""
            public class OrdersClient(HttpClient http)
            {
                public Task<HttpResponseMessage> Post(string body) => http.PostAsync("/orders", new StringContent(body, null, "application/json"));
            }
            """, cfg);
        Assert.Equal("/api/orders", m["OrdersClient.Post"].Route);
    }

    [Fact]
    public void AnyHit_IsFalse_ForPlainLibraries()
    {
        var result = new ClientMethodFinder(new DepenkConfig()).Find(Src.SetFor("r", "Lib", ("a.cs",
            "public class Maths { public int Add(int a, int b) => a + b; }")));
        Assert.False(result.AnyHit);
        Assert.Empty(result.Methods);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Depenk.Tests --filter "DeclaredTypesTests|ClientMethodFinderTests"`
Expected: build FAILS: `The type or namespace name 'Clients' does not exist in the namespace 'Depenk.Analysis'`.

- [ ] **Step 3: Implement DeclaredTypes**

`src/Depenk.Analysis/DeclaredTypes.cs`:

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Depenk.Analysis.SyntaxHelpers;

namespace Depenk.Analysis;

public static class DeclaredTypes
{
    public static string? Of(ExpressionSyntax receiver, SyntaxNode context)
    {
        var name = receiver switch
        {
            IdentifierNameSyntax id => id.Identifier.Text,
            MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax } m => m.Name.Identifier.Text,
            _ => null,
        };
        if (name is null) return null;

        foreach (var anc in context.Ancestors())
        {
            if (anc is BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax
                or AccessorDeclarationSyntax or GlobalStatementSyntax)
            {
                var local = anc.DescendantNodes().OfType<VariableDeclaratorSyntax>()
                    .FirstOrDefault(v => v.Identifier.Text == name && v.Parent is VariableDeclarationSyntax);
                if (local?.Parent is VariableDeclarationSyntax decl)
                    return decl.Type.IsVar ? FromInitializer(local.Initializer?.Value) : TypeName(decl.Type);

                IEnumerable<ParameterSyntax> parameters = anc switch
                {
                    BaseMethodDeclarationSyntax bm => bm.ParameterList.Parameters,
                    LocalFunctionStatementSyntax lf => lf.ParameterList.Parameters,
                    ParenthesizedLambdaExpressionSyntax pl => pl.ParameterList.Parameters,
                    _ => [],
                };
                if (parameters.FirstOrDefault(p => p.Identifier.Text == name) is { Type: { } pt }) return TypeName(pt);
            }
            if (anc is TypeDeclarationSyntax td)
            {
                if (td.ParameterList?.Parameters.FirstOrDefault(p => p.Identifier.Text == name) is { Type: { } ct })
                    return TypeName(ct);
                foreach (var f in td.Members.OfType<FieldDeclarationSyntax>())
                    if (f.Declaration.Variables.Any(v => v.Identifier.Text == name)) return TypeName(f.Declaration.Type);
                foreach (var p in td.Members.OfType<PropertyDeclarationSyntax>())
                    if (p.Identifier.Text == name) return TypeName(p.Type);
                return null;
            }
        }
        return null;
    }

    public static List<string> BaseTypes(TypeDeclarationSyntax td) =>
        td.BaseList?.Types.Select(t => TypeName(t.Type)).ToList() ?? [];

    private static string? FromInitializer(ExpressionSyntax? init) => init switch
    {
        ObjectCreationExpressionSyntax oc => TypeName(oc.Type),
        InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name: GenericNameSyntax g } }
            when g.Identifier.Text is "GetRequiredService" or "GetService" => TypeName(g.TypeArgumentList.Arguments[0]),
        AwaitExpressionSyntax a => FromInitializer(a.Expression),
        _ => null,
    };
}
```

- [ ] **Step 4: Implement the strategies**

`src/Depenk.Analysis/Clients/RouteHit.cs`:

```csharp
using Depenk.Core.Model;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Depenk.Analysis.Clients;

public sealed record RouteHit(string Verb, string Route, string Strategy, Confidence Confidence);

public interface IRouteStrategy
{
    RouteHit? Match(MethodDeclarationSyntax method, TypeDeclarationSyntax owner);
}

internal static class Verbs
{
    private static readonly (string Prefix, string Verb)[] Prefixes =
        [("Get", "GET"), ("Post", "POST"), ("Put", "PUT"), ("Delete", "DELETE"), ("Patch", "PATCH")];

    public static string? FromMethodName(string name) =>
        Prefixes.FirstOrDefault(p => name.StartsWith(p.Prefix, StringComparison.OrdinalIgnoreCase)).Verb;

    /// <summary>HttpMethod.Get / HttpMethod.Put / new HttpMethod("GET").</summary>
    public static string? FromHttpMethodExpression(ExpressionSyntax e) => e switch
    {
        MemberAccessExpressionSyntax m when m.Expression.ToString().EndsWith("HttpMethod", StringComparison.Ordinal) =>
            m.Name.Identifier.Text.ToUpperInvariant(),
        ObjectCreationExpressionSyntax oc when oc.Type.ToString().EndsWith("HttpMethod", StringComparison.Ordinal)
            && oc.ArgumentList?.Arguments.Count == 1 => SyntaxHelpers.StringValue(oc.ArgumentList.Arguments[0].Expression)?.ToUpperInvariant(),
        _ => null,
    };

    private static readonly string[] MediaPrefixes = ["application/", "text/", "multipart/", "image/"];

    public static bool IsRouteLike(string? s) =>
        !string.IsNullOrEmpty(s) && !s.Any(char.IsWhiteSpace) && (s.Contains('/') || s.Contains('{'))
        && !MediaPrefixes.Any(p => s.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>StringValue, plus resolution of a local variable declared in the same method.</summary>
    public static string? RouteValue(ExpressionSyntax e, MethodDeclarationSyntax method)
    {
        if (SyntaxHelpers.StringValue(e) is { } s) return s;
        if (e is not IdentifierNameSyntax id) return null;
        var local = method.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .FirstOrDefault(v => v.Identifier.Text == id.Identifier.Text);
        return local?.Initializer?.Value is { } init ? SyntaxHelpers.StringValue(init) : null;
    }
}
```

`src/Depenk.Analysis/Clients/RefitStrategy.cs`:

```csharp
using Depenk.Core.Model;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Depenk.Analysis.SyntaxHelpers;

namespace Depenk.Analysis.Clients;

public sealed class RefitStrategy : IRouteStrategy
{
    private static readonly HashSet<string> VerbAttrs = ["Get", "Post", "Put", "Delete", "Patch", "Head"];

    public RouteHit? Match(MethodDeclarationSyntax method, TypeDeclarationSyntax owner) =>
        Attrs(method).Where(a => VerbAttrs.Contains(AttrName(a)))
            .Select(a => FirstStringArg(a) is { } route ? new RouteHit(AttrName(a).ToUpperInvariant(), route, "refit", Confidence.High) : null)
            .FirstOrDefault(h => h is not null);
}
```

`src/Depenk.Analysis/Clients/GeneratedClientStrategy.cs`:

```csharp
using System.Text.RegularExpressions;
using Depenk.Core.Model;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Depenk.Analysis.SyntaxHelpers;

namespace Depenk.Analysis.Clients;

public sealed partial class GeneratedClientStrategy : IRouteStrategy
{
    public RouteHit? Match(MethodDeclarationSyntax method, TypeDeclarationSyntax owner) =>
        MatchNSwag(method) ?? MatchKiota(method, owner);

    private static RouteHit? MatchNSwag(MethodDeclarationSyntax method)
    {
        var appends = method.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => i.Expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "Append" } m
                        && m.Expression.ToString() == "urlBuilder_" && i.ArgumentList.Arguments.Count == 1)
            .Select(i => StringValue(i.ArgumentList.Arguments[0].Expression) ?? "{}")
            .ToList();
        if (appends.Count == 0) return null;

        var verb = method.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left.ToString().EndsWith(".Method", StringComparison.Ordinal))
            .Select(a => Verbs.FromHttpMethodExpression(a.Right)).FirstOrDefault(v => v is not null);
        return verb is null ? null : new RouteHit(verb, string.Concat(appends), "generated", Confidence.High);
    }

    private static RouteHit? MatchKiota(MethodDeclarationSyntax method, TypeDeclarationSyntax owner)
    {
        var verb = Verbs.FromMethodName(method.Identifier.Text);
        if (verb is null) return null;
        var template = owner.Members.OfType<ConstructorDeclarationSyntax>()
            .Select(c => c.Initializer?.ArgumentList.Arguments.Select(a => StringValue(a.Expression))
                .FirstOrDefault(s => s?.Contains("{+baseurl}") == true))
            .FirstOrDefault(s => s is not null);
        if (template is null) return null;
        var route = KiotaQuery().Replace(template.Replace("{+baseurl}", ""), "");
        return new RouteHit(verb, route, "generated", Confidence.High);
    }

    [GeneratedRegex(@"\{\?[^}]*\}")]
    private static partial Regex KiotaQuery();
}
```

`src/Depenk.Analysis/Clients/ConfiguredWrapperStrategy.cs`:

```csharp
using Depenk.Core.Model;
using Depenk.Scanning.Config;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Depenk.Analysis.Clients;

public sealed class ConfiguredWrapperStrategy(IReadOnlyList<HttpWrapperConfig> wrappers) : IRouteStrategy
{
    public RouteHit? Match(MethodDeclarationSyntax method, TypeDeclarationSyntax owner)
    {
        if (wrappers.Count == 0) return null;
        foreach (var inv in method.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var (receiverTypes, calledName) = inv.Expression switch
            {
                MemberAccessExpressionSyntax m => (DeclaredTypes.Of(m.Expression, inv) is { } t ? [t] : new List<string>(),
                    m.Name.Identifier.Text),
                IdentifierNameSyntax id => (DeclaredTypes.BaseTypes(owner), id.Identifier.Text),
                GenericNameSyntax g => (DeclaredTypes.BaseTypes(owner), g.Identifier.Text),
                _ => (new List<string>(), ""),
            };
            foreach (var w in wrappers)
            {
                if (!receiverTypes.Any(t => Glob.IsMatch(w.Type, StripGenerics(t)))) continue;
                var verb = w.Methods.FirstOrDefault(kv => Glob.IsMatch(kv.Key, calledName)).Value;
                var args = inv.ArgumentList.Arguments;
                if (verb is null || w.RouteArgument >= args.Count) continue;
                if (Verbs.RouteValue(args[w.RouteArgument].Expression, method) is { } route)
                    return new RouteHit(verb.ToUpperInvariant(), route, "configured-wrapper", Confidence.High);
            }
        }
        return null;
    }

    private static string StripGenerics(string t) => t.Split('<')[0];
}
```

`src/Depenk.Analysis/Clients/GenericHttpStrategy.cs`:

```csharp
using Depenk.Core.Model;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Depenk.Analysis.Clients;

public sealed class GenericHttpStrategy : IRouteStrategy
{
    public RouteHit? Match(MethodDeclarationSyntax method, TypeDeclarationSyntax owner)
    {
        foreach (var node in method.DescendantNodes())
        {
            if (node is ObjectCreationExpressionSyntax oc && oc.Type.ToString().EndsWith("HttpRequestMessage", StringComparison.Ordinal)
                && oc.ArgumentList?.Arguments.Count >= 2
                && Verbs.FromHttpMethodExpression(oc.ArgumentList.Arguments[0].Expression) is { } v
                && Verbs.RouteValue(oc.ArgumentList.Arguments[1].Expression, method) is { } r && Verbs.IsRouteLike(r))
                return new RouteHit(v, r, "generic-http", Confidence.Medium);

            if (node is InvocationExpressionSyntax inv)
            {
                var name = inv.Expression switch
                {
                    MemberAccessExpressionSyntax m => m.Name.Identifier.Text,
                    IdentifierNameSyntax id => id.Identifier.Text,
                    GenericNameSyntax g => g.Identifier.Text,
                    _ => null,
                };
                if (name is null || Verbs.FromMethodName(name) is not { } verb) continue;
                if (name == method.Identifier.Text) continue; // self/overload call, handled by delegation pass
                var route = inv.ArgumentList.Arguments.Select(a => Verbs.RouteValue(a.Expression, method))
                    .FirstOrDefault(Verbs.IsRouteLike);
                if (route is not null) return new RouteHit(verb, route, "generic-http", Confidence.Medium);
            }
        }
        return null;
    }
}
```

`src/Depenk.Analysis/Clients/ClientMethodFinder.cs`:

```csharp
using Depenk.Analysis.Routes;
using Depenk.Core;
using Depenk.Core.Model;
using Depenk.Scanning.Config;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Depenk.Analysis.SyntaxHelpers;

namespace Depenk.Analysis.Clients;

public sealed record ClientScanResult(List<ClientMethodNode> Methods, bool AnyHit);

public sealed class ClientMethodFinder(DepenkConfig config)
{
    private readonly IRouteStrategy[] _strategies =
    [
        new RefitStrategy(), new GeneratedClientStrategy(),
        new ConfiguredWrapperStrategy(config.HttpWrappers), new GenericHttpStrategy(),
    ];

    private sealed record Candidate(SourceDoc Doc, TypeDeclarationSyntax Owner, MethodDeclarationSyntax Method)
    {
        public RouteHit? Hit { get; set; }
    }

    public ClientScanResult Find(SourceSet src)
    {
        var candidates = src.All<TypeDeclarationSyntax>()
            .Where(t => t.Node is ClassDeclarationSyntax or InterfaceDeclarationSyntax)
            .SelectMany(t => t.Node.Members.OfType<MethodDeclarationSyntax>()
                .Where(m => t.Node is InterfaceDeclarationSyntax
                            || (m.Modifiers.Any(SyntaxKind.PublicKeyword) && !m.Modifiers.Any(SyntaxKind.StaticKeyword)))
                .Select(m => new Candidate(t.Doc, t.Node, m)))
            .ToList();

        foreach (var c in candidates)
            c.Hit = _strategies.Select(s => s.Match(c.Method, c.Owner)).FirstOrDefault(h => h is not null);

        // pass 2a: overload/self delegation inside the same class
        foreach (var c in candidates.Where(c => c.Hit is null && c.Owner is ClassDeclarationSyntax))
        {
            var calls = c.Method.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Select(i => i.Expression switch { IdentifierNameSyntax id => id.Identifier.Text, _ => null })
                .Where(n => n == c.Method.Identifier.Text);
            if (calls.Any())
                c.Hit = candidates.FirstOrDefault(o => o != c && o.Owner == c.Owner && o.Hit is not null
                                                        && o.Method.Identifier.Text == c.Method.Identifier.Text)?.Hit;
        }

        // pass 2b: interface methods inherit from implementing classes
        foreach (var c in candidates.Where(c => c.Hit is null && c.Owner is InterfaceDeclarationSyntax))
        {
            var iface = c.Owner.Identifier.Text;
            c.Hit = candidates.FirstOrDefault(o => o.Hit is not null && o.Owner is ClassDeclarationSyntax
                                                   && DeclaredTypes.BaseTypes(o.Owner).Any(b => b.Split('<')[0] == iface)
                                                   && o.Method.Identifier.Text == c.Method.Identifier.Text)?.Hit;
        }

        var ownersWithHits = candidates.Where(c => c.Hit is not null).Select(c => c.Owner).ToHashSet();
        var prefix = config.Routes.Prefixes.GetValueOrDefault(src.ProjectName);
        var nodes = new Dictionary<string, ClientMethodNode>();
        foreach (var c in candidates)
        {
            if (c.Hit is null && !(ownersWithHits.Contains(c.Owner) && IsAsync(c.Method))) continue;
            var typeName = c.Owner.Identifier.Text;
            var id = Ids.ClientMethod(src.ProjectName, typeName, c.Method.Identifier.Text);
            if (nodes.TryGetValue(id, out var existing) && (existing.Verb is not null || c.Hit is null)) continue;

            var route = c.Hit is null ? null
                : prefix is null ? RouteNormalizer.Combine(null, c.Hit.Route)
                : RouteNormalizer.Combine(prefix, c.Hit.Route.TrimStart('/'));
            nodes[id] = new ClientMethodNode(id, src.Repo, src.ProjectId, typeName, c.Method.Identifier.Text,
                $"{TypeName(c.Method.ReturnType)} {c.Method.Identifier.Text}({c.Method.ParameterList.Parameters})",
                c.Hit?.Verb, route, route is null ? null : RouteNormalizer.Normalize(route),
                c.Hit?.Strategy ?? "none", c.Hit?.Confidence ?? Confidence.Low,
                new SourceLocation(c.Doc.RelativePath, Line(c.Method.Identifier)));
        }
        return new ClientScanResult([.. nodes.Values], candidates.Any(c => c.Hit is not null));
    }

    private static bool IsAsync(MethodDeclarationSyntax m)
    {
        var rt = TypeName(m.ReturnType);
        return rt is "Task" or "ValueTask" || rt.StartsWith("Task<", StringComparison.Ordinal) || rt.StartsWith("ValueTask<", StringComparison.Ordinal);
    }
}
```

- [ ] **Step 5: Run tests**

Run: `dotnet test tests/Depenk.Tests --filter "DeclaredTypesTests|ClientMethodFinderTests"`
Expected: PASS (8 DeclaredTypes cases + 7 finder tests).

Things to check if a test fails:
- **`ListAsync`:** `"api/orders?page=" + page` evaluates to `api/orders?page={}`, which is route-like because it contains `/`. The normalizer strips the query.
- **`SetStatusAsync`:** the `StringContent(..., "application/json")` argument must be rejected by `IsRouteLike`, and `HttpRequestMessage` must be matched first.
- **`GetFromJsonAsync<OrderDto>(...)`:** the expression is a `MemberAccessExpressionSyntax` whose `Name` is a `GenericNameSyntax`. `m.Name.Identifier.Text` handles this, because `SimpleNameSyntax.Identifier` covers both simple and generic names.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(analysis): client method finder with refit, generated, wrapper and generic strategies"
```

### Task 8: Client-method → endpoint linker

**Files:**
- Create: `src/Depenk.Analysis/Linking/ClientEndpointLinker.cs`
- Test: `tests/Depenk.Tests/Analysis/ClientEndpointLinkerTests.cs`

**Interfaces:**
- Consumes: `DepGraph`, `EndpointNode`, `ClientMethodNode`, `Edge`, `DiagnosticKinds` (Task 1)
- Produces: `ClientEndpointLinker.Link(DepGraph graph)`, which appends `targets` edges and diagnostics to `graph`

**Rules (spec §3.1 step 7):**
- Candidates are endpoints in the **same repo** as the client method, with an equal verb (case-insensitive) and an equal `NormalizedRoute`.
- Exactly one candidate: a `targets` edge with the client method's confidence and `Strategy = cm.Strategy`.
- More than one: a `targets` edge to each, all `Low`, plus an `ambiguousRoute` diagnostic.
- None: try a **suffix match** in the same repo, where one normalized route ends with `"/" + other` (this covers base-address prefixes such as `api/`). A single suffix match gives an edge with `Low` confidence and `Strategy = cm.Strategy + "+suffix"`. Otherwise an `unresolvedClientMethod` diagnostic (severity `info`).
- A client method with `Verb == null` (no HTTP call detected) gets an `unresolvedClientMethod` diagnostic with the message `"{id}: no HTTP call detected"`.

- [ ] **Step 1: Write failing tests**

`tests/Depenk.Tests/Analysis/ClientEndpointLinkerTests.cs`:

```csharp
using Depenk.Analysis.Linking;
using Depenk.Core.Model;

namespace Depenk.Tests.Analysis;

public class ClientEndpointLinkerTests
{
    private static readonly SourceLocation Loc = new("x.cs", 1);

    private static EndpointNode Ep(string repo, string verb, string route, string norm) =>
        new($"ep:{repo}:{verb}:{route}", repo, $"proj:{repo}/Api", verb, route, norm, "C.M", [], [], Loc);

    private static ClientMethodNode Cm(string repo, string name, string? verb, string? norm,
        string strategy = "refit", Confidence c = Confidence.High) =>
        new($"cm:Client:I.{name}", repo, $"proj:{repo}/Client", "I", name, "", verb, norm is null ? null : "/" + norm,
            norm, strategy, c, Loc);

    [Fact]
    public void ExactMatch_SameRepoOnly()
    {
        var g = new DepGraph();
        g.Endpoints.Add(Ep("orders", "GET", "/api/orders/{id}", "api/orders/{}"));
        g.Endpoints.Add(Ep("billing", "GET", "/api/orders/{id}", "api/orders/{}"));
        g.ClientMethods.Add(Cm("orders", "Get", "GET", "api/orders/{}"));

        ClientEndpointLinker.Link(g);

        var e = g.EdgesOf(EdgeKind.Targets).Single();
        Assert.Equal(("cm:Client:I.Get", "ep:orders:GET:/api/orders/{id}", Confidence.High, "refit"),
            (e.From, e.To, e.Confidence, e.Strategy));
        Assert.Empty(g.Diagnostics);
    }

    [Fact]
    public void VerbMustMatch_CaseInsensitive()
    {
        var g = new DepGraph();
        g.Endpoints.Add(Ep("orders", "POST", "/api/orders", "api/orders"));
        g.ClientMethods.Add(Cm("orders", "Create", "post", "api/orders"));
        g.ClientMethods.Add(Cm("orders", "Wrong", "PUT", "api/orders"));

        ClientEndpointLinker.Link(g);

        Assert.Equal("cm:Client:I.Create", g.EdgesOf(EdgeKind.Targets).Single().From);
        Assert.Contains(g.Diagnostics, d => d.Kind == DiagnosticKinds.UnresolvedClientMethod && d.NodeIds.Contains("cm:Client:I.Wrong"));
    }

    [Fact]
    public void Ambiguous_KeepsAllAsLow_WithDiagnostic()
    {
        var g = new DepGraph();
        g.Endpoints.Add(Ep("orders", "GET", "/api/orders/{id}", "api/orders/{}"));
        g.Endpoints.Add(Ep("orders", "GET", "/api/orders/{slug}", "api/orders/{}"));
        g.ClientMethods.Add(Cm("orders", "Get", "GET", "api/orders/{}"));

        ClientEndpointLinker.Link(g);

        Assert.Equal(2, g.EdgesOf(EdgeKind.Targets).Count());
        Assert.All(g.EdgesOf(EdgeKind.Targets), e => Assert.Equal(Confidence.Low, e.Confidence));
        Assert.Contains(g.Diagnostics, d => d.Kind == DiagnosticKinds.AmbiguousRoute);
    }

    [Fact]
    public void SuffixMatch_IsLowConfidence()
    {
        var g = new DepGraph();
        g.Endpoints.Add(Ep("orders", "GET", "/api/orders/{id}", "api/orders/{}"));
        g.ClientMethods.Add(Cm("orders", "Get", "GET", "orders/{}", "generic-http", Confidence.Medium));

        ClientEndpointLinker.Link(g);

        var e = g.EdgesOf(EdgeKind.Targets).Single();
        Assert.Equal((Confidence.Low, "generic-http+suffix"), (e.Confidence, e.Strategy));
    }

    [Fact]
    public void NoHttpCallDetected_Diagnostic()
    {
        var g = new DepGraph();
        g.ClientMethods.Add(Cm("orders", "Stats", null, null, "none", Confidence.Low));
        ClientEndpointLinker.Link(g);
        Assert.Equal("cm:Client:I.Stats: no HTTP call detected", g.Diagnostics.Single().Message);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Depenk.Tests --filter ClientEndpointLinkerTests`
Expected: build FAILS: `The type or namespace name 'Linking' does not exist`.

- [ ] **Step 3: Implement**

`src/Depenk.Analysis/Linking/ClientEndpointLinker.cs`:

```csharp
using Depenk.Core.Model;

namespace Depenk.Analysis.Linking;

public static class ClientEndpointLinker
{
    public static void Link(DepGraph graph)
    {
        var byRepo = graph.Endpoints.ToLookup(e => e.Repo);
        foreach (var cm in graph.ClientMethods)
        {
            if (cm.Verb is null || cm.NormalizedRoute is null)
            {
                graph.Diagnostics.Add(new Diagnostic(DiagnosticKinds.UnresolvedClientMethod, "info", [cm.Id],
                    $"{cm.Id}: no HTTP call detected"));
                continue;
            }
            var sameVerb = byRepo[cm.Repo].Where(e => e.Verb.Equals(cm.Verb, StringComparison.OrdinalIgnoreCase)).ToList();
            var exact = sameVerb.Where(e => e.NormalizedRoute == cm.NormalizedRoute).ToList();

            if (exact.Count == 1)
            {
                graph.Edges.Add(new Edge(EdgeKind.Targets, cm.Id, exact[0].Id, cm.Confidence) { Strategy = cm.Strategy });
            }
            else if (exact.Count > 1)
            {
                foreach (var ep in exact)
                    graph.Edges.Add(new Edge(EdgeKind.Targets, cm.Id, ep.Id, Confidence.Low) { Strategy = cm.Strategy });
                graph.Diagnostics.Add(new Diagnostic(DiagnosticKinds.AmbiguousRoute, "warning", [cm.Id, .. exact.Select(e => e.Id)],
                    $"{cm.Id} ({cm.Verb} {cm.Route}) matches {exact.Count} endpoints"));
            }
            else
            {
                var suffix = sameVerb.Where(e => IsSuffix(e.NormalizedRoute, cm.NormalizedRoute)).ToList();
                if (suffix.Count == 1)
                    graph.Edges.Add(new Edge(EdgeKind.Targets, cm.Id, suffix[0].Id, Confidence.Low) { Strategy = cm.Strategy + "+suffix" });
                else
                    graph.Diagnostics.Add(new Diagnostic(DiagnosticKinds.UnresolvedClientMethod, "info", [cm.Id],
                        $"{cm.Id}: no endpoint in repo '{cm.Repo}' matches {cm.Verb} {cm.Route}"));
            }
        }
    }

    private static bool IsSuffix(string a, string b) =>
        a.Length > 0 && b.Length > 0 && (a.EndsWith("/" + b, StringComparison.Ordinal) || b.EndsWith("/" + a, StringComparison.Ordinal));
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet test tests/Depenk.Tests --filter ClientEndpointLinkerTests`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(analysis): link client methods to endpoints with ambiguity and suffix handling"
```

### Task 9: Type unwrapper + model extractor

**Files:**
- Create: `src/Depenk.Analysis/Models/TypeUnwrapper.cs`, `src/Depenk.Analysis/Models/TypeIndex.cs`, `src/Depenk.Analysis/Models/ModelExtractor.cs`
- Test: `tests/Depenk.Tests/Analysis/TypeUnwrapperTests.cs`, `tests/Depenk.Tests/Analysis/ModelExtractorTests.cs`

**Interfaces:**
- Consumes: `SourceSet`, `SyntaxHelpers` (Task 6); graph model (Task 1)
- Produces:
  - `sealed record TypeRef(string Name, bool Collection)`, where `Name` is the simple type name without its generic arity (e.g. `OrderDto`, `PagedResult`)
  - `TypeUnwrapper.Unwrap(string typeText) : List<TypeRef>`: every *model candidate* in the type text; primitives and framework types are excluded
  - `TypeUnwrapper.IsSimple(string simpleName) : bool`
  - `sealed record TypeDecl(SourceSet Source, SourceDoc Doc, BaseTypeDeclarationSyntax Node, string FullName)`
  - `TypeIndex(IEnumerable<SourceSet> sources)` with `TypeDecl? Resolve(string simpleName, string fromProjectId, IReadOnlyList<string> referencedProjectIds)`
  - `ModelExtractor(IReadOnlyList<SourceSet> sources, Func<string, IReadOnlyList<string>> referencedProjectIds, int maxDepth = 6)` with `void Extract(DepGraph graph, IReadOnlySet<string> contractProjectIds)`

**Rules (spec §3.1 step 8):**
- **Unwrap** (parsed with `SyntaxFactory.ParseTypeName`, and recursive):
  - these wrappers are passed through: `Task`, `ValueTask`, `ActionResult`, `Ok`, `Created`, `CreatedAtRoute`, `Accepted`, `Results` (every argument), `Nullable`, `IAsyncEnumerable`
  - these set `Collection = true`: `IEnumerable`, `List`, `IList`, `ICollection`, `IReadOnlyList`, `IReadOnlyCollection`, `HashSet`, `ISet`, arrays, and `Dictionary`/`IDictionary`/`IReadOnlyDictionary` (value argument only)
  - `T?` becomes `T`
  - a qualified name uses its rightmost part
  - any **other** generic type (e.g. `PagedResult<OrderDto>`) yields itself (`PagedResult`) **plus** its unwrapped arguments
  - simple names are dropped: C# keywords and `Guid`, `DateTime`, `DateTimeOffset`, `DateOnly`, `TimeOnly`, `TimeSpan`, `Uri`, `Stream`, `IFormFile`, `JsonElement`, `JsonDocument`, `CancellationToken`, and the result markers `IActionResult`, `ActionResult`, `IResult`, `NotFound`, `NoContent`, `BadRequest`, `Ok`, `Unauthorized`, `Forbid`, `Conflict`, `UnprocessableEntity`, `ValidationProblem`, `ProblemDetails`, `HttpResponseMessage`
- **Resolve** a simple name, in this order:
  1. the same project
  2. referenced project IDs, in the order given
  3. any project in the same repo
  4. any project in the workspace

  Within each level, the first match in path order wins. Nothing found means an **opaque** node `model:?:{Name}` with `Kind = Opaque`, `Repo = ""` and `ProjectId = null`.
- **Model nodes:**
  - classes and structs (records use `Record`) get public instance properties that have a getter, plus positional record parameters, plus the properties of the first base type that resolves (one level)
  - enums get `EnumValues`
  - field `TypeName` = the property's type text; `Nullable` = the text ends with `?`; `Collection` = any unwrapped reference is a collection
- **Edges:**
  - for each endpoint parameter, whether it's `body`, `query`, `form` or `route`: `accepts` (Endpoint → Model) with `Source = parameter.Source`, `Confidence.High`, for each model candidate in its type
  - for each response: `returns` with `StatusCode`, `Confidence.High`
  - for each model field that has model candidates: `fieldOf` (Parent → Child) with `FieldName`, `Confidence.High`
  - recursion goes up to `maxDepth` levels from an endpoint and is guarded by a visited set, so cyclic models terminate
  - edges are deduplicated on (kind, from, to, source/status/field)
- **Contract surface:** every public class, record, struct or enum declared in a project listed in `contractProjectIds` (Client projects) also gets a model node, even when no endpoint references it. This lets Task 11 flag `unusedModel`.

- [ ] **Step 1: Write failing tests**

`tests/Depenk.Tests/Analysis/TypeUnwrapperTests.cs`:

```csharp
using Depenk.Analysis.Models;

namespace Depenk.Tests.Analysis;

public class TypeUnwrapperTests
{
    [Theory]
    [InlineData("Task<ActionResult<List<OrderDto>>>", "OrderDto*")]
    [InlineData("OrderDto?", "OrderDto")]
    [InlineData("Acme.Orders.OrderDto[]", "OrderDto*")]
    [InlineData("Results<Ok<OrderDto>, NotFound>", "OrderDto")]
    [InlineData("PagedResult<OrderDto>", "PagedResult,OrderDto")]
    [InlineData("Dictionary<string, OrderLineDto>", "OrderLineDto*")]
    [InlineData("Task<IActionResult>", "")]
    [InlineData("Guid", "")]
    [InlineData("int?", "")]
    [InlineData("IAsyncEnumerable<OrderDto>", "OrderDto")]
    public void Unwraps(string input, string expected)
    {
        var actual = string.Join(",", TypeUnwrapper.Unwrap(input).Select(r => r.Name + (r.Collection ? "*" : "")));
        Assert.Equal(expected, actual);
    }
}
```

`tests/Depenk.Tests/Analysis/ModelExtractorTests.cs`:

```csharp
using Depenk.Analysis;
using Depenk.Analysis.Endpoints;
using Depenk.Analysis.Models;
using Depenk.Core.Model;
using Depenk.Tests.TestUtil;

namespace Depenk.Tests.Analysis;

public class ModelExtractorTests
{
    private const string Api = """
        namespace Acme.Orders.Api;
        [Route("api/orders")]
        public class OrdersController : ControllerBase
        {
            [HttpGet("{id}")] public Task<ActionResult<OrderDto>> Get(Guid id) => null!;
            [HttpPost] public Task<ActionResult<OrderDto>> Create(CreateOrderRequest request) => null!;
            [HttpGet("tree")] public Node Tree() => null!;
            [HttpGet("mystery")] public Mystery Unknown() => null!;
        }
        public class Node { public Node? Parent { get; set; } public List<Node> Children { get; set; } = []; }
        """;

    private const string Client = """
        namespace Acme.Orders.Client;
        public record CreateOrderRequest(Guid CustomerId, List<OrderLineDto> Lines);
        public class OrderDto : EntityBase
        {
            public OrderStatus Status { get; init; }
            public CustomerRef? Customer { get; init; }
            public List<OrderLineDto> Lines { get; init; } = [];
            private int Hidden { get; set; }
            public static OrderDto Empty { get; } = new();
        }
        public class EntityBase { public Guid Id { get; init; } }
        public class OrderLineDto { public string Sku { get; set; } = ""; public int Qty { get; set; } }
        public enum OrderStatus { Pending, Paid, Shipped }
        public class UnusedDto { public int X { get; set; } }
        """;

    private const string Customers = """
        namespace Acme.Customers.Client;
        public class CustomerRef { public Guid Id { get; init; } public string Name { get; init; } = ""; }
        """;

    private static DepGraph Run()
    {
        var api = Src.SetFor("orders", "Orders.Api", ("orders/Api/OrdersController.cs", Api));
        var client = Src.SetFor("orders", "Orders.Client", ("orders/Client/Models.cs", Client));
        var customers = Src.SetFor("customers", "Customers.Client", ("customers/Client/CustomerRef.cs", Customers));
        var g = new DepGraph();
        g.Endpoints.AddRange(new ControllerEndpointFinder().Find(api));
        var refs = new Dictionary<string, IReadOnlyList<string>> { ["proj:orders/Orders.Api"] = ["proj:orders/Orders.Client"] };
        new ModelExtractor([api, client, customers], id => refs.GetValueOrDefault(id, []))
            .Extract(g, new HashSet<string> { "proj:orders/Orders.Client", "proj:customers/Customers.Client" });
        return g;
    }

    private static ModelNode M(DepGraph g, string fullName) => g.Models.Single(m => m.FullName == fullName);

    [Fact]
    public void BuildsModelTree_AcrossProjectsAndRepos()
    {
        var g = Run();
        var order = M(g, "Acme.Orders.Client.OrderDto");
        Assert.Equal("model:Orders.Client:Acme.Orders.Client.OrderDto", order.Id);
        Assert.Equal(ModelKind.Class, order.Kind);
        Assert.Equal(["Status", "Customer", "Lines", "Id"], order.Fields.Select(f => f.Name));
        Assert.Equal(new ModelField("Customer", "CustomerRef?", true, false), order.Fields[1]);
        Assert.Equal(new ModelField("Lines", "List<OrderLineDto>", false, true), order.Fields[2]);

        var customer = M(g, "Acme.Customers.Client.CustomerRef");
        Assert.Equal("customers", customer.Repo);
        Assert.Contains(g.EdgesOf(EdgeKind.FieldOf), e => e.From == order.Id && e.To == customer.Id && e.FieldName == "Customer");

        Assert.Equal(["Pending", "Paid", "Shipped"], M(g, "Acme.Orders.Client.OrderStatus").EnumValues);
        var req = M(g, "Acme.Orders.Client.CreateOrderRequest");
        Assert.Equal(ModelKind.Record, req.Kind);
        Assert.Equal(["CustomerId", "Lines"], req.Fields.Select(f => f.Name));
    }

    [Fact]
    public void LinksEndpointsToModels()
    {
        var g = Run();
        var ret = g.EdgesOf(EdgeKind.Returns).Single(e => e.From == "ep:orders:GET:/api/orders/{id}");
        Assert.Equal(("model:Orders.Client:Acme.Orders.Client.OrderDto", 200), (ret.To, ret.StatusCode));
        var acc = g.EdgesOf(EdgeKind.Accepts).Single(e => e.From == "ep:orders:POST:/api/orders");
        Assert.Equal(("model:Orders.Client:Acme.Orders.Client.CreateOrderRequest", "body"), (acc.To, acc.Source));
        Assert.DoesNotContain(g.EdgesOf(EdgeKind.Accepts), e => e.From == "ep:orders:GET:/api/orders/{id}"); // Guid is simple
    }

    [Fact]
    public void CyclicModels_Terminate_AndUnknownTypesAreOpaque()
    {
        var g = Run();
        var node = M(g, "Acme.Orders.Api.Node");
        Assert.Contains(g.EdgesOf(EdgeKind.FieldOf), e => e.From == node.Id && e.To == node.Id && e.FieldName == "Parent");
        var mystery = g.Models.Single(m => m.Id == "model:?:Mystery");
        Assert.Equal((ModelKind.Opaque, ""), (mystery.Kind, mystery.Repo));
    }

    [Fact]
    public void ContractSurface_IncludesUnreferencedClientModels_WithoutDuplicates()
    {
        var g = Run();
        Assert.Single(g.Models, m => m.FullName == "Acme.Orders.Client.UnusedDto");
        Assert.Equal(g.Models.Count, g.Models.Select(m => m.Id).Distinct().Count());
        Assert.Equal(g.Edges.Count, g.Edges.Distinct().Count());
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Depenk.Tests --filter "TypeUnwrapperTests|ModelExtractorTests"`
Expected: build FAILS: `The type or namespace name 'Models' does not exist in the namespace 'Depenk.Analysis'`.

- [ ] **Step 3: Implement TypeUnwrapper**

`src/Depenk.Analysis/Models/TypeUnwrapper.cs`:

```csharp
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Depenk.Analysis.Models;

public sealed record TypeRef(string Name, bool Collection);

public static class TypeUnwrapper
{
    private static readonly HashSet<string> PassThrough =
        ["Task", "ValueTask", "ActionResult", "Ok", "Created", "CreatedAtRoute", "Accepted", "Results", "Nullable", "IAsyncEnumerable"];
    private static readonly HashSet<string> Collections =
        ["IEnumerable", "List", "IList", "ICollection", "IReadOnlyList", "IReadOnlyCollection", "HashSet", "ISet"];
    private static readonly HashSet<string> Dictionaries = ["Dictionary", "IDictionary", "IReadOnlyDictionary"];
    private static readonly HashSet<string> SimpleNames =
    [
        "string", "int", "long", "short", "byte", "sbyte", "uint", "ulong", "ushort", "bool", "decimal", "double", "float",
        "char", "object", "dynamic", "void", "String", "Int32", "Int64", "Boolean", "Decimal", "Double", "Object",
        "Guid", "DateTime", "DateTimeOffset", "DateOnly", "TimeOnly", "TimeSpan", "Uri", "Stream", "IFormFile",
        "JsonElement", "JsonDocument", "CancellationToken",
        "IActionResult", "ActionResult", "IResult", "NotFound", "NoContent", "BadRequest", "Ok", "Unauthorized",
        "Forbid", "Conflict", "UnprocessableEntity", "ValidationProblem", "ProblemDetails", "HttpResponseMessage", "Task", "ValueTask",
    ];

    public static bool IsSimple(string simpleName) => SimpleNames.Contains(simpleName);

    public static List<TypeRef> Unwrap(string typeText)
    {
        var result = new List<TypeRef>();
        Walk(SyntaxFactory.ParseTypeName(typeText.Replace("global::", "")), false, result);
        return result.Distinct().ToList();
    }

    private static void Walk(TypeSyntax t, bool collection, List<TypeRef> acc)
    {
        switch (t)
        {
            case NullableTypeSyntax n: Walk(n.ElementType, collection, acc); break;
            case ArrayTypeSyntax a: Walk(a.ElementType, true, acc); break;
            case QualifiedNameSyntax q: Walk(q.Right, collection, acc); break;
            case AliasQualifiedNameSyntax al: Walk(al.Name, collection, acc); break;
            case GenericNameSyntax g:
                var name = g.Identifier.Text;
                var args = g.TypeArgumentList.Arguments;
                if (PassThrough.Contains(name)) foreach (var a in args) Walk(a, collection, acc);
                else if (Collections.Contains(name)) foreach (var a in args) Walk(a, true, acc);
                else if (Dictionaries.Contains(name)) { if (args.Count == 2) Walk(args[1], true, acc); }
                else
                {
                    acc.Add(new TypeRef(name, collection));
                    foreach (var a in args) Walk(a, collection, acc);
                }
                break;
            case IdentifierNameSyntax id when !IsSimple(id.Identifier.Text):
                acc.Add(new TypeRef(id.Identifier.Text, collection));
                break;
        }
    }
}
```

`PredefinedTypeSyntax` (`int`, `string`…) matches no case, so it's dropped automatically.

- [ ] **Step 4: Implement TypeIndex**

`src/Depenk.Analysis/Models/TypeIndex.cs`:

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Depenk.Analysis.Models;

public sealed record TypeDecl(SourceSet Source, SourceDoc Doc, BaseTypeDeclarationSyntax Node, string FullName);

public sealed class TypeIndex
{
    private readonly ILookup<string, TypeDecl> _byName;

    public TypeIndex(IEnumerable<SourceSet> sources) =>
        _byName = sources
            .SelectMany(s => s.All<BaseTypeDeclarationSyntax>()
                .Where(t => t.Node is ClassDeclarationSyntax or RecordDeclarationSyntax or StructDeclarationSyntax or EnumDeclarationSyntax)
                .Select(t => new TypeDecl(s, t.Doc, t.Node, FullNameOf(t.Node))))
            .ToLookup(d => d.Node.Identifier.Text, StringComparer.Ordinal);

    public IEnumerable<TypeDecl> All => _byName.SelectMany(g => g);

    public TypeDecl? Resolve(string simpleName, string fromProjectId, IReadOnlyList<string> referencedProjectIds)
    {
        var candidates = _byName[simpleName].ToList();
        if (candidates.Count == 0) return null;
        var fromRepo = candidates.FirstOrDefault(c => c.Source.ProjectId == fromProjectId)?.Source.Repo
                       ?? RepoOf(fromProjectId);
        return candidates.FirstOrDefault(c => c.Source.ProjectId == fromProjectId)
               ?? referencedProjectIds.Select(r => candidates.FirstOrDefault(c => c.Source.ProjectId == r)).FirstOrDefault(c => c is not null)
               ?? candidates.FirstOrDefault(c => c.Source.Repo == fromRepo)
               ?? candidates[0];
    }

    /// <summary>"proj:{repo}/{name}" → repo.</summary>
    private static string RepoOf(string projectId) => projectId["proj:".Length..].Split('/')[0];

    public static string FullNameOf(BaseTypeDeclarationSyntax t)
    {
        var parts = new List<string> { t.Identifier.Text };
        foreach (var anc in t.Ancestors())
        {
            switch (anc)
            {
                case BaseTypeDeclarationSyntax outer: parts.Add(outer.Identifier.Text); break;
                case BaseNamespaceDeclarationSyntax ns: parts.Add(ns.Name.ToString()); break;
            }
        }
        parts.Reverse();
        return string.Join('.', parts);
    }
}
```

- [ ] **Step 5: Implement ModelExtractor**

`src/Depenk.Analysis/Models/ModelExtractor.cs`:

```csharp
using Depenk.Core;
using Depenk.Core.Model;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Depenk.Analysis.SyntaxHelpers;

namespace Depenk.Analysis.Models;

public sealed class ModelExtractor(
    IReadOnlyList<SourceSet> sources, Func<string, IReadOnlyList<string>> referencedProjectIds, int maxDepth = 6)
{
    private readonly TypeIndex _index = new(sources);

    public void Extract(DepGraph graph, IReadOnlySet<string> contractProjectIds)
    {
        var models = graph.Models.ToDictionary(m => m.Id);
        var edges = new HashSet<Edge>(graph.Edges);
        void AddEdge(Edge e) { if (edges.Add(e)) graph.Edges.Add(e); }

        string Ensure(TypeRef tr, string fromProjectId, int depth)
        {
            var decl = _index.Resolve(tr.Name, fromProjectId, referencedProjectIds(fromProjectId));
            if (decl is null)
            {
                var opaqueId = Ids.Model("?", tr.Name);
                if (!models.ContainsKey(opaqueId))
                {
                    models[opaqueId] = new ModelNode(opaqueId, "", null, tr.Name, ModelKind.Opaque, [], null, null);
                    graph.Models.Add(models[opaqueId]);
                }
                return opaqueId;
            }
            return EnsureDecl(decl, depth);
        }

        string EnsureDecl(TypeDecl decl, int depth)
        {
            var id = Ids.Model(decl.Source.ProjectName, decl.FullName);
            if (models.ContainsKey(id)) return id;

            var (kind, fields, enumValues) = Describe(decl);
            var node = new ModelNode(id, decl.Source.Repo, decl.Source.ProjectId, decl.FullName, kind, fields, enumValues,
                new SourceLocation(decl.Doc.RelativePath, Line(decl.Node.Identifier)));
            models[id] = node;          // register before recursing: cycles terminate
            graph.Models.Add(node);

            if (depth >= maxDepth) return id;
            foreach (var f in fields)
            foreach (var child in TypeUnwrapper.Unwrap(f.TypeName))
                AddEdge(new Edge(EdgeKind.FieldOf, id, Ensure(child, decl.Source.ProjectId, depth + 1), Confidence.High)
                    { FieldName = f.Name });
            return id;
        }

        foreach (var ep in graph.Endpoints.ToList())
        {
            foreach (var p in ep.Parameters)
            foreach (var tr in TypeUnwrapper.Unwrap(p.TypeName))
                AddEdge(new Edge(EdgeKind.Accepts, ep.Id, Ensure(tr, ep.ProjectId, 1), Confidence.High) { Source = p.Source });
            foreach (var r in ep.Responses)
            foreach (var tr in TypeUnwrapper.Unwrap(r.TypeName))
                AddEdge(new Edge(EdgeKind.Returns, ep.Id, Ensure(tr, ep.ProjectId, 1), Confidence.High) { StatusCode = r.StatusCode });
        }

        foreach (var decl in _index.All.Where(d => contractProjectIds.Contains(d.Source.ProjectId)
                                                  && d.Node.Modifiers.Any(SyntaxKind.PublicKeyword)))
            EnsureDecl(decl, 1);
    }

    private (ModelKind, List<ModelField>, List<string>?) Describe(TypeDecl decl)
    {
        if (decl.Node is EnumDeclarationSyntax en)
            return (ModelKind.Enum, [], en.Members.Select(m => m.Identifier.Text).ToList());

        var type = (TypeDeclarationSyntax)decl.Node;
        var kind = type switch
        {
            RecordDeclarationSyntax => ModelKind.Record,
            StructDeclarationSyntax => ModelKind.Struct,
            _ => ModelKind.Class,
        };
        var fields = new List<ModelField>();
        if (type.ParameterList is { } positional)
            fields.AddRange(positional.Parameters.Where(p => p.Type is not null).Select(p => Field(p.Identifier.Text, p.Type!)));
        fields.AddRange(PublicProperties(type));

        var baseDecl = DeclaredTypes.BaseTypes(type)
            .Select(b => _index.Resolve(b.Split('<')[0], decl.Source.ProjectId, referencedProjectIds(decl.Source.ProjectId)))
            .FirstOrDefault(d => d?.Node is ClassDeclarationSyntax or RecordDeclarationSyntax);
        if (baseDecl?.Node is TypeDeclarationSyntax baseType)
            fields.AddRange(PublicProperties(baseType).Where(bf => fields.All(f => f.Name != bf.Name)));
        return (kind, fields, null);
    }

    private static IEnumerable<ModelField> PublicProperties(TypeDeclarationSyntax t) =>
        t.Members.OfType<PropertyDeclarationSyntax>()
            .Where(p => p.Modifiers.Any(SyntaxKind.PublicKeyword) && !p.Modifiers.Any(SyntaxKind.StaticKeyword)
                        && (p.ExpressionBody is not null
                            || p.AccessorList?.Accessors.Any(a => a.IsKind(SyntaxKind.GetAccessorDeclaration)) == true))
            .Select(p => Field(p.Identifier.Text, p.Type));

    private static ModelField Field(string name, TypeSyntax type)
    {
        var text = TypeName(type);
        return new ModelField(name, text, text.EndsWith('?'), TypeUnwrapper.Unwrap(text).Any(r => r.Collection));
    }
}
```

Records compare by value, and every `Edge` property involved here is a string or int, except `ViaPackages`, which is always null for these edge kinds. So `HashSet<Edge>` deduplicates correctly.

- [ ] **Step 6: Run tests**

Run: `dotnet test tests/Depenk.Tests --filter "TypeUnwrapperTests|ModelExtractorTests"`
Expected: PASS (10 unwrap cases + 4 extractor tests).

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat(analysis): type unwrapping and model extraction with cross-repo resolution"
```

### Task 10: Call-site finder

**Files:**
- Create: `src/Depenk.Analysis/CallSites/CallSiteFinder.cs`
- Test: `tests/Depenk.Tests/Analysis/CallSiteFinderTests.cs`

**Interfaces:**
- Consumes: `SourceSet` (Task 6), `DeclaredTypes` (Task 7), `ClientMethodNode`, `Ids` (Task 1)
- Produces:
  - `sealed record CallSiteScan(List<CallSiteNode> CallSites, List<Edge> Invokes)`
  - `CallSiteFinder.Find(SourceSet consumer, IReadOnlyList<ClientMethodNode> reachableClientMethods) : CallSiteScan`

**Rules (spec §3.1 step 9):**
- `reachableClientMethods` are the client methods whose project produces a package that the consumer project references (the orchestrator computes this in Task 11).
- For each call `recv.Name(...)` (including `recv.Name<T>(...)` and `recv?.Name(...)`) in the consumer:
  - resolve `DeclaredTypes.Of(recv)`, then normalize it: strip `?`, generic arguments, and namespace qualifiers
  - if a reachable client method has `TypeName == normalized` and `MethodName == Name`, that call is a call site
- Call site ID: `Ids.CallSite(repo, projectName, containingType, containingMember, line)`, where `containingMember` is the enclosing method, constructor, property or local-function name.
- `ContainingMember` field = `"{Type}.{Member}"`.
- Confidence is `Medium` for both the node and the `invokes` edge (CallSite → ClientMethod).
- Several calls on the same line to the same member make one call site with one edge per distinct client method.

- [ ] **Step 1: Write failing tests**

`tests/Depenk.Tests/Analysis/CallSiteFinderTests.cs`:

```csharp
using Depenk.Analysis.CallSites;
using Depenk.Core.Model;
using Depenk.Tests.TestUtil;

namespace Depenk.Tests.Analysis;

public class CallSiteFinderTests
{
    private static readonly SourceLocation Loc = new("x.cs", 1);

    private static ClientMethodNode Cm(string type, string method) =>
        new($"cm:Orders.Client:{type}.{method}", "orders", "proj:orders/Orders.Client", type, method, "",
            "GET", "/api/orders/{id}", "api/orders/{}", "refit", Confidence.High, Loc);

    private const string Consumer = """
        namespace Acme.Billing.Api;
        public class InvoiceBuilder(Acme.Orders.IOrdersClient orders)
        {
            private readonly IOrdersClient? _backup;

            public async Task Build(Guid id)
            {
                var order = await orders.GetOrderAsync(id);
                await _backup?.GetOrderAsync(id)!;
                var other = new OtherClient();
                other.GetOrderAsync(id);
            }

            public int Count => orders.ListAsync(1).Result.Count;
        }
        """;

    [Fact]
    public void FindsCallsOnClientTypedReceivers()
    {
        var scan = CallSiteFinder.Find(
            Src.SetFor("billing", "Billing.Api", ("billing/Api/InvoiceBuilder.cs", Consumer)),
            [Cm("IOrdersClient", "GetOrderAsync"), Cm("IOrdersClient", "ListAsync")]);

        Assert.Equal(["InvoiceBuilder.Build", "InvoiceBuilder.Build", "InvoiceBuilder.Count"],
            scan.CallSites.Select(c => c.ContainingMember));
        var first = scan.CallSites[0];
        Assert.Equal("cs:billing/Billing.Api:InvoiceBuilder.Build:8", first.Id);
        Assert.Equal(new SourceLocation("billing/Api/InvoiceBuilder.cs", 8), first.Location);
        Assert.Equal(Confidence.Medium, first.Confidence);

        Assert.Equal(3, scan.Invokes.Count);
        Assert.All(scan.Invokes, e => Assert.Equal((EdgeKind.Invokes, Confidence.Medium), (e.Kind, e.Confidence)));
        Assert.Equal("cm:Orders.Client:IOrdersClient.ListAsync", scan.Invokes[2].To);
    }

    [Fact]
    public void SameTypeNameInSeveralPackages_IsDisambiguatedByNamespace()
    {
        var a = new ClientMethodNode("cm:A.Client:R0Client.Get", "a", "proj:a/A.Client", "R0Client", "Get", "",
            "GET", "/x", "x", "refit", Confidence.High, Loc);
        var b = a with { Id = "cm:B.Client:R0Client.Get", Repo = "b", ProjectId = "proj:b/B.Client" };

        var scan = CallSiteFinder.Find(Src.SetFor("c", "C.Api", ("c.cs", """
            using Acme.B.Client;
            public class U(R0Client viaUsing, A.Client.R0Client qualified)
            {
                public void M() { viaUsing.Get(); qualified.Get(); }
            }
            """)), [a, b]);

        Assert.Equal(["cm:B.Client:R0Client.Get", "cm:A.Client:R0Client.Get"], scan.Invokes.Select(e => e.To));
    }

    [Fact]
    public void NoReachableMethods_NoCallSites()
    {
        var scan = CallSiteFinder.Find(Src.SetFor("billing", "Billing.Api", ("a.cs", Consumer)), []);
        Assert.Empty(scan.CallSites);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Depenk.Tests --filter CallSiteFinderTests`
Expected: build FAILS: `The type or namespace name 'CallSites' does not exist`.

- [ ] **Step 3: Implement**

`src/Depenk.Analysis/CallSites/CallSiteFinder.cs`:

```csharp
using Depenk.Core;
using Depenk.Core.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Depenk.Analysis.SyntaxHelpers;

namespace Depenk.Analysis.CallSites;

public sealed record CallSiteScan(List<CallSiteNode> CallSites, List<Edge> Invokes);

public static class CallSiteFinder
{
    public static CallSiteScan Find(SourceSet consumer, IReadOnlyList<ClientMethodNode> reachableClientMethods)
    {
        var sites = new Dictionary<string, CallSiteNode>();
        var edges = new List<Edge>();
        if (reachableClientMethods.Count == 0) return new CallSiteScan([], []);
        var byKey = reachableClientMethods.ToLookup(m => (m.TypeName, m.MethodName));

        foreach (var (doc, inv) in consumer.All<InvocationExpressionSyntax>())
        {
            var (receiver, name) = inv.Expression switch
            {
                MemberAccessExpressionSyntax m => (m.Expression, m.Name.Identifier.Text),
                MemberBindingExpressionSyntax b
                    when inv.Ancestors().OfType<ConditionalAccessExpressionSyntax>().FirstOrDefault() is { } ca =>
                    (ca.Expression, b.Name.Identifier.Text),
                _ => ((ExpressionSyntax?)null, ""),
            };
            if (receiver is null) continue;
            if (DeclaredTypes.Of(receiver, inv) is not { } declared) continue;
            var matches = byKey[(Normalize(declared), name)].ToList();
            if (matches.Count == 0) continue;
            if (matches.Count > 1) matches = Disambiguate(matches, declared, doc);

            var (typeName, member) = Containing(inv);
            var line = Line(inv);
            var id = Ids.CallSite(consumer.Repo, consumer.ProjectName, typeName, member, line);
            if (!sites.ContainsKey(id))
                sites[id] = new CallSiteNode(id, consumer.Repo, consumer.ProjectId, $"{typeName}.{member}",
                    Confidence.Medium, new SourceLocation(doc.RelativePath, line));
            foreach (var cm in matches)
            {
                var edge = new Edge(EdgeKind.Invokes, id, cm.Id, Confidence.Medium);
                if (!edges.Contains(edge)) edges.Add(edge);
            }
        }
        return new CallSiteScan([.. sites.Values], edges);
    }

    /// <summary>
    /// Same simple type name in several reachable packages: keep the candidates whose project name relates to the
    /// explicit qualifier (e.g. "A.Client.R0Client") or, failing that, to the file's using directives.
    /// Falls back to all candidates when nothing relates.
    /// </summary>
    private static List<ClientMethodNode> Disambiguate(List<ClientMethodNode> matches, string declared, SourceDoc doc)
    {
        var t = declared.TrimEnd('?');
        var lt = t.IndexOf('<');
        if (lt >= 0) t = t[..lt];
        var dot = t.LastIndexOf('.');
        var hints = dot > 0
            ? [t[..dot]]
            : doc.Tree.GetRoot().DescendantNodes().OfType<UsingDirectiveSyntax>()
                .Select(u => u.Name?.ToString()).OfType<string>().ToList();

        static bool Related(string ns, string project) =>
            ns == project || ns.EndsWith("." + project, StringComparison.Ordinal) || project.EndsWith("." + ns, StringComparison.Ordinal);
        static string ProjectName(ClientMethodNode m) => m.ProjectId[(m.ProjectId.IndexOf('/') + 1)..];

        var filtered = matches.Where(m => hints.Any(h => Related(h, ProjectName(m)))).ToList();
        return filtered.Count > 0 ? filtered : matches;
    }

    /// <summary>"Acme.Orders.IOrdersClient?" → "IOrdersClient"; "Wrapper&lt;T&gt;" → "Wrapper".</summary>
    private static string Normalize(string typeText)
    {
        var t = typeText.TrimEnd('?');
        var lt = t.IndexOf('<');
        if (lt >= 0) t = t[..lt];
        var dot = t.LastIndexOf('.');
        return dot >= 0 ? t[(dot + 1)..] : t;
    }

    private static (string Type, string Member) Containing(SyntaxNode n)
    {
        var type = n.Ancestors().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault()?.Identifier.Text ?? "Program";
        var member = n.Ancestors().Select(a => a switch
        {
            LocalFunctionStatementSyntax lf => lf.Identifier.Text,
            MethodDeclarationSyntax m => m.Identifier.Text,
            ConstructorDeclarationSyntax => ".ctor",
            PropertyDeclarationSyntax p => p.Identifier.Text,
            _ => null,
        }).FirstOrDefault(s => s is not null) ?? "<top-level>";
        return (type, member);
    }
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet test tests/Depenk.Tests --filter CallSiteFinderTests`
Expected: PASS (3 tests). Line check: `var order = await orders.GetOrderAsync(id);` is line 8 of the raw string, and `_backup?.GetOrderAsync(id)` is line 9. So the IDs are `...Build:8` and `...Build:9`, and `Count` is on line 14.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(analysis): find consumer call sites of client methods"
```

### Task 11: Graph diagnostics (version drift, cycles, unused)

**Files:**
- Create: `src/Depenk.Analysis/Diagnostics/GraphDiagnostics.cs`
- Test: `tests/Depenk.Tests/Analysis/GraphDiagnosticsTests.cs`

**Interfaces:**
- Consumes: graph model (Task 1)
- Produces: `GraphDiagnostics.Add(DepGraph graph)`, which appends `versionDrift`, `cycle`, `unusedEndpoint`, `unusedClientMethod` and `unusedModel` diagnostics

**Rules:**
- **`versionDrift`** (warning):
  - for each non-external package, the latest version is the producer's `produces.Version`, if set and resolved
  - otherwise it's the highest resolved version among the `references`
  - every `references` edge whose resolved version differs from the latest gets one diagnostic per package, listing the consumers that are behind
  - versions are compared with `System.Version.TryParse` on the part before any `-`; if that fails, an ordinal string compare is used
- **`cycle`** (warning): every strongly connected component with more than one repo in the `dependsOn` graph gets one diagnostic, with the repo IDs sorted and the message `"Circular dependency: a → b → a"`.
- **`unusedEndpoint`** (info): an endpoint with no incoming `targets` edge, **only** in repos that contain at least one project with a client method (other repos' endpoints are public entry points).
- **`unusedClientMethod`** (info): a client method with `Verb != null` where neither it nor any client method with the same `(ProjectId, MethodName)` has an incoming `invokes` edge. This way, calls through `IOrdersClient` also count as using the `OrdersClient` implementation.
- **`unusedModel`** (info): a model whose `ProjectId` belongs to a project with `Kind == Client`, and which has no incoming `accepts`, `returns` or `fieldOf` edge.

- [ ] **Step 1: Write failing tests**

`tests/Depenk.Tests/Analysis/GraphDiagnosticsTests.cs`:

```csharp
using Depenk.Analysis.Diagnostics;
using Depenk.Core.Model;

namespace Depenk.Tests.Analysis;

public class GraphDiagnosticsTests
{
    private static readonly SourceLocation Loc = new("x.cs", 1);

    [Fact]
    public void VersionDrift_AgainstProducerVersion()
    {
        var g = new DepGraph();
        g.Packages.Add(new PackageNode("pkg:Orders.Client", "Orders.Client", ["proj:orders/Orders.Client"]));
        g.Packages.Add(new PackageNode("pkg:Serilog", "Serilog", []));
        g.Edges.Add(new Edge(EdgeKind.Produces, "proj:orders/Orders.Client", "pkg:Orders.Client", Confidence.Certain) { Version = "3.4.1" });
        g.Edges.Add(new Edge(EdgeKind.References, "proj:billing/Billing.Api", "pkg:Orders.Client", Confidence.Certain) { Version = "3.2.0" });
        g.Edges.Add(new Edge(EdgeKind.References, "proj:gateway/Gateway.Api", "pkg:Orders.Client", Confidence.Certain) { Version = "3.4.1" });
        g.Edges.Add(new Edge(EdgeKind.References, "proj:a/A", "pkg:Serilog", Confidence.Certain) { Version = "3.0.0" });
        g.Edges.Add(new Edge(EdgeKind.References, "proj:b/B", "pkg:Serilog", Confidence.Certain) { Version = "4.0.0" });

        GraphDiagnostics.Add(g);

        var drift = g.Diagnostics.Where(d => d.Kind == DiagnosticKinds.VersionDrift).ToList();
        Assert.Single(drift); // external packages are not checked
        Assert.Equal(["pkg:Orders.Client", "proj:billing/Billing.Api"], drift[0].NodeIds);
        Assert.Contains("3.2.0", drift[0].Message);
        Assert.Contains("3.4.1", drift[0].Message);
    }

    [Fact]
    public void VersionDrift_UsesHighestReference_WhenProducerVersionUnknown_AndSkipsUnresolved()
    {
        var g = new DepGraph();
        g.Packages.Add(new PackageNode("pkg:Lib", "Lib", ["proj:lib/Lib"]));
        g.Edges.Add(new Edge(EdgeKind.Produces, "proj:lib/Lib", "pkg:Lib", Confidence.Certain));
        g.Edges.Add(new Edge(EdgeKind.References, "proj:a/A", "pkg:Lib", Confidence.Certain) { Version = "1.10.0" });
        g.Edges.Add(new Edge(EdgeKind.References, "proj:b/B", "pkg:Lib", Confidence.Certain) { Version = "1.9.0" });
        g.Edges.Add(new Edge(EdgeKind.References, "proj:c/C", "pkg:Lib", Confidence.Certain) { Version = "unresolved($(X))" });

        GraphDiagnostics.Add(g);

        Assert.Equal(["pkg:Lib", "proj:b/B"], g.Diagnostics.Single(d => d.Kind == DiagnosticKinds.VersionDrift).NodeIds);
    }

    [Fact]
    public void Cycles_BetweenRepos()
    {
        var g = new DepGraph();
        g.Edges.Add(new Edge(EdgeKind.DependsOn, "repo:orders", "repo:billing", Confidence.Certain));
        g.Edges.Add(new Edge(EdgeKind.DependsOn, "repo:billing", "repo:orders", Confidence.Certain));
        g.Edges.Add(new Edge(EdgeKind.DependsOn, "repo:gateway", "repo:orders", Confidence.Certain));

        GraphDiagnostics.Add(g);

        var cycle = g.Diagnostics.Single(d => d.Kind == DiagnosticKinds.Cycle);
        Assert.Equal(["repo:billing", "repo:orders"], cycle.NodeIds);
        Assert.Equal("Circular dependency: billing → orders → billing", cycle.Message);
    }

    [Fact]
    public void Unused_Endpoints_ClientMethods_Models()
    {
        var g = new DepGraph();
        g.Projects.Add(new ProjectNode("proj:orders/Orders.Client", "orders", "Orders.Client", "p", ProjectKind.Client, null, null, null, true));
        g.Endpoints.Add(new EndpointNode("ep:orders:GET:/a", "orders", "proj:orders/Orders.Api", "GET", "/a", "a", "C.A", [], [], Loc));
        g.Endpoints.Add(new EndpointNode("ep:orders:GET:/b", "orders", "proj:orders/Orders.Api", "GET", "/b", "b", "C.B", [], [], Loc));
        g.Endpoints.Add(new EndpointNode("ep:public:GET:/c", "public", "proj:public/Api", "GET", "/c", "c", "C.C", [], [], Loc));
        g.ClientMethods.Add(new ClientMethodNode("cm:Orders.Client:I.A", "orders", "proj:orders/Orders.Client", "I", "A", "", "GET", "/a", "a", "refit", Confidence.High, Loc));
        g.ClientMethods.Add(new ClientMethodNode("cm:Orders.Client:I.B", "orders", "proj:orders/Orders.Client", "I", "B", "", "GET", "/b", "b", "refit", Confidence.High, Loc));
        g.ClientMethods.Add(new ClientMethodNode("cm:Orders.Client:Impl.B", "orders", "proj:orders/Orders.Client", "Impl", "B", "", "GET", "/b", "b", "refit", Confidence.High, Loc));
        g.CallSites.Add(new CallSiteNode("cs:x/X:C.M:1", "x", "proj:x/X", "C.M", Confidence.Medium, Loc));
        g.Edges.Add(new Edge(EdgeKind.Invokes, "cs:x/X:C.M:1", "cm:Orders.Client:I.B", Confidence.Medium));
        g.Models.Add(new ModelNode("model:Orders.Client:Used", "orders", "proj:orders/Orders.Client", "Used", ModelKind.Class, [], null, Loc));
        g.Models.Add(new ModelNode("model:Orders.Client:Unused", "orders", "proj:orders/Orders.Client", "Unused", ModelKind.Class, [], null, Loc));
        g.Edges.Add(new Edge(EdgeKind.Targets, "cm:Orders.Client:I.A", "ep:orders:GET:/a", Confidence.High));
        g.Edges.Add(new Edge(EdgeKind.Returns, "ep:orders:GET:/a", "model:Orders.Client:Used", Confidence.High));

        GraphDiagnostics.Add(g);

        Assert.Equal(["ep:orders:GET:/b"], g.Diagnostics.Where(d => d.Kind == DiagnosticKinds.UnusedEndpoint).SelectMany(d => d.NodeIds));
        Assert.Equal(["cm:Orders.Client:I.A"], g.Diagnostics.Where(d => d.Kind == DiagnosticKinds.UnusedClientMethod).SelectMany(d => d.NodeIds));
        Assert.Equal(["model:Orders.Client:Unused"], g.Diagnostics.Where(d => d.Kind == DiagnosticKinds.UnusedModel).SelectMany(d => d.NodeIds));
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Depenk.Tests --filter GraphDiagnosticsTests`
Expected: build FAILS: `The type or namespace name 'Diagnostics' does not exist in the namespace 'Depenk.Analysis'`.

- [ ] **Step 3: Implement**

`src/Depenk.Analysis/Diagnostics/GraphDiagnostics.cs`:

```csharp
using Depenk.Core.Model;
using Depenk.Scanning;

namespace Depenk.Analysis.Diagnostics;

public static class GraphDiagnostics
{
    public static void Add(DepGraph g)
    {
        VersionDrift(g);
        Cycles(g);
        Unused(g);
    }

    private static void VersionDrift(DepGraph g)
    {
        foreach (var pkg in g.Packages.Where(p => !p.External))
        {
            var refs = g.EdgesOf(EdgeKind.References)
                .Where(e => e.To == pkg.Id && e.Version is not null && !ProjectFile.IsUnresolved(e.Version)).ToList();
            if (refs.Count == 0) continue;
            var produced = g.EdgesOf(EdgeKind.Produces)
                .Where(e => e.To == pkg.Id && e.Version is not null && !ProjectFile.IsUnresolved(e.Version))
                .Select(e => e.Version!).FirstOrDefault();
            var latest = produced ?? refs.Select(r => r.Version!).OrderBy(v => v, VersionComparer.Instance).Last();
            var behind = refs.Where(r => r.Version != latest).OrderBy(r => r.From, StringComparer.Ordinal).ToList();
            if (behind.Count == 0) continue;
            g.Diagnostics.Add(new Diagnostic(DiagnosticKinds.VersionDrift, "warning",
                [pkg.Id, .. behind.Select(b => b.From)],
                $"{pkg.PackageId}: latest {latest}; behind: {string.Join(", ", behind.Select(b => $"{b.From} ({b.Version})"))}"));
        }
    }

    private static void Cycles(DepGraph g)
    {
        var adj = g.EdgesOf(EdgeKind.DependsOn).GroupBy(e => e.From).ToDictionary(x => x.Key, x => x.Select(e => e.To).ToList());
        var nodes = adj.Keys.Concat(adj.Values.SelectMany(v => v)).Distinct().Order(StringComparer.Ordinal).ToList();

        // Tarjan's strongly connected components
        var index = 0;
        var indices = new Dictionary<string, int>();
        var low = new Dictionary<string, int>();
        var stack = new Stack<string>();
        var onStack = new HashSet<string>();
        var sccs = new List<List<string>>();

        void Visit(string v)
        {
            indices[v] = low[v] = index++;
            stack.Push(v); onStack.Add(v);
            foreach (var w in adj.GetValueOrDefault(v, []))
            {
                if (!indices.ContainsKey(w)) { Visit(w); low[v] = Math.Min(low[v], low[w]); }
                else if (onStack.Contains(w)) low[v] = Math.Min(low[v], indices[w]);
            }
            if (low[v] != indices[v]) return;
            var scc = new List<string>();
            string x;
            do { x = stack.Pop(); onStack.Remove(x); scc.Add(x); } while (x != v);
            sccs.Add(scc);
        }

        foreach (var n in nodes) if (!indices.ContainsKey(n)) Visit(n);

        foreach (var scc in sccs.Where(s => s.Count > 1))
        {
            var ids = scc.Order(StringComparer.Ordinal).ToList();
            var names = ids.Select(i => i["repo:".Length..]).ToList();
            g.Diagnostics.Add(new Diagnostic(DiagnosticKinds.Cycle, "warning", ids,
                $"Circular dependency: {string.Join(" → ", names)} → {names[0]}"));
        }
    }

    private static void Unused(DepGraph g)
    {
        var targeted = g.EdgesOf(EdgeKind.Targets).Select(e => e.To).ToHashSet();
        var reposWithClients = g.ClientMethods.Select(c => c.Repo).ToHashSet();
        foreach (var ep in g.Endpoints.Where(e => reposWithClients.Contains(e.Repo) && !targeted.Contains(e.Id)))
            g.Diagnostics.Add(new Diagnostic(DiagnosticKinds.UnusedEndpoint, "info", [ep.Id],
                $"{ep.Verb} {ep.Route} ({ep.Handler}) is not called by any client method"));

        var invoked = g.EdgesOf(EdgeKind.Invokes).Select(e => e.To).ToHashSet();
        // an interface method and its implementation share (project, method name): either being invoked counts for both
        var invokedNames = g.ClientMethods.Where(c => invoked.Contains(c.Id)).Select(c => (c.ProjectId, c.MethodName)).ToHashSet();
        foreach (var cm in g.ClientMethods.Where(c => c.Verb is not null && !invokedNames.Contains((c.ProjectId, c.MethodName))))
            g.Diagnostics.Add(new Diagnostic(DiagnosticKinds.UnusedClientMethod, "info", [cm.Id],
                $"{cm.TypeName}.{cm.MethodName} has no call sites in the workspace"));

        var clientProjects = g.Projects.Where(p => p.Kind == ProjectKind.Client).Select(p => p.Id).ToHashSet();
        var referencedModels = g.Edges.Where(e => e.Kind is EdgeKind.Accepts or EdgeKind.Returns or EdgeKind.FieldOf)
            .Select(e => e.To).ToHashSet();
        foreach (var m in g.Models.Where(m => m.ProjectId is not null && clientProjects.Contains(m.ProjectId)
                                              && !referencedModels.Contains(m.Id)))
            g.Diagnostics.Add(new Diagnostic(DiagnosticKinds.UnusedModel, "info", [m.Id],
                $"{m.FullName} is not used by any endpoint or model"));
    }

    private sealed class VersionComparer : IComparer<string>
    {
        public static readonly VersionComparer Instance = new();

        public int Compare(string? a, string? b)
        {
            if (Version.TryParse(a?.Split('-')[0], out var va) && Version.TryParse(b?.Split('-')[0], out var vb))
                return va.CompareTo(vb);
            return string.CompareOrdinal(a, b);
        }
    }
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet test tests/Depenk.Tests --filter GraphDiagnosticsTests`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(analysis): version drift, repo cycle and unused-item diagnostics"
```

### Task 12: Scan orchestrator + `depenk scan` CLI + fixture workspace golden test

**Files:**
- Create: `src/Depenk.Analysis/ScanOrchestrator.cs`
- Modify: `src/depenk/depenk.csproj` (tool packaging), `src/depenk/Program.cs`
- Modify: `tests/Depenk.Tests/Depenk.Tests.csproj` (copy fixtures to output)
- Create: the fixture workspace under `tests/fixtures/workspace/` (files listed in Step 1)
- Test: `tests/Depenk.Tests/FixtureScanTests.cs`, with the snapshot in `tests/Depenk.Tests/Snapshots/`

**Interfaces:**
- Consumes: everything from Tasks 1–11
- Produces:
  - `ScanOrchestrator.Scan(string workspace) : DepGraph`: the full pipeline from spec §3.1, with deterministic sorting
  - `ScanOrchestrator.GraphPath(string workspace) : string` = `<workspace>/.depenk/graph.json`
  - CLI: `depenk scan [--workspace <dir>]` (default: current directory), which writes the graph and prints a summary; exit code `0` on success, `2` on `ConfigException`

**Pipeline (the order matters):**
1. load the config
2. discover repos and add the RepoNodes
3. `PackageGraphBuilder.LoadProjects`
4. load a `SourceSet` for each project, excluding files under a *nested* project's directory, and add a `parseError` diagnostic (severity `info`) for each document with an error-level Roslyn diagnostic
5. for each non-test project, run both endpoint finders and `ClientMethodFinder`, then classify it with those signals
6. `PackageGraphBuilder.Build`
7. keep endpoints only from `Api` projects and client methods only from `Client` projects; deduplicate endpoint IDs by appending `#2`, `#3`…
8. `ClientEndpointLinker.Link`
9. `ModelExtractor`: a project's referenced projects are its `ProjectReference` targets plus the producers of the packages it references; the contract projects are the `Client` projects
10. `CallSiteFinder` for each non-test project, where the reachable client methods are those whose `ProjectId` is among its referenced projects
11. fill in `dependsOn.CallCount` (the number of `invokes` edges from call sites in the `From` repo to client methods in the `To` repo)
12. `GraphDiagnostics.Add`
13. sort every list: nodes by `Id` (ordinal); edges by kind, from, to, source, status code, field name; diagnostics by kind, first node ID, message

- [ ] **Step 1: Create the fixture workspace**

Every repo directory is committed **without** a `.git` folder, because git can't commit nested `.git` directories. The test creates `.git/HEAD` at runtime using `TempWorkspace.Repo`.

`tests/fixtures/workspace/depenk.yml`:

```yaml
httpWrappers:
  - type: "*.IApiHttpClient"
    methods: { "Get*": GET, "Post*": POST, "Put*": PUT, "Delete*": DELETE }
    routeArgument: 0
```

`tests/fixtures/workspace/orders/Directory.Build.props`:

```xml
<Project><PropertyGroup><OrdersVersion>3.4.1</OrdersVersion></PropertyGroup></Project>
```

`tests/fixtures/workspace/orders/src/Orders.Client/Orders.Client.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <PackageId>Orders.Client</PackageId>
    <Version>$(OrdersVersion)</Version>
    <IsPackable>true</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Acme.Http" Version="1.0.0" />
    <PackageReference Include="Customers.Client" Version="2.0.0" />
    <PackageReference Include="Shared.Kernel" Version="1.0.0" />
  </ItemGroup>
</Project>
```

`tests/fixtures/workspace/orders/src/Orders.Client/OrdersClient.cs`:

```csharp
namespace Acme.Orders.Client;

public interface IOrdersClient
{
    Task<OrderDto> GetOrderAsync(Guid id);
    Task<List<OrderDto>> ListAsync(int page);
    Task<OrderDto> CreateAsync(CreateOrderRequest request);
}

public sealed class OrdersClient(Acme.Http.IApiHttpClient http) : IOrdersClient
{
    public Task<OrderDto> GetOrderAsync(Guid id) => http.GetJsonAsync<OrderDto>($"api/orders/{id}");
    public Task<List<OrderDto>> ListAsync(int page) => http.GetJsonAsync<List<OrderDto>>($"api/orders?page={page}");
    public Task<OrderDto> CreateAsync(CreateOrderRequest request) => http.PostJsonAsync<OrderDto>("api/orders", request);
}
```

`tests/fixtures/workspace/orders/src/Orders.Client/Models.cs`:

```csharp
using Acme.Customers.Client;
using Acme.Shared;

namespace Acme.Orders.Client;

public class OrderDto
{
    public Guid Id { get; init; }
    public OrderStatus Status { get; init; }
    public CustomerDto? Customer { get; init; }
    public List<OrderLineDto> Lines { get; init; } = [];
}

public class OrderLineDto
{
    public string Sku { get; set; } = "";
    public int Qty { get; set; }
    public Money UnitPrice { get; set; } = new(0, "USD");
}

public enum OrderStatus { Pending, Paid, Shipped }

public record CreateOrderRequest(Guid CustomerId, List<OrderLineDto> Lines);
```

`tests/fixtures/workspace/orders/src/Orders.Api/Orders.Api.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <ItemGroup>
    <PackageReference Include="Billing.Client" Version="1.0.0" />
    <PackageReference Include="Shared.Kernel" Version="1.0.0" />
    <ProjectReference Include="..\Orders.Client\Orders.Client.csproj" />
  </ItemGroup>
</Project>
```

`tests/fixtures/workspace/orders/src/Orders.Api/OrdersController.cs`:

```csharp
using Acme.Orders.Client;
using Microsoft.AspNetCore.Mvc;

namespace Acme.Orders.Api;

[ApiController]
[Route("api/[controller]")]
public class OrdersController : ControllerBase
{
    [HttpGet("{id}")] public Task<ActionResult<OrderDto>> Get(Guid id) => null!;
    [HttpGet] public Task<ActionResult<List<OrderDto>>> List([FromQuery] int page) => null!;
    [HttpPost] public Task<ActionResult<OrderDto>> Create(CreateOrderRequest request) => null!;
    [HttpDelete("{id}")] public Task<IActionResult> Delete(Guid id) => null!;
}
```

`tests/fixtures/workspace/orders/src/Orders.Api/InvoiceNotifier.cs`:

```csharp
using Acme.Billing.Client;

namespace Acme.Orders.Api;

public class InvoiceNotifier(IBillingApi billing)
{
    public Task NotifyAsync(Guid orderId) => billing.CreateInvoice(new CreateInvoiceRequest(orderId));
}
```

`tests/fixtures/workspace/orders/tests/Orders.Tests/Orders.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.0" />
    <ProjectReference Include="..\..\src\Orders.Api\Orders.Api.csproj" />
  </ItemGroup>
</Project>
```

`tests/fixtures/workspace/customers/src/Customers.Client/Customers.Client.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <PackageId>Customers.Client</PackageId>
    <Version>2.0.0</Version>
    <IsPackable>true</IsPackable>
  </PropertyGroup>
</Project>
```

`tests/fixtures/workspace/customers/src/Customers.Client/CustomerDto.cs`:

```csharp
namespace Acme.Customers.Client;

public class CustomerDto
{
    public Guid Id { get; init; }
    public string Name { get; init; } = "";
}
```

`tests/fixtures/workspace/customers/src/Customers.Client/CustomersClient.cs` (NSwag-style):

```csharp
namespace Acme.Customers.Client;

public partial class CustomersClient
{
    public virtual Task<CustomerDto> GetCustomerAsync(string key) => GetCustomerAsync(key, CancellationToken.None);

    public virtual async Task<CustomerDto> GetCustomerAsync(string key, CancellationToken cancellationToken)
    {
        var urlBuilder_ = new System.Text.StringBuilder();
        urlBuilder_.Append("api/customers/");
        urlBuilder_.Append(Uri.EscapeDataString(key));
        using var request_ = new System.Net.Http.HttpRequestMessage();
        request_.Method = new System.Net.Http.HttpMethod("GET");
        return await Task.FromResult(new CustomerDto());
    }
}
```

`tests/fixtures/workspace/customers/src/Customers.Api/Customers.Api.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <ItemGroup>
    <ProjectReference Include="..\Customers.Client\Customers.Client.csproj" />
  </ItemGroup>
</Project>
```

`tests/fixtures/workspace/customers/src/Customers.Api/Program.cs` (two routes that collide on purpose):

```csharp
using Acme.Customers.Client;

var app = WebApplication.Create(args);
var customers = app.MapGroup("/api/customers");
customers.MapGet("{id:guid}", (Guid id) => TypedResults.Ok(new CustomerDto()));
customers.MapGet("{slug}", (string slug) => TypedResults.Ok(new CustomerDto()));
app.Run();
```

`tests/fixtures/workspace/billing/Directory.Packages.props`:

```xml
<Project>
  <ItemGroup>
    <PackageVersion Include="Orders.Client" Version="3.2.0" />
    <PackageVersion Include="Customers.Client" Version="2.0.0" />
  </ItemGroup>
</Project>
```

`tests/fixtures/workspace/billing/src/Billing.Api/Billing.Api.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <ItemGroup>
    <PackageReference Include="Orders.Client" />
    <PackageReference Include="Customers.Client" />
    <ProjectReference Include="..\Billing.Client\Billing.Client.csproj" />
  </ItemGroup>
</Project>
```

`tests/fixtures/workspace/billing/src/Billing.Api/InvoicesController.cs`:

```csharp
using Acme.Billing.Client;
using Microsoft.AspNetCore.Mvc;

namespace Acme.Billing.Api;

[ApiController]
[Route("api/invoices")]
public class InvoicesController : ControllerBase
{
    [HttpPost] public Task<ActionResult<InvoiceDto>> Create(CreateInvoiceRequest request) => null!;
}
```

`tests/fixtures/workspace/billing/src/Billing.Api/InvoiceBuilder.cs`:

```csharp
using Acme.Customers.Client;
using Acme.Orders.Client;

namespace Acme.Billing.Api;

public class InvoiceBuilder(IOrdersClient orders, CustomersClient customers)
{
    public async Task<decimal> BuildAsync(Guid orderId)
    {
        var order = await orders.GetOrderAsync(orderId);
        var customer = await customers.GetCustomerAsync(order.Customer!.Id.ToString());
        return order.Lines.Sum(l => l.Qty * l.UnitPrice.Amount);
    }
}
```

`tests/fixtures/workspace/billing/src/Billing.Api/Broken.cs` (a deliberate syntax error):

```csharp
namespace Acme.Billing.Api;

public class Broken { public void M( { } }
```

`tests/fixtures/workspace/billing/src/Billing.Client/Billing.Client.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <PackageId>Billing.Client</PackageId>
    <Version>1.0.0</Version>
    <IsPackable>true</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Refit" Version="7.2.1" />
  </ItemGroup>
</Project>
```

`tests/fixtures/workspace/billing/src/Billing.Client/IBillingApi.cs`:

```csharp
using Refit;

namespace Acme.Billing.Client;

public interface IBillingApi
{
    [Post("/api/invoices")]
    Task<InvoiceDto> CreateInvoice([Body] CreateInvoiceRequest request);
}

public record CreateInvoiceRequest(Guid OrderId);
public record InvoiceDto(Guid Id, decimal Total);
```

`tests/fixtures/workspace/gateway/src/Gateway.Api/Gateway.Api.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <ItemGroup>
    <PackageReference Include="Orders.Client" Version="3.4.1" />
  </ItemGroup>
</Project>
```

`tests/fixtures/workspace/gateway/src/Gateway.Api/OrdersProxy.cs`:

```csharp
using Acme.Orders.Client;

namespace Acme.Gateway.Api;

public class OrdersProxy
{
    private readonly IOrdersClient _orders;
    public OrdersProxy(IOrdersClient orders) => _orders = orders;
    public Task<List<OrderDto>> List(int page) => _orders.ListAsync(page);
    public Task<OrderDto> Create(CreateOrderRequest r) => _orders.CreateAsync(r);
}
```

`tests/fixtures/workspace/shared/src/Shared.Kernel/Shared.Kernel.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <PackageId>Shared.Kernel</PackageId>
    <Version>1.0.0</Version>
    <IsPackable>true</IsPackable>
  </PropertyGroup>
</Project>
```

`tests/fixtures/workspace/shared/src/Shared.Kernel/Money.cs`:

```csharp
namespace Acme.Shared;

public record Money(decimal Amount, string Currency);
```

Add to `tests/Depenk.Tests/Depenk.Tests.csproj`:

```xml
<ItemGroup>
  <None Include="..\fixtures\**\*" LinkBase="fixtures" CopyToOutputDirectory="PreserveNewest" />
</ItemGroup>
```

- [ ] **Step 2: Write the failing fixture test**

`tests/Depenk.Tests/FixtureScanTests.cs`:

```csharp
using Depenk.Analysis;
using Depenk.Core;
using Depenk.Core.Model;
using Depenk.Tests.TestUtil;
using static VerifyXunit.Verifier;

namespace Depenk.Tests;

public class FixtureScanTests
{
    private static readonly string[] RepoNames = ["billing", "customers", "gateway", "orders", "shared"];

    internal static TempWorkspace CopyFixture()
    {
        var ws = new TempWorkspace();
        var src = Path.Combine(AppContext.BaseDirectory, "fixtures", "workspace");
        foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
            ws.File(Path.GetRelativePath(src, file).Replace('\\', '/'), File.ReadAllText(file));
        foreach (var r in RepoNames) ws.Repo(r);
        return ws;
    }

    private static (DepGraph Graph, string Json, string Root) ScanFixture()
    {
        using var ws = CopyFixture();
        var g = new ScanOrchestrator().Scan(ws.Root);
        g.GeneratedAt = DateTimeOffset.UnixEpoch;
        return (g, GraphJson.Serialize(g), ws.Root);
    }

    [Fact]
    public void RepoDependencies()
    {
        var (g, _, _) = ScanFixture();
        Assert.Equal(
            ["billing→customers", "billing→orders", "gateway→orders", "orders→billing", "orders→customers", "orders→shared"],
            g.EdgesOf(EdgeKind.DependsOn).Select(e => $"{e.From[5..]}→{e.To[5..]}"));
        Assert.Equal(1, g.EdgesOf(EdgeKind.DependsOn).Single(e => e.From == "repo:billing" && e.To == "repo:orders").CallCount);
        Assert.Equal(2, g.EdgesOf(EdgeKind.DependsOn).Single(e => e.From == "repo:gateway").CallCount);
    }

    [Fact]
    public void ProjectKinds()
    {
        var (g, _, _) = ScanFixture();
        var kinds = g.Projects.ToDictionary(p => p.Name, p => p.Kind);
        Assert.Equal(ProjectKind.Api, kinds["Orders.Api"]);
        Assert.Equal(ProjectKind.Client, kinds["Orders.Client"]);
        Assert.Equal(ProjectKind.Client, kinds["Customers.Client"]);
        Assert.Equal(ProjectKind.Client, kinds["Billing.Client"]);
        Assert.Equal(ProjectKind.Library, kinds["Shared.Kernel"]);
        Assert.Equal(ProjectKind.Test, kinds["Orders.Tests"]);
        Assert.Equal("3.4.1", g.Projects.Single(p => p.Name == "Orders.Client").Version);
    }

    [Fact]
    public void EndToEndFlow_CallSiteToEndpointToModel()
    {
        var (g, _, _) = ScanFixture();
        var target = g.EdgesOf(EdgeKind.Targets).Single(e => e.From == "cm:Orders.Client:IOrdersClient.GetOrderAsync");
        Assert.Equal(("ep:orders:GET:/api/orders/{id}", "configured-wrapper"), (target.To, target.Strategy));
        Assert.Contains(g.EdgesOf(EdgeKind.Invokes), e => e.From.StartsWith("cs:billing/Billing.Api:InvoiceBuilder.BuildAsync:")
                                                          && e.To == "cm:Orders.Client:IOrdersClient.GetOrderAsync");
        Assert.Contains(g.EdgesOf(EdgeKind.Returns), e => e.From == "ep:orders:GET:/api/orders/{id}"
                                                          && e.To == "model:Orders.Client:Acme.Orders.Client.OrderDto");
        Assert.Contains(g.EdgesOf(EdgeKind.FieldOf), e => e.From == "model:Orders.Client:Acme.Orders.Client.OrderDto"
                                                          && e.To == "model:Customers.Client:Acme.Customers.Client.CustomerDto");
        Assert.Contains(g.EdgesOf(EdgeKind.FieldOf), e => e.To == "model:Shared.Kernel:Acme.Shared.Money");
        Assert.Contains(g.EdgesOf(EdgeKind.Targets), e => e.From == "cm:Billing.Client:IBillingApi.CreateInvoice"
                                                          && e.To == "ep:billing:POST:/api/invoices");
    }

    [Fact]
    public void Diagnostics()
    {
        var (g, _, _) = ScanFixture();
        string[] Of(string kind) => g.Diagnostics.Where(d => d.Kind == kind).Select(d => d.NodeIds[0]).ToArray();

        Assert.Equal(["pkg:Orders.Client"], Of(DiagnosticKinds.VersionDrift));
        Assert.Equal(["repo:billing"], Of(DiagnosticKinds.Cycle));
        Assert.Equal(["cm:Customers.Client:CustomersClient.GetCustomerAsync"], Of(DiagnosticKinds.AmbiguousRoute));
        Assert.Equal(["ep:orders:DELETE:/api/orders/{id}"], Of(DiagnosticKinds.UnusedEndpoint));
        Assert.Empty(Of(DiagnosticKinds.UnusedClientMethod));
        var parse = g.Diagnostics.Single(d => d.Kind == DiagnosticKinds.ParseError);
        Assert.StartsWith("billing/src/Billing.Api/Broken.cs: line 3:", parse.Message);
    }

    [Fact]
    public void Json_IsPortable_AndDeterministic()
    {
        var (_, json1, root) = ScanFixture();
        var (_, json2, _) = ScanFixture();
        Assert.Equal(json1, json2);
        Assert.DoesNotContain("\\\\", json1);
        Assert.DoesNotContain(root.Replace('\\', '/'), json1);
        Assert.DoesNotContain(root, json1);
    }

    [Fact]
    public Task Snapshot() => Verify(ScanFixture().Json, extension: "json").UseDirectory("Snapshots");
}
```

- [ ] **Step 3: Run to verify failure**

Run: `dotnet test tests/Depenk.Tests --filter FixtureScanTests`
Expected: build FAILS: `The type or namespace name 'ScanOrchestrator' could not be found`.

- [ ] **Step 4: Implement ScanOrchestrator**

`src/Depenk.Analysis/ScanOrchestrator.cs`:

```csharp
using Depenk.Analysis.CallSites;
using Depenk.Analysis.Clients;
using Depenk.Analysis.Diagnostics;
using Depenk.Analysis.Endpoints;
using Depenk.Analysis.Linking;
using Depenk.Analysis.Models;
using Depenk.Core;
using Depenk.Core.Model;
using Depenk.Scanning;
using Depenk.Scanning.Config;
using Microsoft.CodeAnalysis;

namespace Depenk.Analysis;

public sealed class ScanOrchestrator
{
    public static string GraphPath(string workspace) => Path.Combine(workspace, ".depenk", "graph.json");

    public DepGraph Scan(string workspace)
    {
        workspace = Path.GetFullPath(workspace);
        var config = ConfigLoader.Load(workspace);
        var g = new DepGraph { Workspace = "." };

        var repos = RepoDiscovery.Discover(workspace, config);
        g.Repos.AddRange(repos.Select(r => new RepoNode(Ids.Repo(r.Name), r.Name, r.RelativePath, r.HeadSha, r.Dirty)));
        var projects = PackageGraphBuilder.LoadProjects(workspace, repos, config, g);

        var sources = projects.ToDictionary(p => p.Id, p => LoadSources(workspace, p, projects, g));

        IEndpointFinder[] endpointFinders = [new ControllerEndpointFinder(), new MinimalApiEndpointFinder()];
        var clientFinder = new ClientMethodFinder(config);
        var endpoints = new Dictionary<string, List<EndpointNode>>();
        var clients = new Dictionary<string, ClientScanResult>();
        var kinds = new Dictionary<string, ProjectKind>();
        foreach (var p in projects)
        {
            var test = p.File.IsTestProject;
            endpoints[p.Id] = test ? [] : endpointFinders.SelectMany(f => f.Find(sources[p.Id])).ToList();
            clients[p.Id] = test ? new ClientScanResult([], false) : clientFinder.Find(sources[p.Id]);
            kinds[p.Id] = ProjectClassifier.Classify(p.File,
                new ProjectSignals(endpoints[p.Id].Count > 0, clients[p.Id].AnyHit), config);
        }
        PackageGraphBuilder.Build(workspace, projects, kinds, config, g);

        foreach (var p in projects)
        {
            if (kinds[p.Id] == ProjectKind.Api) g.Endpoints.AddRange(endpoints[p.Id]);
            if (kinds[p.Id] == ProjectKind.Client) g.ClientMethods.AddRange(clients[p.Id].Methods);
        }
        DedupeEndpointIds(g);
        ClientEndpointLinker.Link(g);

        var referenced = ReferencedProjects(projects, g);
        IReadOnlyList<string> RefsOf(string id) => referenced.GetValueOrDefault(id, []);
        new ModelExtractor([.. sources.Values], RefsOf)
            .Extract(g, kinds.Where(k => k.Value == ProjectKind.Client).Select(k => k.Key).ToHashSet());

        foreach (var p in projects.Where(p => !p.File.IsTestProject))
        {
            var refs = RefsOf(p.Id).ToHashSet();
            var scan = CallSiteFinder.Find(sources[p.Id], g.ClientMethods.Where(cm => refs.Contains(cm.ProjectId)).ToList());
            g.CallSites.AddRange(scan.CallSites);
            g.Edges.AddRange(scan.Invokes);
        }

        FillCallCounts(g);
        GraphDiagnostics.Add(g);
        Sort(g);
        return g;
    }

    private static SourceSet LoadSources(string workspace, ScannedProject p, IReadOnlyList<ScannedProject> all, DepGraph g)
    {
        var set = SourceSet.Load(workspace, p.Repo.Name, p.Id, p.File.Name, p.Directory,
            (rel, ex) => ParseError(g, p.Repo.Name, $"{rel}: {ex.Message}"));
        var nested = all.Where(o => o != p && IsUnder(o.Directory, p.Directory))
            .Select(o => PathUtil.Rel(workspace, o.Directory) + "/").ToList();
        var docs = set.Docs.Where(d => !nested.Any(n => d.RelativePath.StartsWith(n, StringComparison.Ordinal))).ToList();
        foreach (var doc in docs)
        {
            var err = doc.Tree.GetDiagnostics().FirstOrDefault(d => d.Severity == DiagnosticSeverity.Error);
            if (err is not null)
                ParseError(g, p.Repo.Name,
                    $"{doc.RelativePath}: line {err.Location.GetLineSpan().StartLinePosition.Line + 1}: {err.GetMessage()}");
        }
        return new SourceSet(set.Repo, set.ProjectId, set.ProjectName, docs);
    }

    private static bool IsUnder(string dir, string parent) =>
        dir.Length > parent.Length
        && dir.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static void ParseError(DepGraph g, string repo, string message) =>
        g.Diagnostics.Add(new Diagnostic(DiagnosticKinds.ParseError, "info", [Ids.Repo(repo)], message));

    private static void DedupeEndpointIds(DepGraph g)
    {
        var seen = new Dictionary<string, int>();
        for (var i = 0; i < g.Endpoints.Count; i++)
        {
            var id = g.Endpoints[i].Id;
            seen[id] = seen.GetValueOrDefault(id) + 1;
            if (seen[id] > 1) g.Endpoints[i] = g.Endpoints[i] with { Id = $"{id}#{seen[id]}" };
        }
    }

    private static Dictionary<string, IReadOnlyList<string>> ReferencedProjects(IReadOnlyList<ScannedProject> projects, DepGraph g)
    {
        var byPath = projects.ToDictionary(p => p.File.AbsolutePath, p => p.Id, StringComparer.OrdinalIgnoreCase);
        var producers = g.Packages.ToDictionary(p => p.PackageId, p => p.ProducerProjectIds, StringComparer.OrdinalIgnoreCase);
        return projects.ToDictionary(p => p.Id, p => (IReadOnlyList<string>)
            p.File.ProjectReferences.Select(r => byPath.GetValueOrDefault(r)).OfType<string>()
                .Concat(p.File.PackageReferences.SelectMany(r => producers.GetValueOrDefault(r.Id, [])))
                .Distinct().ToList());
    }

    private static void FillCallCounts(DepGraph g)
    {
        var siteRepo = g.CallSites.ToDictionary(c => c.Id, c => c.Repo);
        var methodRepo = g.ClientMethods.ToDictionary(c => c.Id, c => c.Repo);
        var counts = g.EdgesOf(EdgeKind.Invokes)
            .GroupBy(e => (From: Ids.Repo(siteRepo[e.From]), To: Ids.Repo(methodRepo[e.To])))
            .ToDictionary(x => x.Key, x => x.Count());
        for (var i = 0; i < g.Edges.Count; i++)
            if (g.Edges[i].Kind == EdgeKind.DependsOn)
                g.Edges[i] = g.Edges[i] with { CallCount = counts.GetValueOrDefault((g.Edges[i].From, g.Edges[i].To)) };
    }

    private static void Sort(DepGraph g)
    {
        static int ById<T>(T a, T b, Func<T, string> id) => string.CompareOrdinal(id(a), id(b));
        g.Repos.Sort((a, b) => ById(a, b, x => x.Id));
        g.Projects.Sort((a, b) => ById(a, b, x => x.Id));
        g.Packages.Sort((a, b) => ById(a, b, x => x.Id));
        g.Endpoints.Sort((a, b) => ById(a, b, x => x.Id));
        g.ClientMethods.Sort((a, b) => ById(a, b, x => x.Id));
        g.CallSites.Sort((a, b) => ById(a, b, x => x.Id));
        g.Models.Sort((a, b) => ById(a, b, x => x.Id));
        var edges = g.Edges.OrderBy(e => e.Kind).ThenBy(e => e.From, StringComparer.Ordinal).ThenBy(e => e.To, StringComparer.Ordinal)
            .ThenBy(e => e.Source, StringComparer.Ordinal).ThenBy(e => e.StatusCode).ThenBy(e => e.FieldName, StringComparer.Ordinal).ToList();
        g.Edges.Clear(); g.Edges.AddRange(edges);
        var diags = g.Diagnostics.OrderBy(d => d.Kind, StringComparer.Ordinal)
            .ThenBy(d => d.NodeIds.FirstOrDefault(), StringComparer.Ordinal).ThenBy(d => d.Message, StringComparer.Ordinal).ToList();
        g.Diagnostics.Clear(); g.Diagnostics.AddRange(diags);
    }
}
```

`CallCount = 0` is written as `0`, not omitted, for dependsOn edges that have no detected calls. That's intentional: it separates "no calls found" from "not computed".

- [ ] **Step 5: Implement the CLI**

Replace the contents of `src/depenk/depenk.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <PackAsTool>true</PackAsTool>
    <ToolCommandName>depenk</ToolCommandName>
    <PackageId>depenk</PackageId>
    <Version>0.1.0</Version>
    <Description>Cross-repo C# dependency explorer: NuGet client packages, HTTP endpoints and models.</Description>
    <PackageLicenseExpression>MIT</PackageLicenseExpression>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="System.CommandLine" Version="2.0.0-beta4.22272.1" />
    <ProjectReference Include="..\Depenk.Analysis\Depenk.Analysis.csproj" />
  </ItemGroup>
</Project>
```

`src/depenk/Program.cs`:

```csharp
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
            $"in {sw.Elapsed.TotalSeconds:F1}s → {Path.GetRelativePath(ws, path)} ({warnings} warnings)");
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
```

- [ ] **Step 6: Run the tests and accept the snapshot**

Run: `dotnet test tests/Depenk.Tests --filter FixtureScanTests`
Expected: 5 PASS and 1 FAIL. `Snapshot` fails on the first run and creates `tests/Depenk.Tests/Snapshots/FixtureScanTests.Snapshot.received.json`.

Open the `.received.json` file and check it by eye:
- 5 repos, 9 projects
- no `\` in paths
- `schemaVersion: 1`

Then accept it:

```bash
mv tests/Depenk.Tests/Snapshots/FixtureScanTests.Snapshot.received.json tests/Depenk.Tests/Snapshots/FixtureScanTests.Snapshot.verified.json
dotnet test tests/Depenk.Tests
```

Expected: the whole suite passes.

If `Diagnostics` fails on the parse-error line number, the fixture's `Broken.cs` must keep the `public class Broken` line as **line 3**. If `RepoDependencies` shows an extra edge, look at which package it goes through (`ViaPackages`) before changing anything. The expected set is derived from the fixture csproj files listed in Step 1.

- [ ] **Step 7: Smoke-test the CLI**

The copied fixture has no `.git` folders, so smoke-test against a real folder of clones instead:

```bash
dotnet run --project src/depenk -- scan --workspace /c/code/repos
```

Expected: one summary line (`Scanned N repos, …`), and `/c/code/repos/.depenk/graph.json` exists. No crash, even though those repos aren't .NET services.

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "feat: scan orchestrator, depenk scan CLI and fixture workspace golden test"
```

### Task 13: Incremental rescan (parse cache + manifest) + performance smoke test

**Files:**
- Create: `src/Depenk.Analysis/ParseCache.cs`, `src/Depenk.Analysis/WorkspaceManifest.cs`
- Modify: `src/Depenk.Analysis/SourceSet.cs` (add `Hash` to `SourceDoc`; `Load` takes an optional `ParseCache`)
- Modify: `src/Depenk.Analysis/ScanOrchestrator.cs` (keeps a cache; loads projects in parallel; caches per-project analysis)
- Modify: `src/depenk/Program.cs` (`--force`, skip when up to date, write the manifest)
- Test: `tests/Depenk.Tests/IncrementalScanTests.cs`, `tests/Depenk.Tests/PerfSmokeTests.cs`, `tests/Depenk.Tests/TestUtil/SyntheticWorkspace.cs`

**Interfaces:**
- Consumes: `ScanOrchestrator`, `SourceSet`, `FixtureScanTests.CopyFixture()` (Task 12)
- Produces:
  - `sealed record SourceDoc(string RelativePath, SyntaxTree Tree, string Hash = "")`: existing callers are unchanged
  - `SourceSet.Load(string workspace, string repo, string projectId, string projectName, string projectDir, Action<string, Exception> onError, ParseCache? cache = null)`
  - `sealed class ParseCache`:
    - `(SyntaxTree Tree, string Hash) GetOrParse(string absolutePath, string relativePath)`
    - `int ParseCount { get; }`: cumulative parses
    - `static string HashText(string text)`: SHA-256 as lowercase hex
  - `ScanOrchestrator(ParseCache? cache = null)`: a `Scan` on the same instance reuses unchanged trees and the per-project analysis
  - `WorkspaceManifest`:
    - `Compute(string workspace) : SortedDictionary<string,string>`, with keys `repo:{name}` → head sha, and relative paths of `*.cs`, `*.csproj`, `*.props` and `depenk.yml` → content hash
    - `Save(string workspace, SortedDictionary<string,string>)`
    - `IsUpToDate(string workspace) : bool`: true only when `.depenk/graph.json` and `.depenk/manifest.json` both exist and the manifest matches
    - `ManifestPath(string workspace) : string`
- CLI: `depenk scan [--workspace] [--force]` prints `Graph is up to date (.depenk/graph.json)` and exits `0` without scanning when `IsUpToDate` is true and `--force` isn't given

**Performance budgets (spec §10):** a synthetic workspace of 50 repos with 5,000 endpoints must do a full scan in under 60 s, and an incremental rescan after one file changes (same orchestrator instance) in under 2 s.

- [ ] **Step 1: Write the synthetic workspace generator**

`tests/Depenk.Tests/TestUtil/SyntheticWorkspace.cs`:

```csharp
using System.Text;

namespace Depenk.Tests.TestUtil;

/// <summary>
/// N repos; each has an Api project (C controllers × E endpoints) and a Client project (C clients × E methods,
/// hand-written HttpClient style), plus a consumer class calling the clients of the next 3 repos.
/// </summary>
public static class SyntheticWorkspace
{
    public static TempWorkspace Create(int repos = 50, int controllers = 10, int endpointsPerController = 10)
    {
        var ws = new TempWorkspace();
        for (var r = 0; r < repos; r++)
        {
            var name = $"svc{r:D2}";
            ws.Repo(name);
            var deps = Enumerable.Range(1, 3).Select(k => $"svc{(r + k) % repos:D2}").ToList();

            ws.File($"{name}/src/{name}.Client/{name}.Client.csproj",
                $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><PackageId>{name}.Client</PackageId><Version>1.0.0</Version><IsPackable>true</IsPackable></PropertyGroup></Project>");
            ws.File($"{name}/src/{name}.Api/{name}.Api.csproj",
                $"<Project Sdk=\"Microsoft.NET.Sdk.Web\"><ItemGroup>{string.Concat(deps.Select(d => $"<PackageReference Include=\"{d}.Client\" Version=\"1.0.0\" />"))}<ProjectReference Include=\"..\\{name}.Client\\{name}.Client.csproj\" /></ItemGroup></Project>");

            for (var c = 0; c < controllers; c++)
            {
                var ctl = new StringBuilder($"namespace {name}.Api;\n[ApiController]\n[Route(\"api/r{c}\")]\npublic class R{c}Controller : ControllerBase\n{{\n");
                var cli = new StringBuilder($"namespace {name}.Client;\npublic record R{c}Dto(Guid Id, string Name, List<R{c}LineDto> Lines);\npublic record R{c}LineDto(int Qty);\npublic class R{c}Client(HttpClient http)\n{{\n");
                for (var e = 0; e < endpointsPerController; e++)
                {
                    ctl.Append($"    [HttpGet(\"e{e}/{{id}}\")] public Task<ActionResult<R{c}Dto>> E{e}(Guid id) => null!;\n");
                    cli.Append($"    public Task<R{c}Dto?> E{e}Async(Guid id) => http.GetFromJsonAsync<R{c}Dto>($\"api/r{c}/e{e}/{{id}}\");\n");
                }
                ws.File($"{name}/src/{name}.Api/Controllers/R{c}Controller.cs", ctl.Append("}\n").ToString());
                ws.File($"{name}/src/{name}.Client/R{c}Client.cs", cli.Append("}\n").ToString());
            }

            var consumer = new StringBuilder($"namespace {name}.Api;\npublic class Consumer({string.Join(", ", deps.Select((d, i) => $"{d}.Client.R0Client c{i}"))})\n{{\n    public async Task Run()\n    {{\n");
            for (var i = 0; i < deps.Count; i++) consumer.Append($"        await c{i}.E0Async(Guid.Empty);\n");
            ws.File($"{name}/src/{name}.Api/Consumer.cs", consumer.Append("    }\n}\n").ToString());
        }
        return ws;
    }
}
```

- [ ] **Step 2: Write failing tests**

`tests/Depenk.Tests/IncrementalScanTests.cs`:

```csharp
using Depenk.Analysis;
using Depenk.Core;

namespace Depenk.Tests;

public class IncrementalScanTests
{
    [Fact]
    public void Rescan_ReparsesOnlyChangedFiles_AndPicksUpChanges()
    {
        using var ws = FixtureScanTests.CopyFixture();
        var cache = new ParseCache();
        var orchestrator = new ScanOrchestrator(cache);

        var first = orchestrator.Scan(ws.Root);
        var parsesAfterFirst = cache.ParseCount;
        Assert.DoesNotContain(first.Endpoints, e => e.Route == "/api/orders/{id}/cancel");

        ws.File("orders/src/Orders.Api/CancelController.cs", """
            using Microsoft.AspNetCore.Mvc;
            namespace Acme.Orders.Api;
            [Route("api/orders")]
            public class CancelController : ControllerBase { [HttpPost("{id}/cancel")] public Task Cancel(Guid id) => Task.CompletedTask; }
            """);
        var second = orchestrator.Scan(ws.Root);

        Assert.Equal(1, cache.ParseCount - parsesAfterFirst);
        Assert.Contains(second.Endpoints, e => e.Route == "/api/orders/{id}/cancel");
    }

    [Fact]
    public void Manifest_DetectsChanges()
    {
        using var ws = FixtureScanTests.CopyFixture();
        Assert.False(WorkspaceManifest.IsUpToDate(ws.Root));

        GraphJson.Save(new ScanOrchestrator().Scan(ws.Root), ScanOrchestrator.GraphPath(ws.Root));
        WorkspaceManifest.Save(ws.Root, WorkspaceManifest.Compute(ws.Root));
        Assert.True(WorkspaceManifest.IsUpToDate(ws.Root));

        ws.File("orders/src/Orders.Client/Models.cs", "namespace Acme.Orders.Client; public class Changed {}");
        Assert.False(WorkspaceManifest.IsUpToDate(ws.Root));
    }

    [Fact]
    public void Manifest_KeysArePortable()
    {
        using var ws = FixtureScanTests.CopyFixture();
        var m = WorkspaceManifest.Compute(ws.Root);
        Assert.Contains("repo:orders", m.Keys);
        Assert.Contains("orders/src/Orders.Api/OrdersController.cs", m.Keys);
        Assert.Contains("depenk.yml", m.Keys);
        Assert.All(m.Keys, k => Assert.DoesNotContain('\\', k));
    }
}
```

`tests/Depenk.Tests/PerfSmokeTests.cs`:

```csharp
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
        Assert.True(full < TimeSpan.FromSeconds(60), $"full scan took {full}");
        Assert.True(incremental < TimeSpan.FromSeconds(2), $"incremental rescan took {incremental}");
    }
}
```

- [ ] **Step 3: Run to verify failure**

Run: `dotnet test tests/Depenk.Tests --filter "IncrementalScanTests|PerfSmokeTests"`
Expected: build FAILS: `The type or namespace name 'ParseCache' could not be found`.

- [ ] **Step 4: Implement ParseCache and the SourceSet changes**

`src/Depenk.Analysis/ParseCache.cs`:

```csharp
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Depenk.Analysis;

public sealed class ParseCache
{
    private readonly ConcurrentDictionary<string, (string Hash, SyntaxTree Tree)> _trees = new(StringComparer.OrdinalIgnoreCase);
    private int _parseCount;

    public int ParseCount => _parseCount;

    public (SyntaxTree Tree, string Hash) GetOrParse(string absolutePath, string relativePath)
    {
        var text = File.ReadAllText(absolutePath);
        var hash = HashText(text);
        if (_trees.TryGetValue(absolutePath, out var hit) && hit.Hash == hash) return (hit.Tree, hash);
        var tree = CSharpSyntaxTree.ParseText(text, path: relativePath);
        Interlocked.Increment(ref _parseCount);
        _trees[absolutePath] = (hash, tree);
        return (tree, hash);
    }

    public static string HashText(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}
```

In `src/Depenk.Analysis/SourceSet.cs`, change the record and `Load`:

```csharp
public sealed record SourceDoc(string RelativePath, SyntaxTree Tree, string Hash = "");
```

```csharp
public static SourceSet Load(string workspace, string repo, string projectId, string projectName, string projectDir,
    Action<string, Exception> onError, ParseCache? cache = null)
{
    cache ??= new ParseCache();
    var docs = new List<SourceDoc>();
    foreach (var file in PathUtil.EnumerateFiles(projectDir, "*.cs").Order(StringComparer.Ordinal))
    {
        var rel = PathUtil.Rel(workspace, file);
        try
        {
            var (tree, hash) = cache.GetOrParse(file, rel);
            docs.Add(new SourceDoc(rel, tree, hash));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { onError(rel, ex); }
    }
    return new SourceSet(repo, projectId, projectName, docs);
}
```

Remove the now-unused `using Microsoft.CodeAnalysis.CSharp;` from `SourceSet.cs` if nothing else in the file needs it.

- [ ] **Step 5: Update ScanOrchestrator**

In `src/Depenk.Analysis/ScanOrchestrator.cs`, add a constructor, a per-project analysis cache, and parallel source loading.

1. Add these members at the top of the class:

```csharp
private readonly ParseCache _cache;
private readonly Dictionary<string, (string Key, List<EndpointNode> Endpoints, ClientScanResult Clients)> _analysis = [];

public ScanOrchestrator(ParseCache? cache = null) => _cache = cache ?? new ParseCache();
```

2. Replace the `sources` line in `Scan` with a parallel load. Each project's diagnostics are collected into its own list and merged in project order, so the output stays deterministic:

```csharp
var loaded = new (SourceSet Set, List<Diagnostic> Diags)[projects.Count];
Parallel.For(0, projects.Count, i =>
{
    var local = new DepGraph();
    loaded[i] = (LoadSources(workspace, projects[i], projects, local, _cache), local.Diagnostics);
});
var sources = new Dictionary<string, SourceSet>();
for (var i = 0; i < projects.Count; i++)
{
    sources[projects[i].Id] = loaded[i].Set;
    g.Diagnostics.AddRange(loaded[i].Diags);
}
var configHash = File.Exists(Path.Combine(workspace, ConfigLoader.FileName))
    ? ParseCache.HashText(File.ReadAllText(Path.Combine(workspace, ConfigLoader.FileName))) : "";
```

3. Give `LoadSources` a `ParseCache cache` parameter and pass it through: `SourceSet.Load(..., cache)`.

4. Replace the per-project endpoint/client loop with a cached version:

```csharp
foreach (var p in projects)
{
    var src = sources[p.Id];
    var key = configHash + "|" + string.Join(",", src.Docs.Select(d => d.RelativePath + ":" + d.Hash));
    if (!_analysis.TryGetValue(p.Id, out var cached) || cached.Key != key)
    {
        var test = p.File.IsTestProject;
        cached = (key,
            test ? [] : endpointFinders.SelectMany(f => f.Find(src)).ToList(),
            test ? new ClientScanResult([], false) : clientFinder.Find(src));
        _analysis[p.Id] = cached;
    }
    endpoints[p.Id] = cached.Endpoints;
    clients[p.Id] = cached.Clients;
    kinds[p.Id] = ProjectClassifier.Classify(p.File, new ProjectSignals(cached.Endpoints.Count > 0, cached.Clients.AnyHit), config);
}
```

5. `g.Endpoints.AddRange(endpoints[p.Id])` now adds the cached `EndpointNode` records. They're immutable, and `DedupeEndpointIds` replaces list entries with `with` copies rather than mutating them, so sharing them across scans is safe.

- [ ] **Step 6: Implement WorkspaceManifest**

`src/Depenk.Analysis/WorkspaceManifest.cs`:

```csharp
using System.Text.Json;
using Depenk.Scanning;
using Depenk.Scanning.Config;

namespace Depenk.Analysis;

public static class WorkspaceManifest
{
    private static readonly string[] Patterns = ["*.cs", "*.csproj", "*.props"];

    public static string ManifestPath(string workspace) => Path.Combine(workspace, ".depenk", "manifest.json");

    public static SortedDictionary<string, string> Compute(string workspace)
    {
        workspace = Path.GetFullPath(workspace);
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var configPath = Path.Combine(workspace, ConfigLoader.FileName);
        if (File.Exists(configPath)) result[ConfigLoader.FileName] = ParseCache.HashText(File.ReadAllText(configPath));

        DepenkConfig config;
        try { config = ConfigLoader.Load(workspace); }
        catch (ConfigException) { return result; } // an invalid config is never "up to date"

        foreach (var repo in RepoDiscovery.Discover(workspace, config))
        {
            result[$"repo:{repo.Name}"] = repo.HeadSha ?? "";
            foreach (var pattern in Patterns)
            foreach (var file in PathUtil.EnumerateFiles(repo.AbsolutePath, pattern))
                result[PathUtil.Rel(workspace, file)] = ParseCache.HashText(File.ReadAllText(file));
        }
        return result;
    }

    public static void Save(string workspace, SortedDictionary<string, string> manifest)
    {
        var path = ManifestPath(workspace);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static bool IsUpToDate(string workspace)
    {
        var path = ManifestPath(workspace);
        if (!File.Exists(path) || !File.Exists(ScanOrchestrator.GraphPath(workspace))) return false;
        var saved = JsonSerializer.Deserialize<SortedDictionary<string, string>>(File.ReadAllText(path));
        return saved is not null && saved.SequenceEqual(Compute(workspace));
    }
}
```

- [ ] **Step 7: Update the CLI**

In `src/depenk/Program.cs`:

1. Add the option:

```csharp
var forceOption = new Option<bool>("--force", "Rescan even if nothing changed since the last scan");
```

2. Add it to the command: `var scan = new Command("scan", "…") { workspaceOption, forceOption };`

3. Inside the handler, right after `ws` is computed, add:

```csharp
if (!ctx.ParseResult.GetValueForOption(forceOption) && WorkspaceManifest.IsUpToDate(ws))
{
    Console.WriteLine($"Graph is up to date ({Path.GetRelativePath(ws, ScanOrchestrator.GraphPath(ws))})");
    ctx.ExitCode = 0;
    return;
}
```

4. After `GraphJson.Save(graph, path);`, add:

```csharp
WorkspaceManifest.Save(ws, WorkspaceManifest.Compute(ws));
```

- [ ] **Step 8: Run tests**

Run: `dotnet test tests/Depenk.Tests --filter "IncrementalScanTests|PerfSmokeTests"`
Expected: PASS (4 tests). The perf test prints `full=…s incremental=…s`; record those numbers in the commit message.

If the incremental budget is missed, profile before loosening anything. The usual cost is `ModelExtractor` rebuilding its `TypeIndex`, or `CallSiteFinder` walking every tree. Neither is cached yet. Cache `TypeIndex` by the same per-project key, only if it's needed to meet the 2 s budget.

Then run the whole suite: `dotnet test tests/Depenk.Tests`. Expected: all pass, and the `FixtureScanTests.Snapshot` output is unchanged.

- [ ] **Step 9: Commit**

```bash
git add -A
git commit -m "feat: incremental rescans via parse cache, workspace manifest and perf smoke test"
```

---

## Spec Coverage (Plan 1 scope)

| Spec section | Task(s) |
|---|---|
| §2 Workspace: local clones, include/exclude, explicit paths | 2 |
| §2 `depenk.yml` sections (repos, projects, packages, httpWrappers, routes) | 2 (parse), 4 (kindOverrides, ignore, producers), 7 (httpWrappers, prefixes) |
| §2 Cache `graph.json` + per-file hashes | 12 (graph), 13 (manifest + parse cache) |
| §3 Architecture: Core / Scanning / Analysis / CLI, no build or restore | 1, 12 |
| §3.1 step 1: discover repos and projects, git HEAD | 2, 4 |
| §3.1 step 2: parse csproj, CPM, `Directory.Build.props`, `$(Prop)` | 3 |
| §3.1 step 3: classify projects, overrides | 4, 12 (signals) |
| §3.1 step 4: package → producer, external, ambiguous | 4 |
| §3.1 step 5: controllers and minimal APIs (incl. `MapGroup`) | 6 |
| §3.1 step 6: Refit / NSwag / Kiota / configured wrapper / generic heuristic; interface mapping | 7 |
| §3.1 step 7: normalize and link same-repo; ambiguous → low; unresolved diagnostic | 5, 8 |
| §3.1 step 8: model extraction, unwrapping, resolution order, opaque, depth limit | 9 |
| §3.1 step 9: call sites on client-typed receivers | 10 |
| §3.1 step 10: derived `dependsOn` (+ counts), diagnostics, write graph | 4, 11, 12 |
| §3.1 incremental rescan | 13 |
| §4 graph model, IDs, confidence, diagnostic kinds | 1, 11 |
| §9 partial results, visible ambiguity, config errors with line, read-only | 3, 4, 8, 2, 12 |
| §10 fixture workspace, golden snapshot, unit tests, perf smoke test | 12, 2–11, 13 |

**Deferred to later plans, on purpose:**
- **Plan 2** (Query + MCP + skill + distribution):
  - `Depenk.Query`, the MCP server and every tool
  - publishing the JSON Schema for `depenk.yml`
  - `depenk query`, the skill and plugin, and `dnx` packaging (which needs the .NET 10 SDK; this machine has 9.0.3xx)
  - the query conformance suite
  - the MCP startup staleness check, which will reuse `WorkspaceManifest.IsUpToDate`
- **Plan 3** (Frontend): `web/`, `export`, `serve`, `open_diagram`, localhost binding, `--include-source`, and the Playwright screenshot tests.
- **Plan 4** (History): snapshots, `compare_snapshots`, `check_contract_changes`, and `RepoNode.Dirty` via `git status --porcelain`.

## Execution Notes

- Run the fast suite with `dotnet test --filter "Category!=Perf"` during development. Run the full suite, including `PerfSmokeTests`, before the final review.
- Every task leaves `dotnet build Depenk.sln` at **0 warnings** (`TreatWarningsAsErrors`).
- If a task's expected test count differs, find out why before continuing. The counts are part of the spec.
