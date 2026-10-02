# depenk Plan 3: Interactive Diagram Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the dependency graph visible as an interactive map. It can be opened four ways:
- as one offline HTML file (`depenk export`)
- live on localhost (`depenk serve`)
- by an agent through MCP (`export_diagram`)
- by an agent through MCP (`open_diagram`)

**Architecture:**
- **`web/` app:** a React + TypeScript app built by Vite into one self-contained `index.html`. It holds a TypeScript port of the query layer (kept in lockstep with C# by the shared conformance cases). elkjs computes layout in a Web Worker, and React Flow draws the canvas.
- **`Depenk.Server`** (new) contains:
  - the embedded bundle, plus `DiagramExporter`, which inlines the graph into the bundle
  - a loopback-only Kestrel host (`/`, `/api/graph`, `/api/events` SSE) with a debounced workspace watcher
  - `GraphStore`, moved here from `Depenk.Mcp`
- **CLI:** adds the `export` and `serve` commands.
- **`Depenk.Mcp`:** adds the `export_diagram` and `open_diagram` tools.

**Tech Stack:**
- **.NET:** .NET 9 and the ASP.NET Core shared framework (`FrameworkReference Microsoft.AspNetCore.App`). Existing packages are unchanged.
- **Runtime npm packages:** react 19.3.0, @xyflow/react 12.12.0, elkjs 0.12.0, html-to-image 1.11.13, @fontsource IBM Plex 5.3.0.
- **Build and test npm packages:** Vite 8.3.2 with vite-plugin-singlefile 2.3.3, TypeScript 5.9.3, Vitest 5.0.3 with jsdom 30.1.1, Testing Library, Playwright 1.63.0.
- **Node:** 22, needed for development and CI only.

**Spec:** `docs/superpowers/specs/2026-10-02-depenk-3-diagram-design.md`. This plan covers every section of it.

**Planned departures from the spec** (each recorded where it applies):
- **Export placeholders:** the export marker is the exact empty element `<script type="application/json" id="depenk-graph"></script>` (plus `<meta name="depenk-focus" content="">`), not an HTML comment. Vite keeps elements verbatim, and the app detects the mode by the element's content.
- **Performance graph:** the perf test generates its synthetic 50-repo graph in TypeScript (`web/test/util/synthetic.ts`), mirroring `SyntheticWorkspace`. This saves the browser test from a .NET round-trip.
- **Uncapped impact for the UI:** the UI computes impact uncapped (`computeImpact`), so the map can mark every affected node. `impactOfChange` applies the MCP cap of 500 and is what the conformance cases test.

## Global Constraints

- **.NET:** all projects target `net9.0`, and `TreatWarningsAsErrors` applies (from `Directory.Build.props`). No new NuGet packages: ASP.NET Core comes from `<FrameworkReference Include="Microsoft.AspNetCore.App" />`.
- **Node:** Node 22 is used only for development and CI. npm versions are exact (no `^`/`~`). `web/package-lock.json` is committed, and `web/dist/` stays git-ignored (it already is).
- **MCP stdout:** stdout is the MCP protocol channel in `depenk mcp`. Code reachable from there, including `DiagramServer` started by `open_diagram`, must never write to stdout, and Kestrel logging stays off.
- **Server:**
  - binds `127.0.0.1` only
  - rejects any request whose `Host` is not `127.0.0.1:<port>` or `localhost:<port>` with 403
  - has no write endpoints
- **Exports:** contain paths and line numbers only, never source code. The graph is inlined with `<` → `<`, `>` → `>`, `&` → `&`, U+2028 → ` `, U+2029 → ` `.
- **Graph format:** the graph must have `schemaVersion` 1. The UI refuses any other version with an "upgrade depenk" message.
- **Deep links:** `#/<id>` and `#/impact/<target>`. The id is encoded with `encodeURIComponent` and then `%3A`→`:` and `%2F`→`/` are restored. C# `DeepLink.Encode` does the same with `Uri.EscapeDataString`. Both sides turn `ep:orders:GET:/api/orders/{id}` into `ep:orders:GET:/api/orders/%7Bid%7D`.
- **Visuals** (spec §3):
  - the ServiceMap tokens, with light and dark themes
  - IBM Plex Sans / Sans Condensed / Mono, bundled with no CDN
  - an 8-colour repo palette chosen by FNV-1a hash with linear probing
- **MCP tools:** there are 14. Every tool keeps the `{summary, stale, truncated, data}` envelope and the structured errors from Plan 2.
- **Version:** `0.3.0` in `Directory.Build.props` and `.claude-plugin/plugin.json`.
- **Commits:** work directly on `master` (user preference). Every commit message ends with a blank line then `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- **UI screenshots:** for every task that changes visible UI (Tasks 7–11), take a **before and after Puppeteer screenshot** of the built bundle and show both to the user. This is the user's global instruction.

## Review Focus

1. **A huge workspace at Project or Endpoint depth** (thousands of projects): the app must fall back to a coarser view with a notice, never freeze or draw thousands of boxes. Tested in Task 5 (`buildView` fallbacks) and Task 15 (perf budget).
2. **A model, route or repo name containing `</script>`, `<`, `&` or U+2028** in an export: it must stay inert JSON data, and the page must still load. Tested in Task 11 (TypeScript escape) and Task 12 (C# escape, shared test vector).
3. **A browser page on another site reaching `depenk serve` through DNS rebinding** (Host header `evil.example:<port>`): the response must be 403 with no graph data. Tested in Task 13.
4. **A deep link, palette entry or `--focus` naming a node that doesn't exist** (typo, or renamed since the last scan): the result must be "Not found" with suggestions (UI), or `not_found` / exit code 2 (CLI and MCP). It must never be a blank map. Tested in Tasks 7, 10 and 12.
5. **A live rescan that removes the selected node, the drill-in focus or the impact target:** the app keeps everything that still exists, drops the rest and doesn't crash. Tested in Task 11 (`reconcile`).

---

## File Structure

```
src/
├─ Depenk.Server/                      # new: references Query + Analysis + ASP.NET Core shared framework
│  ├─ Depenk.Server.csproj             # embeds web/dist/index.html; BuildWeb target runs npm
│  ├─ GraphStore.cs                    # moved from Depenk.Mcp (namespace Depenk.Server), unchanged behaviour
│  ├─ DiagramBundle.cs                 # embedded bundle + placeholder constants
│  ├─ DiagramExporter.cs               # EscapeJson, Render, Export
│  ├─ DeepLink.cs                      # deep-link id encoding shared with the UI
│  ├─ WorkspaceWatcher.cs              # debounced FileSystemWatcher with relevance filter
│  └─ DiagramServer.cs                 # Kestrel: /, /api/graph (ETag), /api/events (SSE), Host guard
├─ Depenk.Mcp/
│  ├─ DiagramHost.cs                   # one DiagramServer per MCP session (open_diagram)
│  ├─ DepenkTools.cs                   # + export_diagram, open_diagram
│  └─ DepenkMcpServer.cs               # registers DiagramHost; instructions mention the diagram
└─ depenk/Program.cs                   # + export, serve
web/
├─ package.json, package-lock.json, tsconfig.json, vite.config.ts, vitest.config.ts, playwright.config.ts, index.html
├─ scripts/check-bundle.mjs            # build guard: one file, placeholders present, no external URLs
├─ src/
│  ├─ main.tsx                         # boot: export mode (inline graph) or serve mode (live)
│  ├─ graph/
│  │  ├─ types.ts                      # graph.json types
│  │  ├─ load.ts                       # inline read, parse, schema check, dangling edges → diagnostics
│  │  ├─ fuzzy.ts                      # Levenshtein (same as C# Fuzzy)
│  │  ├─ graphIndex.ts                 # GraphIndex, QueryError, resolvers, suggest
│  │  ├─ queries.ts                    # neighbors, trace, relax, computeImpact, impactOfChange, endpointsUsingModel
│  │  ├─ colors.ts                     # FNV-1a repo palette assignment
│  │  ├─ views.ts                      # repo / project / endpoint view models, filters, scale fallback
│  │  ├─ overview.ts                   # overview numbers + needs-attention ordering
│  │  ├─ browse.ts                     # sidebar browse lists, diagnostic groups
│  │  ├─ details.ts                    # inspector data per kind, model tree
│  │  └─ impactMarks.ts                # impact result → map marks for the current view
│  ├─ layout/
│  │  ├─ elkInput.ts                   # view → ELK graph, ELK result → positions
│  │  ├─ engine.ts                     # LayoutEngine, worker engine, layoutView
│  │  └─ layout.worker.ts              # elkjs in a worker
│  ├─ map/
│  │  ├─ geometry.ts                   # edge curve + stroke width
│  │  ├─ neighbours.ts                 # highlight sets, node/edge classes, keyboard moves, ImpactMarks type
│  │  ├─ nodes.tsx, edges.tsx          # React Flow node/edge components
│  │  ├─ MapCanvas.tsx                 # React Flow canvas
│  │  └─ MapStage.tsx                  # view + layout + saved positions + canvas
│  ├─ panels/
│  │  ├─ types.ts, Segmented.tsx, TopBar.tsx, Sidebar.tsx, Overview.tsx, NotFound.tsx
│  │  ├─ Inspector.tsx, ModelTreeView.tsx, ImpactPanel.tsx, CommandPalette.tsx, ExportMenu.tsx
│  ├─ app/
│  │  ├─ App.tsx, state.ts, route.ts, persist.ts, theme.ts, live.ts, exportHtml.ts
│  └─ styles/fonts.ts, tokens.css, app.css
├─ test/                               # Vitest (jsdom)
│  ├─ setup.ts, util/fixture.ts, util/inThreadEngine.ts, util/synthetic.ts, *.test.ts(x)
└─ e2e/                                # Playwright: global-setup.ts, screens.spec.ts, perf.spec.ts
tests/
├─ query-cases/impact-*.json           # 4 new conformance cases (op "impact")
└─ Depenk.Tests/
   ├─ Server/GraphStoreTests.cs        # moved
   ├─ Server/DiagramExporterTests.cs, DiagramServerTests.cs, WorkspaceWatcherTests.cs, CliDiagramTests.cs
   └─ Mcp/DiagramToolsTests.cs
.github/workflows/ci.yml               # web (Vitest, build, Playwright) + dotnet legs, windows-latest
```

---

### Task 1: `Depenk.Server` project; move `GraphStore` into it

**Files:**
- Create: `src/Depenk.Server/Depenk.Server.csproj`
- Move: `src/Depenk.Mcp/GraphStore.cs` → `src/Depenk.Server/GraphStore.cs` (namespace `Depenk.Server`)
- Move: `tests/Depenk.Tests/Mcp/GraphStoreTests.cs` → `tests/Depenk.Tests/Server/GraphStoreTests.cs`
- Modify: `src/Depenk.Mcp/Depenk.Mcp.csproj`, `tests/Depenk.Tests/Depenk.Tests.csproj`, `Depenk.sln`, plus every `.cs` file that uses `GraphStore`/`GraphSnapshot` (add `using Depenk.Server;`)

**Interfaces:**
- Produces: `Depenk.Server.GraphStore` and `Depenk.Server.GraphSnapshot`, with the same members as before: `Current()`, `Rescan()`, `StartBackgroundRefresh()`, `Workspace`, `ScanCount`, and the record `GraphSnapshot(DepGraph Graph, GraphIndex Index, QueryService Query, bool Stale)`.

- [ ] **Step 1: Move the test and add the failing assertion**

```bash
mkdir -p tests/Depenk.Tests/Server
git mv tests/Depenk.Tests/Mcp/GraphStoreTests.cs tests/Depenk.Tests/Server/GraphStoreTests.cs
```

In `tests/Depenk.Tests/Server/GraphStoreTests.cs`:
- replace `using Depenk.Mcp;` with `using Depenk.Server;`
- replace `namespace Depenk.Tests.Mcp;` with `namespace Depenk.Tests.Server;`
- add this test at the top of the class:

```csharp
    [Fact]
    public void GraphStore_LivesInDepenkServer() =>
        Assert.Equal("Depenk.Server", typeof(GraphStore).Assembly.GetName().Name);
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet build tests/Depenk.Tests`
Expected: FAIL with `CS0246`/`CS0234`: the namespace `Depenk.Server` does not exist.

- [ ] **Step 3: Create the project and move the class**

`src/Depenk.Server/Depenk.Server.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\Depenk.Query\Depenk.Query.csproj" />
    <ProjectReference Include="..\Depenk.Analysis\Depenk.Analysis.csproj" />
  </ItemGroup>
</Project>
```

```bash
git mv src/Depenk.Mcp/GraphStore.cs src/Depenk.Server/GraphStore.cs
sed -i 's/^namespace Depenk.Mcp;$/namespace Depenk.Server;/' src/Depenk.Server/GraphStore.cs
dotnet sln Depenk.sln add src/Depenk.Server/Depenk.Server.csproj --solution-folder src
```

Add to `src/Depenk.Mcp/Depenk.Mcp.csproj`, inside the first `<ItemGroup>`:

```xml
    <ProjectReference Include="..\Depenk.Server\Depenk.Server.csproj" />
```

Add to `tests/Depenk.Tests/Depenk.Tests.csproj`, next to the other `src` project references:

```xml
    <ProjectReference Include="..\..\src\Depenk.Server\Depenk.Server.csproj" />
```

Then add `using Depenk.Server;` to every remaining user of the moved types:

```bash
grep -rlE "\bGraph(Store|Snapshot)\b" src tests --include=*.cs | grep -v "src/Depenk.Server/" \
  | xargs -I{} sed -i '0,/^namespace /s//using Depenk.Server;\n\nnamespace /' {}
```

Then fix the placement by hand where a file had no blank line before its namespace. `dotnet build` points to any such file. Expected files: `src/Depenk.Mcp/DepenkTools.cs`, `DepenkResources.cs`, `DepenkMcpServer.cs`, `QueryRunner.cs`, `src/depenk/Program.cs` (a top-level-statements file: put `using Depenk.Server;` with the other usings at the top) and `tests/Depenk.Tests/TestUtil/McpHarness.cs`, plus any test that names `GraphStore`.

- [ ] **Step 4: Run the full suite**

Run: `dotnet build Depenk.sln` (expect 0 warnings), then `dotnet test tests/Depenk.Tests --filter "Category!=Perf"`
Expected: PASS, including `GraphStore_LivesInDepenkServer`.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "refactor: Depenk.Server project; GraphStore moves there so the server and MCP can share it

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: Impact conformance cases (shared contract for the TypeScript port)

**Files:**
- Modify: `tests/Depenk.Tests/QueryConformanceTests.cs`, `tests/query-cases/README.md`
- Create: `tests/query-cases/impact-field-orderdto-lines.json`, `impact-package-orders-client.json`, `impact-endpoint-post-invoices.json`, `impact-model-money.json`

**Interfaces:**
- Consumes: `QueryService.ImpactOfChange(string target, int? limit)` → `ImpactResult(Target, TargetKind, Field, Repos, Projects, Endpoints, ClientMethods, CallSites, Models, Totals, Truncated)`.
- Produces: the case op `"impact"`, with args `{ "target": string }`. The expected output is `["target:<id>[#<field>]", "<id>@<confidence>", …]`. Groups come in the order repos, projects, endpoints, clientMethods, callSites, models. Within a group, ids are in ordinal order, and confidence is lower-case. Task 4's TypeScript runner relies on this exact format.

- [ ] **Step 1: Write the case files (failing: the runner does not know `impact`)**

`tests/query-cases/impact-field-orderdto-lines.json`:

```json
{
  "name": "a field resolves to its model; consumers reached through Orders.Client are affected at medium",
  "graph": "../Depenk.Tests/Snapshots/FixtureScanTests.Snapshot.verified.json",
  "op": "impact",
  "args": { "target": "OrderDto.Lines" },
  "expected": [
    "target:model:Orders.Client:Acme.Orders.Client.OrderDto#Lines",
    "repo:billing@medium", "repo:gateway@medium", "repo:orders@high",
    "proj:billing/Billing.Api@medium", "proj:gateway/Gateway.Api@medium", "proj:orders/Orders.Api@high", "proj:orders/Orders.Client@high",
    "ep:orders:GET:/api/orders@high", "ep:orders:GET:/api/orders/{id}@high", "ep:orders:POST:/api/orders@high",
    "cm:Orders.Client:IOrdersClient.CreateAsync@high", "cm:Orders.Client:IOrdersClient.GetOrderAsync@high",
    "cm:Orders.Client:IOrdersClient.ListAsync@high", "cm:Orders.Client:OrdersClient.CreateAsync@high",
    "cm:Orders.Client:OrdersClient.GetOrderAsync@high", "cm:Orders.Client:OrdersClient.ListAsync@high",
    "cs:billing/Billing.Api:InvoiceBuilder.BuildAsync:10@medium", "cs:gateway/Gateway.Api:OrdersProxy.Create:10@medium",
    "cs:gateway/Gateway.Api:OrdersProxy.List:9@medium"
  ]
}
```

`tests/query-cases/impact-package-orders-client.json`:

```json
{
  "name": "a package affects the projects that reference it and their repos, at certain",
  "graph": "../Depenk.Tests/Snapshots/FixtureScanTests.Snapshot.verified.json",
  "op": "impact",
  "args": { "target": "Orders.Client" },
  "expected": [
    "target:pkg:Orders.Client",
    "repo:billing@certain", "repo:gateway@certain",
    "proj:billing/Billing.Api@certain", "proj:gateway/Gateway.Api@certain"
  ]
}
```

`tests/query-cases/impact-endpoint-post-invoices.json`:

```json
{
  "name": "an endpoint reached by 'VERB /route' affects its client method and the orders call site",
  "graph": "../Depenk.Tests/Snapshots/FixtureScanTests.Snapshot.verified.json",
  "op": "impact",
  "args": { "target": "POST /api/invoices" },
  "expected": [
    "target:ep:billing:POST:/api/invoices",
    "repo:billing@high", "repo:orders@medium",
    "proj:billing/Billing.Client@high", "proj:orders/Orders.Api@medium",
    "cm:Billing.Client:IBillingApi.CreateInvoice@high",
    "cs:orders/Orders.Api:InvoiceNotifier.NotifyAsync:7@medium"
  ]
}
```

`tests/query-cases/impact-model-money.json`:

```json
{
  "name": "a nested model affects every model that contains it and everything that carries those",
  "graph": "../Depenk.Tests/Snapshots/FixtureScanTests.Snapshot.verified.json",
  "op": "impact",
  "args": { "target": "Money" },
  "expected": [
    "target:model:Shared.Kernel:Acme.Shared.Money",
    "repo:billing@medium", "repo:gateway@medium", "repo:orders@high",
    "proj:billing/Billing.Api@medium", "proj:gateway/Gateway.Api@medium", "proj:orders/Orders.Api@high", "proj:orders/Orders.Client@high",
    "ep:orders:GET:/api/orders@high", "ep:orders:GET:/api/orders/{id}@high", "ep:orders:POST:/api/orders@high",
    "cm:Orders.Client:IOrdersClient.CreateAsync@high", "cm:Orders.Client:IOrdersClient.GetOrderAsync@high",
    "cm:Orders.Client:IOrdersClient.ListAsync@high", "cm:Orders.Client:OrdersClient.CreateAsync@high",
    "cm:Orders.Client:OrdersClient.GetOrderAsync@high", "cm:Orders.Client:OrdersClient.ListAsync@high",
    "cs:billing/Billing.Api:InvoiceBuilder.BuildAsync:10@medium", "cs:gateway/Gateway.Api:OrdersProxy.Create:10@medium",
    "cs:gateway/Gateway.Api:OrdersProxy.List:9@medium",
    "model:Orders.Client:Acme.Orders.Client.CreateOrderRequest@high", "model:Orders.Client:Acme.Orders.Client.OrderDto@high",
    "model:Orders.Client:Acme.Orders.Client.OrderLineDto@high"
  ]
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Depenk.Tests --filter "FullyQualifiedName~QueryConformanceTests"`
Expected: 4 FAIL with `InvalidOperationException: unknown op impact`.

- [ ] **Step 3: Teach the C# runner the op**

In `tests/Depenk.Tests/QueryConformanceTests.cs`, add this arm to the `switch`, before the `var op =>` arm:

```csharp
            "impact" => Impact(new QueryService(index).ImpactOfChange(Arg("target"), QueryService.MaxLimit)),
```

and add this method to the class:

```csharp
    private static IEnumerable<string> Impact(ImpactResult r) =>
        new[] { $"target:{r.Target}{(r.Field is null ? "" : "#" + r.Field)}" }
            .Concat(new[] { r.Repos, r.Projects, r.Endpoints, r.ClientMethods, r.CallSites, r.Models }
                .SelectMany(list => list.Select(a => $"{a.Id}@{a.Confidence.ToString().ToLowerInvariant()}")));
```

Append to `tests/query-cases/README.md`:

```markdown

### impact
- **Args:** `target`, which is anything `impact_of_change` accepts: a node ID, repo name, package ID, `"VERB /route"`,
  model name, or `"Model.Field"`
- **Returns:**
  - first, `"target:{resolvedId}"`, with `"#{field}"` appended for a field target
  - then `"{id}@{confidence}"` for every affected node, grouped in this order: repos, projects, endpoints,
    clientMethods, callSites, models
  - IDs are sorted ordinally within each group; confidence is lower-case (`low|medium|high|certain`)
  - limit 500
- **Resolution:** like `ResolveAny` (exact ID, repo name, package ID, `"VERB /route"`, model). If that fails with
  not-found and the target contains `.`, the part before the last `.` must resolve as a model and the rest must name
  one of its fields; the affected set is then the model's
- **Semantics:**
  - Start at the target and follow **dependents** (up) over every edge kind, skipping far ends that are not known nodes.
  - A node's confidence is the **best path confidence**: the maximum, over all paths, of the minimum confidence along
    the path.
  - The target itself is excluded.
  - Every affected node also marks its project (`projectId` of endpoints, client methods, call sites and models) and
    `repo:{repo}`, with the same confidence, keeping the maximum. The exception is when that project or repo is the
    target itself.
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/Depenk.Tests --filter "FullyQualifiedName~QueryConformanceTests"`
Expected: PASS (15 case files).

- [ ] **Step 5: Commit**

```bash
git add tests/query-cases tests/Depenk.Tests/QueryConformanceTests.cs
git commit -m "test(query): impact conformance cases for the TypeScript port

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: `web/` scaffold: single-file build, graph types and loading

**Files:**
- Create: `web/package.json`, `web/tsconfig.json`, `web/vite.config.ts`, `web/vitest.config.ts`, `web/index.html`, `web/scripts/check-bundle.mjs`, `web/src/main.tsx`, `web/src/graph/types.ts`, `web/src/graph/load.ts`, `web/test/setup.ts`, `web/test/util/fixture.ts`, `web/test/load.test.ts`
- Generated and committed: `web/package-lock.json`

**Interfaces:**
- Produces (from `web/src/graph/types.ts`):
  - the camelCase graph types `DepGraph`, `RepoNode`, `ProjectNode`, `PackageNode`, `EndpointNode`, `ClientMethodNode`, `CallSiteNode`, `ModelNode`, `ModelField`, `Edge`, `Diagnostic`, `SourceLocation`
  - the string unions `Confidence`, `EdgeKind`, `ProjectKind`, `ModelKind`, `Severity`
  - `CONFIDENCE_ORDER`, `confidenceRank(c)`, `minConfidence(a, b)`
- Produces (from `web/src/graph/load.ts`):
  - `SUPPORTED_SCHEMA = 1`
  - `readInlineGraph(doc): string | null`
  - `readFocus(doc): string | null`
  - `parseGraph(json): LoadResult`, where `LoadResult = { ok: true; graph } | { ok: false; reason: 'schema'; found } | { ok: false; reason: 'invalid'; message }`
  - `dropDanglingEdges(g)`
- Produces (test utilities): `fixtureJson()`, `fixtureGraph()`, `repoRoot`.
- The build output `web/dist/index.html` contains `<script type="application/json" id="depenk-graph"></script>` and `<meta name="depenk-focus" content="">`. Task 12 depends on these exact strings.

- [ ] **Step 1: Project files**

`web/package.json`:

```json
{
  "name": "depenk-web",
  "private": true,
  "version": "0.3.0",
  "type": "module",
  "engines": { "node": ">=22" },
  "scripts": {
    "dev": "vite",
    "typecheck": "tsc --noEmit -p tsconfig.json",
    "build": "tsc --noEmit -p tsconfig.json && vite build && node scripts/check-bundle.mjs",
    "test": "vitest run",
    "e2e": "playwright test"
  },
  "dependencies": {
    "@fontsource/ibm-plex-mono": "5.3.0",
    "@fontsource/ibm-plex-sans": "5.3.0",
    "@fontsource/ibm-plex-sans-condensed": "5.3.0",
    "@xyflow/react": "12.12.0",
    "elkjs": "0.12.0",
    "html-to-image": "1.11.13",
    "react": "19.3.0",
    "react-dom": "19.3.0"
  },
  "devDependencies": {
    "@playwright/test": "1.63.0",
    "@testing-library/jest-dom": "7.0.1",
    "@testing-library/react": "16.3.3",
    "@testing-library/user-event": "14.6.7",
    "@types/node": "22.20.5",
    "@types/react": "19.3.0",
    "@types/react-dom": "19.3.0",
    "@vitejs/plugin-react": "6.1.1",
    "jsdom": "30.1.1",
    "typescript": "5.9.3",
    "vite": "8.3.2",
    "vite-plugin-singlefile": "2.3.3",
    "vitest": "5.0.3"
  }
}
```

`web/tsconfig.json`:

```json
{
  "compilerOptions": {
    "target": "ES2022",
    "lib": ["ES2023", "DOM", "DOM.Iterable"],
    "module": "ESNext",
    "moduleResolution": "Bundler",
    "jsx": "react-jsx",
    "strict": true,
    "noEmit": true,
    "isolatedModules": true,
    "skipLibCheck": true,
    "resolveJsonModule": true,
    "types": ["vite/client", "node", "@testing-library/jest-dom/vitest"]
  },
  "include": ["src", "test", "e2e", "vite.config.ts", "vitest.config.ts", "playwright.config.ts"]
}
```

`web/vite.config.ts`:

```ts
import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import { viteSingleFile } from 'vite-plugin-singlefile';

// One self-contained index.html: scripts, styles, fonts and the layout worker are all inlined.
export default defineConfig({
  plugins: [react(), viteSingleFile()],
  build: { target: 'es2022', assetsInlineLimit: 100_000_000, cssCodeSplit: false, chunkSizeWarningLimit: 10_000 },
  worker: { format: 'es' },
});
```

`web/vitest.config.ts`:

```ts
import { defineConfig } from 'vitest/config';
import react from '@vitejs/plugin-react';

export default defineConfig({
  plugins: [react()],
  test: {
    environment: 'jsdom',
    setupFiles: ['test/setup.ts'],
    include: ['test/**/*.test.ts', 'test/**/*.test.tsx'],
    testTimeout: 20_000,
  },
});
```

`web/index.html`. The two placeholders are filled by `DiagramExporter` (C#) and `buildExportHtml` (TypeScript), so keep them byte-for-byte:

```html
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>depenk</title>
<meta name="depenk-focus" content="">
<script type="application/json" id="depenk-graph"></script>
</head>
<body>
<div id="root"></div>
<script type="module" src="/src/main.tsx"></script>
</body>
</html>
```

`web/scripts/check-bundle.mjs`:

```js
// Build guard: dist must be exactly one self-contained HTML file with both data placeholders and no external URLs.
import { readdirSync, readFileSync } from 'node:fs';

const files = readdirSync(new URL('../dist/', import.meta.url));
const fail = (msg) => { console.error(`check-bundle: ${msg}`); process.exit(1); };
if (files.length !== 1 || files[0] !== 'index.html') fail(`dist must contain only index.html, found: ${files.join(', ')}`);
const html = readFileSync(new URL('../dist/index.html', import.meta.url), 'utf8');
for (const p of ['<script type="application/json" id="depenk-graph"></script>', '<meta name="depenk-focus" content="">'])
  if (!html.includes(p)) fail(`placeholder missing: ${p}`);
if (/<script[^>]+src=/i.test(html)) fail('external <script src> found');
if (/<link[^>]+rel="stylesheet"[^>]+href=/i.test(html)) fail('external stylesheet found');
if (/fonts\.googleapis|cdn\.|unpkg\.com/i.test(html)) fail('CDN reference found');
console.log(`check-bundle: ok (${Math.round(html.length / 1024)} KB)`);
```

`web/src/main.tsx` (temporary; Task 7 replaces it):

```tsx
import { createRoot } from 'react-dom/client';

createRoot(document.getElementById('root')!).render(<p>depenk</p>);
```

Install, which generates `package-lock.json`: `cd web && npm install`. If npm reports a peer-dependency conflict for one of the pinned versions, use the newest version that resolves cleanly, and record it in `package.json` and the task report.

- [ ] **Step 2: Write the failing loading tests**

`web/test/setup.ts`:

```ts
import '@testing-library/jest-dom/vitest';
import { afterEach } from 'vitest';
import { cleanup } from '@testing-library/react';

afterEach(() => {
  cleanup();
  localStorage.clear();
  history.replaceState(null, '', '/');
  document.documentElement.removeAttribute('data-theme');
});

// React Flow needs these browser APIs, which jsdom lacks (from React Flow's testing guide).
class ResizeObserverStub {
  constructor(private readonly cb: ResizeObserverCallback) {}
  observe(target: Element) { this.cb([{ target } as ResizeObserverEntry], this as unknown as ResizeObserver); }
  unobserve() {}
  disconnect() {}
}
class DOMMatrixReadOnlyStub {
  m22: number;
  constructor(transform?: string) { const s = transform?.match(/scale\(([0-9.]+)\)/)?.[1]; this.m22 = s !== undefined ? +s : 1; }
}
Object.assign(globalThis, { ResizeObserver: ResizeObserverStub, DOMMatrixReadOnly: DOMMatrixReadOnlyStub });
Object.defineProperties(HTMLElement.prototype, {
  offsetHeight: { get() { return parseFloat((this as HTMLElement).style.height) || 1; } },
  offsetWidth: { get() { return parseFloat((this as HTMLElement).style.width) || 1; } },
});
(SVGElement.prototype as unknown as { getBBox: () => DOMRect }).getBBox = () => ({ x: 0, y: 0, width: 0, height: 0 }) as DOMRect;
```

`web/test/util/fixture.ts`:

```ts
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { parseGraph } from '../../src/graph/load';
import type { DepGraph } from '../../src/graph/types';

export const repoRoot = fileURLToPath(new URL('../../../', import.meta.url));
export const fixtureJson = (): string =>
  readFileSync(join(repoRoot, 'tests', 'Depenk.Tests', 'Snapshots', 'FixtureScanTests.Snapshot.verified.json'), 'utf8');
export function fixtureGraph(): DepGraph {
  const r = parseGraph(fixtureJson());
  if (!r.ok) throw new Error('fixture graph did not load');
  return r.graph;
}
```

`web/test/load.test.ts`:

```ts
import { describe, expect, test } from 'vitest';
import { parseGraph, readFocus, readInlineGraph } from '../src/graph/load';
import { fixtureGraph, fixtureJson } from './util/fixture';

const doc = (head: string) => new DOMParser().parseFromString(`<!doctype html><html><head>${head}</head><body></body></html>`, 'text/html');

describe('readInlineGraph / readFocus', () => {
  test('empty placeholder means serve mode', () => {
    expect(readInlineGraph(doc('<script type="application/json" id="depenk-graph"></script>'))).toBeNull();
    expect(readFocus(doc('<meta name="depenk-focus" content="">'))).toBeNull();
  });
  test('filled placeholder is returned', () => {
    expect(readInlineGraph(doc('<script type="application/json" id="depenk-graph">{"a":1}</script>'))).toBe('{"a":1}');
    expect(readFocus(doc('<meta name="depenk-focus" content="repo:orders">'))).toBe('repo:orders');
  });
});

describe('parseGraph', () => {
  test('loads the fixture (with its UTF-8 BOM)', () => {
    expect(fixtureJson().charCodeAt(0)).toBe(0xfeff);
    const g = fixtureGraph();
    expect(g.repos.map(r => r.name)).toEqual(['billing', 'customers', 'gateway', 'orders', 'shared']);
    expect(g.diagnostics.some(d => d.kind === 'danglingEdge')).toBe(false);
  });
  test('refuses another schemaVersion', () => {
    expect(parseGraph('{"schemaVersion":2,"repos":[]}')).toEqual({ ok: false, reason: 'schema', found: 2 });
  });
  test('reports invalid JSON and non-graphs', () => {
    expect(parseGraph('{ nope').ok).toBe(false);
    expect(parseGraph('[]')).toMatchObject({ ok: false, reason: 'invalid' });
  });
  test('drops dangling edges and reports them as diagnostics', () => {
    const json = JSON.stringify({
      schemaVersion: 1, generatedAt: '2026-10-02T00:00:00Z',
      repos: [{ id: 'repo:a', name: 'a', path: 'a', dirty: false }],
      edges: [{ kind: 'dependsOn', from: 'repo:a', to: 'repo:ghost', confidence: 'certain' }],
    });
    const r = parseGraph(json);
    if (!r.ok) throw new Error('expected ok');
    expect(r.graph.edges).toEqual([]);
    expect(r.graph.projects).toEqual([]); // missing arrays are normalized
    expect(r.graph.diagnostics).toEqual([{
      kind: 'danglingEdge', severity: 'warning', nodeIds: ['repo:a'],
      message: 'dependsOn edge repo:a → repo:ghost points at a node that is not in the graph',
    }]);
  });
});
```

- [ ] **Step 3: Run to verify failure**

Run: `cd web && npx vitest run test/load.test.ts`
Expected: FAIL, because `../src/graph/load` cannot be resolved.

- [ ] **Step 4: Implement types and loading**

`web/src/graph/types.ts`:

```ts
// Mirrors Depenk.Core (graph.json, schemaVersion 1): camelCase properties, camelCase enums, nulls omitted.
export type Confidence = 'low' | 'medium' | 'high' | 'certain';
export type EdgeKind = 'references' | 'produces' | 'targets' | 'invokes' | 'accepts' | 'returns' | 'fieldOf' | 'dependsOn';
export type ProjectKind = 'api' | 'client' | 'library' | 'test' | 'other';
export type ModelKind = 'class' | 'record' | 'struct' | 'enum' | 'opaque';
export type Severity = 'info' | 'warning' | 'error';

export interface SourceLocation { path: string; line: number }
export interface RepoNode { id: string; name: string; path: string; headSha?: string; dirty: boolean }
export interface ProjectNode {
  id: string; repo: string; name: string; path: string; kind: ProjectKind;
  sdk?: string; packageId?: string; version?: string; isPackable: boolean;
}
export interface PackageNode { id: string; packageId: string; producerProjectIds: string[] }
export interface EndpointParameter { name: string; source: string; typeName: string; required: boolean; default?: string }
export interface ResponseType { statusCode: number; typeName: string }
export interface EndpointNode {
  id: string; repo: string; projectId: string; verb: string; route: string; normalizedRoute: string; handler: string;
  parameters: EndpointParameter[]; responses: ResponseType[]; location: SourceLocation;
}
export interface ClientMethodNode {
  id: string; repo: string; projectId: string; typeName: string; methodName: string; signature: string;
  verb?: string; route?: string; normalizedRoute?: string; strategy: string; confidence: Confidence; location: SourceLocation;
}
export interface CallSiteNode { id: string; repo: string; projectId: string; containingMember: string; confidence: Confidence; location: SourceLocation }
export interface ModelField { name: string; typeName: string; nullable: boolean; collection: boolean }
export interface ModelNode {
  id: string; repo: string; projectId?: string; fullName: string; kind: ModelKind;
  fields: ModelField[]; enumValues?: string[]; location?: SourceLocation;
}
export interface Edge {
  kind: EdgeKind; from: string; to: string; confidence: Confidence;
  version?: string; strategy?: string; source?: string; statusCode?: number; fieldName?: string;
  viaPackages?: string[]; callCount?: number;
}
export interface Diagnostic { kind: string; severity: Severity; nodeIds: string[]; message: string }
export interface DepGraph {
  schemaVersion: number; workspace?: string; generatedAt: string;
  repos: RepoNode[]; projects: ProjectNode[]; packages: PackageNode[]; endpoints: EndpointNode[];
  clientMethods: ClientMethodNode[]; callSites: CallSiteNode[]; models: ModelNode[];
  edges: Edge[]; diagnostics: Diagnostic[];
}

export const CONFIDENCE_ORDER: readonly Confidence[] = ['low', 'medium', 'high', 'certain'];
export const confidenceRank = (c: Confidence): number => CONFIDENCE_ORDER.indexOf(c);
export const minConfidence = (a: Confidence, b: Confidence): Confidence => (confidenceRank(a) <= confidenceRank(b) ? a : b);
```

`web/src/graph/load.ts`:

```ts
import type { DepGraph, Diagnostic, Edge } from './types';

export const SUPPORTED_SCHEMA = 1;

export type LoadResult =
  | { ok: true; graph: DepGraph }
  | { ok: false; reason: 'schema'; found: unknown }
  | { ok: false; reason: 'invalid'; message: string };

/** The export's inlined graph, or null in serve mode (the placeholder is empty). */
export function readInlineGraph(doc: Document): string | null {
  const text = doc.getElementById('depenk-graph')?.textContent ?? '';
  return text.trim() === '' ? null : text;
}

/** The node an export was asked to open on (`depenk export --focus`), or null. */
export function readFocus(doc: Document): string | null {
  const v = doc.querySelector('meta[name="depenk-focus"]')?.getAttribute('content') ?? '';
  return v === '' ? null : v;
}

export function parseGraph(json: string): LoadResult {
  let raw: unknown;
  try {
    raw = JSON.parse(json.replace(/^﻿/, ''));
  } catch (e) {
    return { ok: false, reason: 'invalid', message: (e as Error).message };
  }
  if (typeof raw !== 'object' || raw === null || Array.isArray(raw)) return { ok: false, reason: 'invalid', message: 'not a depenk graph' };
  const g = raw as Partial<DepGraph>;
  if (g.schemaVersion !== SUPPORTED_SCHEMA) return { ok: false, reason: 'schema', found: g.schemaVersion };
  return { ok: true, graph: dropDanglingEdges(normalize(g)) };
}

function normalize(g: Partial<DepGraph>): DepGraph {
  return {
    schemaVersion: g.schemaVersion ?? SUPPORTED_SCHEMA, workspace: g.workspace, generatedAt: g.generatedAt ?? '',
    repos: g.repos ?? [], projects: g.projects ?? [], packages: g.packages ?? [], endpoints: g.endpoints ?? [],
    clientMethods: g.clientMethods ?? [], callSites: g.callSites ?? [], models: g.models ?? [],
    edges: g.edges ?? [], diagnostics: g.diagnostics ?? [],
  };
}

/** Edges whose ends are not nodes would break layout; drop them and say so in the diagnostics list. */
export function dropDanglingEdges(g: DepGraph): DepGraph {
  const ids = new Set<string>();
  for (const list of [g.repos, g.projects, g.packages, g.endpoints, g.clientMethods, g.callSites, g.models])
    for (const n of list) ids.add(n.id);
  const kept: Edge[] = [];
  const dangling: Diagnostic[] = [];
  for (const e of g.edges) {
    if (ids.has(e.from) && ids.has(e.to)) { kept.push(e); continue; }
    dangling.push({
      kind: 'danglingEdge', severity: 'warning', nodeIds: [e.from, e.to].filter(id => ids.has(id)),
      message: `${e.kind} edge ${e.from} → ${e.to} points at a node that is not in the graph`,
    });
  }
  return dangling.length === 0 ? g : { ...g, edges: kept, diagnostics: [...g.diagnostics, ...dangling] };
}
```

- [ ] **Step 5: Run tests and the build**

Run: `cd web && npx vitest run && npm run build`
Expected: the tests PASS. The build prints `check-bundle: ok (… KB)` and `dist/` contains only `index.html`.

- [ ] **Step 6: Commit**

```bash
git add web
git commit -m "feat(web): Vite single-file scaffold, graph types and loading with schema and dangling-edge checks

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: TypeScript query layer (`GraphIndex`, queries) and the shared conformance runner

**Files:**
- Create: `web/src/graph/fuzzy.ts`, `web/src/graph/graphIndex.ts`, `web/src/graph/queries.ts`
- Test: `web/test/graphIndex.test.ts`, `web/test/queries.test.ts`, `web/test/conformance.test.ts`

**Interfaces:**
- Consumes: the Task 3 types; `tests/query-cases/*.json` (Task 2 added `impact`).
- Produces (`graphIndex.ts`):
  - `type NodeKind = 'repo' | 'project' | 'package' | 'endpoint' | 'clientMethod' | 'callSite' | 'model'`
  - `interface NodeRef { id; kind: NodeKind; label; repo: string | null }`
  - `interface Hop { from; to; kind: EdgeKind; confidence: Confidence; edge: Edge }` (`from` depends on `to`; `produces` is flipped, as in C#)
  - `class QueryError extends Error { code: 'not_found' | 'ambiguous' | 'invalid_argument'; suggestions: string[] }`
  - `ordinal(a, b)`
  - `class GraphIndex`:
    - fields: `graph`, `repos`, `projects`, `packages`, `endpoints`, `clientMethods`, `callSites`, `models` (all `Map<id, node>`)
    - methods: `nodes()`, `tryGet(id)`, `get(id)`, `dependenciesOf(id)`, `dependentsOf(id)`, `resolveRepo`, `resolvePackage`, `resolveEndpoint`, `resolveModel`, `resolveAny`, `suggest(q, max = 5)`, `notFound(q)`
- Produces (`queries.ts`):
  - constants: `DEFAULT_LIMIT = 50`, `MAX_LIMIT = 500`
  - `cap(limit?)`
  - `neighbors(ix, id, direction)`
  - `trace(ix, node, direction = 'down', depth = 3, limit?) → TraceResult { root; direction; nodes: TraceStep[]; truncated }`, where `TraceStep { id; kind; label; direction: 'down' | 'up'; depth; viaFrom; via: EdgeKind; confidence }`
  - `relax(ix, start, follow) → Map<id, Confidence>`
  - `resolveImpactTarget(ix, target) → { start: string; field: string | null }`
  - `computeImpact(ix, target) → ImpactResult` (uncapped)
  - `impactOfChange(ix, target, limit?) → ImpactResult` (capped, the same as MCP)
  - `endpointsUsingModel(ix, modelId) → string[]`
  - `ImpactResult { target; targetKind; field: string | null; repos; projects; endpoints; clientMethods; callSites; models: Affected[]; totals: Record<'repos' | 'projects' | 'endpoints' | 'clientMethods' | 'callSites' | 'models', number>; truncated }`
  - `Affected { id; label; repo; confidence }`

- [ ] **Step 1: Write the failing tests**

`web/test/conformance.test.ts`:

```ts
import { readdirSync, readFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { describe, expect, test } from 'vitest';
import { GraphIndex, ordinal } from '../src/graph/graphIndex';
import { impactOfChange, MAX_LIMIT, neighbors, trace } from '../src/graph/queries';
import type { DepGraph } from '../src/graph/types';
import { repoRoot } from './util/fixture';

// The same files C# runs in QueryConformanceTests: both implementations must agree exactly.
const casesDir = join(repoRoot, 'tests', 'query-cases');
const files = readdirSync(casesDir).filter(f => f.endsWith('.json')).sort(ordinal);
const read = (path: string) => JSON.parse(readFileSync(path, 'utf8').replace(/^﻿/, ''));

interface Case { name: string; graph: string; op: string; args: Record<string, string | number>; expected: string[] }

test('case files are present (zero-case guard)', () => {
  expect(files.length).toBeGreaterThanOrEqual(15);
  expect(files.some(f => f.startsWith('impact-'))).toBe(true);
});

describe.each(files)('%s', file => {
  test('matches expected', () => {
    const c = read(join(casesDir, file)) as Case;
    const ix = new GraphIndex(read(resolve(casesDir, c.graph)) as DepGraph);
    const s = (k: string) => String(c.args[k]);
    let actual: string[];
    switch (c.op) {
      case 'neighbors': actual = neighbors(ix, s('id'), s('direction') as 'down' | 'up'); break;
      case 'trace': actual = trace(ix, s('id'), s('direction'), Number(c.args.depth), MAX_LIMIT).nodes.map(n => `${n.id}@${n.depth}`); break;
      case 'search': actual = ix.suggest(s('query'), Number(c.args.limit)); break;
      case 'impact': {
        const r = impactOfChange(ix, s('target'), MAX_LIMIT);
        actual = [`target:${r.target}${r.field === null ? '' : '#' + r.field}`,
          ...[r.repos, r.projects, r.endpoints, r.clientMethods, r.callSites, r.models].flat().map(a => `${a.id}@${a.confidence}`)];
        break;
      }
      default: throw new Error(`unknown op ${c.op}`);
    }
    expect(actual).toEqual(c.expected);
  });
});
```

`web/test/graphIndex.test.ts`:

```ts
import { describe, expect, test } from 'vitest';
import { GraphIndex, QueryError } from '../src/graph/graphIndex';
import { fixtureGraph } from './util/fixture';

const ix = new GraphIndex(fixtureGraph());
const err = (f: () => unknown): QueryError => { try { f(); } catch (e) { if (e instanceof QueryError) return e; throw e; } throw new Error('expected a QueryError'); };

describe('GraphIndex', () => {
  test('labels mirror C# NodeRef labels', () => {
    expect(ix.get('ep:orders:GET:/api/orders/{id}').label).toBe('GET /api/orders/{id}');
    expect(ix.get('cm:Orders.Client:IOrdersClient.GetOrderAsync').label).toBe('IOrdersClient.GetOrderAsync');
    expect(ix.get('pkg:Orders.Client')).toMatchObject({ kind: 'package', label: 'Orders.Client', repo: 'orders' });
    expect(ix.get('pkg:Refit').repo).toBeNull();
  });
  test('produces edges are flipped to package → project', () => {
    expect(ix.dependenciesOf('pkg:Orders.Client').map(h => h.to)).toEqual(['proj:orders/Orders.Client']);
  });
  test('resolvers', () => {
    expect(ix.resolveAny('Orders')).toBe('repo:orders');
    expect(ix.resolveAny('orders.client')).toBe('pkg:Orders.Client');
    expect(ix.resolveAny('get /api/orders/{x}')).toBe('ep:orders:GET:/api/orders/{id}');
    expect(ix.resolveAny('OrderDto')).toBe('model:Orders.Client:Acme.Orders.Client.OrderDto');
    expect(ix.resolveAny('Acme.Shared.Money')).toBe('model:Shared.Kernel:Acme.Shared.Money');
  });
  test('ambiguous endpoints list their ids', () => {
    const e = err(() => ix.resolveEndpoint('GET /api/customers/{x}'));
    expect(e.code).toBe('ambiguous');
    expect(e.suggestions).toEqual(['ep:customers:GET:/api/customers/{id:guid}', 'ep:customers:GET:/api/customers/{slug}']);
  });
  test('not found carries fuzzy suggestions', () => {
    const e = err(() => ix.get('repo:order'));
    expect(e.code).toBe('not_found');
    expect(e.suggestions[0]).toBe('repo:orders');
  });
});
```

`web/test/queries.test.ts`:

```ts
import { describe, expect, test } from 'vitest';
import { GraphIndex, QueryError } from '../src/graph/graphIndex';
import { computeImpact, endpointsUsingModel, impactOfChange, trace } from '../src/graph/queries';
import { fixtureGraph } from './util/fixture';

const ix = new GraphIndex(fixtureGraph());
const code = (f: () => unknown) => { try { f(); } catch (e) { return (e as QueryError).code; } return 'none'; };

describe('trace', () => {
  test('rejects unknown directions before resolving', () => expect(code(() => trace(ix, 'nope', 'sideways'))).toBe('invalid_argument'));
  test('caps and flags truncation', () => {
    const r = trace(ix, 'repo:orders', 'both', 3, 1);
    expect(r.nodes).toHaveLength(1);
    expect(r.truncated).toBe(true);
  });
});

describe('impact', () => {
  test('unknown field of a known model is not_found', () => expect(code(() => computeImpact(ix, 'OrderDto.Nope'))).toBe('not_found'));
  test('field targets report the field and the model', () => {
    const r = computeImpact(ix, 'OrderDto.Lines');
    expect([r.target, r.targetKind, r.field]).toEqual(['model:Orders.Client:Acme.Orders.Client.OrderDto', 'field', 'Lines']);
  });
  test('impactOfChange caps each list and sets totals', () => {
    const r = impactOfChange(ix, 'Money', 1);
    expect(r.clientMethods).toHaveLength(1);
    expect(r.totals.clientMethods).toBe(6);
    expect(r.truncated).toBe(true);
    expect(computeImpact(ix, 'Money').clientMethods).toHaveLength(6);
  });
});

test('endpointsUsingModel follows nesting', () => {
  expect(endpointsUsingModel(ix, 'model:Shared.Kernel:Acme.Shared.Money'))
    .toEqual(['ep:orders:GET:/api/orders', 'ep:orders:GET:/api/orders/{id}', 'ep:orders:POST:/api/orders']);
});
```

- [ ] **Step 2: Run to verify failure**

Run: `cd web && npx vitest run test/conformance.test.ts test/graphIndex.test.ts test/queries.test.ts`
Expected: FAIL, because `../src/graph/graphIndex` cannot be resolved.

- [ ] **Step 3: Implement**

`web/src/graph/fuzzy.ts`:

```ts
/** Levenshtein distance over UTF-16 code units, unit costs, no transposition (same as C# Depenk.Query.Fuzzy). */
export function distance(a: string, b: string): number {
  if (a.length === 0) return b.length;
  if (b.length === 0) return a.length;
  let prev = Array.from({ length: b.length + 1 }, (_, j) => j);
  let cur = new Array<number>(b.length + 1).fill(0);
  for (let i = 1; i <= a.length; i++) {
    cur[0] = i;
    for (let j = 1; j <= b.length; j++) {
      const cost = a.charCodeAt(i - 1) === b.charCodeAt(j - 1) ? 0 : 1;
      cur[j] = Math.min(cur[j - 1]! + 1, prev[j]! + 1, prev[j - 1]! + cost);
    }
    [prev, cur] = [cur, prev];
  }
  return prev[b.length]!;
}
```

`web/src/graph/graphIndex.ts`:

```ts
import { distance } from './fuzzy';
import type {
  CallSiteNode, ClientMethodNode, Confidence, DepGraph, Edge, EdgeKind, EndpointNode, ModelNode, PackageNode, ProjectNode, RepoNode,
} from './types';

export type NodeKind = 'repo' | 'project' | 'package' | 'endpoint' | 'clientMethod' | 'callSite' | 'model';
export interface NodeRef { id: string; kind: NodeKind; label: string; repo: string | null }
/** A dependency hop: `from` depends on `to`. `edge` is the original edge (`produces` is flipped to package → project). */
export interface Hop { from: string; to: string; kind: EdgeKind; confidence: Confidence; edge: Edge }
export type QueryErrorCode = 'not_found' | 'ambiguous' | 'invalid_argument';

export class QueryError extends Error {
  constructor(readonly code: QueryErrorCode, message: string, readonly suggestions: string[] = []) {
    super(message);
    this.name = 'QueryError';
  }
}

/** Ordinal (UTF-16 code unit) comparison, the same as C# StringComparer.Ordinal. */
export const ordinal = (a: string, b: string): number => (a < b ? -1 : a > b ? 1 : 0);

function byId<T extends { id: string }>(items: readonly T[]): Map<string, T> {
  const m = new Map<string, T>();
  for (const it of items) if (!m.has(it.id)) m.set(it.id, it);
  return m;
}

function append(map: Map<string, Hop[]>, key: string, hop: Hop) {
  const list = map.get(key);
  if (list) list.push(hop); else map.set(key, [hop]);
}

function simpleName(fullName: string): string {
  const s = fullName.slice(fullName.lastIndexOf('.') + 1);
  const tick = s.indexOf('`');
  return tick >= 0 ? s.slice(0, tick) : s;
}

export function normalizeRoute(route: string): string {
  return route.split(/[?#]/)[0]!.replace(/\{[^{}]*\}/g, '{}')
    .split('/').map(s => s.trim()).filter(s => s !== '').join('/').toLowerCase();
}

export class GraphIndex {
  readonly repos: Map<string, RepoNode>;
  readonly projects: Map<string, ProjectNode>;
  readonly packages: Map<string, PackageNode>;
  readonly endpoints: Map<string, EndpointNode>;
  readonly clientMethods: Map<string, ClientMethodNode>;
  readonly callSites: Map<string, CallSiteNode>;
  readonly models: Map<string, ModelNode>;
  private readonly byNodeId = new Map<string, NodeRef>();
  private readonly deps = new Map<string, Hop[]>();
  private readonly dependents = new Map<string, Hop[]>();

  constructor(readonly graph: DepGraph) {
    this.repos = byId(graph.repos);
    this.projects = byId(graph.projects);
    this.packages = byId(graph.packages);
    this.endpoints = byId(graph.endpoints);
    this.clientMethods = byId(graph.clientMethods);
    this.callSites = byId(graph.callSites);
    this.models = byId(graph.models);

    for (const r of this.repos.values()) this.add({ id: r.id, kind: 'repo', label: r.name, repo: r.name });
    for (const p of this.projects.values()) this.add({ id: p.id, kind: 'project', label: p.name, repo: p.repo });
    for (const p of this.packages.values())
      this.add({ id: p.id, kind: 'package', label: p.packageId,
        repo: p.producerProjectIds.map(id => this.projects.get(id)?.repo).find(r => r !== undefined) ?? null });
    for (const e of this.endpoints.values()) this.add({ id: e.id, kind: 'endpoint', label: `${e.verb} ${e.route}`, repo: e.repo });
    for (const c of this.clientMethods.values()) this.add({ id: c.id, kind: 'clientMethod', label: `${c.typeName}.${c.methodName}`, repo: c.repo });
    for (const c of this.callSites.values()) this.add({ id: c.id, kind: 'callSite', label: c.containingMember, repo: c.repo });
    for (const m of this.models.values()) this.add({ id: m.id, kind: 'model', label: m.fullName, repo: m.repo ? m.repo : null });

    for (const e of graph.edges) {
      const hop: Hop = e.kind === 'produces'
        ? { from: e.to, to: e.from, kind: e.kind, confidence: e.confidence, edge: e }
        : { from: e.from, to: e.to, kind: e.kind, confidence: e.confidence, edge: e };
      append(this.deps, hop.from, hop);
      append(this.dependents, hop.to, hop);
    }
  }

  nodes(): IterableIterator<NodeRef> { return this.byNodeId.values(); }
  tryGet(id: string): NodeRef | undefined { return this.byNodeId.get(id); }
  get(id: string): NodeRef { const n = this.byNodeId.get(id); if (!n) throw this.notFound(id); return n; }
  dependenciesOf(id: string): readonly Hop[] { return this.deps.get(id) ?? []; }
  dependentsOf(id: string): readonly Hop[] { return this.dependents.get(id) ?? []; }

  resolveRepo(q: string): string {
    if (this.byNodeId.get(q)?.kind === 'repo') return q;
    const lq = q.toLowerCase();
    for (const r of this.repos.values()) if (r.name.toLowerCase() === lq) return r.id;
    throw this.notFound(q);
  }

  resolvePackage(q: string): string {
    if (this.packages.has(q)) return q;
    const lq = q.toLowerCase();
    for (const p of this.packages.values()) if (p.packageId.toLowerCase() === lq) return p.id;
    throw this.notFound(q);
  }

  resolveEndpoint(q: string): string {
    if (this.endpoints.has(q)) return q;
    const t = q.trim();
    const space = t.indexOf(' ');
    if (space <= 0) throw this.notFound(q);
    const verb = t.slice(0, space).toUpperCase();
    const route = t.slice(space + 1).trim();
    const sameVerb = [...this.endpoints.values()].filter(e => e.verb.toUpperCase() === verb);
    const exact = sameVerb.filter(e => e.route.toLowerCase() === route.toLowerCase());
    const matches = exact.length > 0 ? exact : sameVerb.filter(e => e.normalizedRoute === normalizeRoute(route));
    if (matches.length === 1) return matches[0]!.id;
    if (matches.length === 0) throw this.notFound(q);
    throw new QueryError('ambiguous', `'${q}' matches ${matches.length} endpoints`, matches.map(m => m.id).sort(ordinal));
  }

  resolveModel(q: string): string {
    if (this.models.has(q)) return q;
    const all = [...this.models.values()];
    const full = all.filter(m => m.fullName === q);
    const matches = full.length > 0 ? full : all.filter(m => simpleName(m.fullName) === q);
    if (matches.length === 1) return matches[0]!.id;
    if (matches.length === 0) throw this.notFound(q);
    throw new QueryError('ambiguous', `'${q}' matches ${matches.length} models`, matches.map(m => m.id).sort(ordinal));
  }

  /** Exact id, repo name, package id, "VERB /route", then model, the same order as C# GraphIndex.ResolveAny. */
  resolveAny(q: string): string {
    if (this.byNodeId.has(q)) return q;
    const lq = q.toLowerCase();
    for (const r of this.repos.values()) if (r.name.toLowerCase() === lq) return r.id;
    for (const p of this.packages.values()) if (p.packageId.toLowerCase() === lq) return p.id;
    if (q.trim().includes(' ')) return this.resolveEndpoint(q);
    return this.resolveModel(q);
  }

  /** Substring matches first, then smaller edit distance to id or label, then ordinal id (tests/query-cases/README.md). */
  suggest(q: string, max = 5): string[] {
    const needle = q.toLowerCase();
    return [...this.byNodeId.values()]
      .map(n => {
        const id = n.id.toLowerCase(), label = n.label.toLowerCase();
        return { id: n.id, contains: id.includes(needle) || label.includes(needle), dist: Math.min(distance(id, needle), distance(label, needle)) };
      })
      .sort((a, b) => Number(b.contains) - Number(a.contains) || a.dist - b.dist || ordinal(a.id, b.id))
      .slice(0, max)
      .map(x => x.id);
  }

  notFound(q: string): QueryError {
    return new QueryError('not_found', `No node matches '${q}'`, this.suggest(q));
  }

  private add(n: NodeRef) { if (!this.byNodeId.has(n.id)) this.byNodeId.set(n.id, n); }
}
```

`web/src/graph/queries.ts`:

```ts
import { GraphIndex, type Hop, type NodeKind, ordinal, QueryError } from './graphIndex';
import { confidenceRank, minConfidence, type Confidence, type EdgeKind } from './types';

export const DEFAULT_LIMIT = 50;
export const MAX_LIMIT = 500;
export const cap = (limit?: number): number => Math.min(Math.max(limit ?? DEFAULT_LIMIT, 1), MAX_LIMIT);

export function neighbors(ix: GraphIndex, id: string, direction: 'down' | 'up'): string[] {
  if (direction !== 'down' && direction !== 'up') throw new QueryError('invalid_argument', `direction must be 'down' or 'up', got '${String(direction)}'`);
  const far = direction === 'down' ? ix.dependenciesOf(id).map(h => h.to) : ix.dependentsOf(id).map(h => h.from);
  return [...new Set(far)].sort(ordinal);
}

export interface TraceStep {
  id: string; kind: NodeKind; label: string; direction: 'down' | 'up'; depth: number; viaFrom: string; via: EdgeKind; confidence: Confidence;
}
export interface TraceResult { root: string; direction: string; nodes: TraceStep[]; truncated: boolean }

export function trace(ix: GraphIndex, node: string, direction = 'down', depth = 3, limit?: number): TraceResult {
  const dir = (direction ?? '').toLowerCase();
  const dirs: ('down' | 'up')[] | null = dir === 'down' ? ['down'] : dir === 'up' ? ['up'] : dir === 'both' ? ['down', 'up'] : null;
  if (!dirs) throw new QueryError('invalid_argument', `direction must be up, down or both (got '${direction}')`);
  const root = ix.resolveAny(node);
  const maxDepth = Math.min(Math.max(depth, 1), 10);
  const steps: TraceStep[] = [];
  for (const d of dirs) {
    const far = (h: Hop) => (d === 'down' ? h.to : h.from);
    const visited = new Set<string>([root]);
    const queue: { id: string; depth: number; conf: Confidence }[] = [{ id: root, depth: 0, conf: 'certain' }];
    for (let qi = 0; qi < queue.length; qi++) {
      const cur = queue[qi]!;
      if (cur.depth >= maxDepth) continue;
      const hops = [...(d === 'down' ? ix.dependenciesOf(cur.id) : ix.dependentsOf(cur.id))].sort((a, b) => ordinal(far(a), far(b)));
      for (const h of hops) {
        const next = far(h);
        if (visited.has(next)) continue;
        visited.add(next); // marked even when unknown, as in C#
        const n = ix.tryGet(next);
        if (!n) continue;
        const conf = minConfidence(cur.conf, h.confidence);
        steps.push({ id: next, kind: n.kind, label: n.label, direction: d, depth: cur.depth + 1, viaFrom: cur.id, via: h.kind, confidence: conf });
        queue.push({ id: next, depth: cur.depth + 1, conf });
      }
    }
  }
  const c = cap(limit);
  return { root, direction: dir, nodes: steps.slice(0, c), truncated: steps.length > c };
}

/** Best confidence (max over paths of min along the path) for every node reachable through dependents. */
export function relax(ix: GraphIndex, start: string, follow: (h: Hop) => boolean): Map<string, Confidence> {
  const best = new Map<string, Confidence>([[start, 'certain']]);
  const queue = [start];
  for (let qi = 0; qi < queue.length; qi++) {
    const cur = queue[qi]!;
    for (const h of ix.dependentsOf(cur)) {
      if (!follow(h) || !ix.tryGet(h.from)) continue;
      const c = minConfidence(best.get(cur)!, h.confidence);
      const old = best.get(h.from);
      if (old !== undefined && confidenceRank(c) <= confidenceRank(old)) continue;
      best.set(h.from, c);
      queue.push(h.from);
    }
  }
  return best;
}

export interface Affected { id: string; label: string; repo: string; confidence: Confidence }
type ImpactList = 'repos' | 'projects' | 'endpoints' | 'clientMethods' | 'callSites' | 'models';
export interface ImpactResult extends Record<ImpactList, Affected[]> {
  target: string; targetKind: string; field: string | null; totals: Record<ImpactList, number>; truncated: boolean;
}

const LISTS: [ImpactList, NodeKind][] = [
  ['repos', 'repo'], ['projects', 'project'], ['endpoints', 'endpoint'], ['clientMethods', 'clientMethod'], ['callSites', 'callSite'], ['models', 'model'],
];

/** impact_of_change target resolution: resolveAny, else "Model.Field" when the model has that field. */
export function resolveImpactTarget(ix: GraphIndex, target: string): { start: string; field: string | null } {
  try {
    return { start: ix.resolveAny(target), field: null };
  } catch (e) {
    if (!(e instanceof QueryError) || e.code !== 'not_found' || !target.includes('.')) throw e;
    const dot = target.lastIndexOf('.');
    const start = ix.resolveModel(target.slice(0, dot));
    const field = target.slice(dot + 1);
    if (!ix.models.get(start)!.fields.some(f => f.name === field)) throw ix.notFound(target);
    return { start, field };
  }
}

function projectIdOf(ix: GraphIndex, id: string): string | undefined {
  return ix.endpoints.get(id)?.projectId ?? ix.clientMethods.get(id)?.projectId ?? ix.callSites.get(id)?.projectId ?? ix.models.get(id)?.projectId;
}

/** Everything affected by changing `target`, uncapped (the map marks every affected node). */
export function computeImpact(ix: GraphIndex, target: string): ImpactResult {
  const { start, field } = resolveImpactTarget(ix, target);
  const best = relax(ix, start, () => true);
  best.delete(start);
  const rolled = new Map(best);
  const bump = (id: string, c: Confidence) => {
    const old = rolled.get(id);
    if (old === undefined || confidenceRank(c) > confidenceRank(old)) rolled.set(id, c);
  };
  for (const [id, c] of best) {
    const n = ix.tryGet(id);
    if (!n) continue;
    const projectId = projectIdOf(ix, id);
    if (projectId !== undefined && projectId !== start) bump(projectId, c);
    if (n.repo && `repo:${n.repo}` !== start) bump(`repo:${n.repo}`, c);
  }
  const lists = {} as Record<ImpactList, Affected[]>;
  const totals = {} as Record<ImpactList, number>;
  for (const [list, kind] of LISTS) {
    lists[list] = [...rolled]
      .flatMap(([id, c]) => { const n = ix.tryGet(id); return n && n.kind === kind ? [{ id, label: n.label, repo: n.repo ?? '', confidence: c }] : []; })
      .sort((a, b) => ordinal(a.id, b.id));
    totals[list] = lists[list].length;
  }
  return { target: start, targetKind: field !== null ? 'field' : ix.get(start).kind, field, ...lists, totals, truncated: false };
}

/** impact_of_change with the MCP cap applied per list (what the conformance cases test). */
export function impactOfChange(ix: GraphIndex, target: string, limit?: number): ImpactResult {
  const full = computeImpact(ix, target);
  const c = cap(limit);
  const capped = { ...full };
  for (const [list] of LISTS) capped[list] = full[list].slice(0, c);
  capped.truncated = LISTS.some(([list]) => full[list].length > c);
  return capped;
}

/** Endpoints that accept or return the model, directly or nested inside another model. */
export function endpointsUsingModel(ix: GraphIndex, modelId: string): string[] {
  const containers = relax(ix, modelId, h => h.kind === 'fieldOf');
  const out = new Set<string>();
  for (const m of containers.keys())
    for (const h of ix.dependentsOf(m))
      if ((h.kind === 'accepts' || h.kind === 'returns') && ix.endpoints.has(h.from)) out.add(h.from);
  return [...out].sort(ordinal);
}
```

- [ ] **Step 4: Run to verify they pass**

Run: `cd web && npx vitest run && npm run typecheck`
Expected: PASS. All 15 conformance cases agree with C#.

- [ ] **Step 5: Commit**

```bash
git add web/src/graph web/test
git commit -m "feat(web): TypeScript GraphIndex and queries, passing the shared C# conformance cases

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 5: View models: repo, project and endpoint depth, filters, scale fallback, repo colours

**Files:**
- Create: `web/src/graph/colors.ts`, `web/src/graph/views.ts`
- Test: `web/test/colors.test.ts`, `web/test/views.test.ts`

**Interfaces:**
- Consumes: `GraphIndex`, `ordinal`, `endpointsUsingModel` (Task 4).
- Produces (`colors.ts`):
  - `REPO_PALETTE_SIZE = 8`
  - `fnv1a(s): number`
  - `assignRepoColors(names): Map<string, number>` (slot 0–7)
  - `repoColor(colors, repo): string` → `'var(--repo-N)'`
- Produces (`views.ts`):
  - `type Depth = 'repo' | 'project' | 'endpoint'`
  - `interface Filters { api; client; library; test; thirdParty: boolean; minConfidence: Confidence }`, with `DEFAULT_FILTERS` (test and thirdParty off, minConfidence `'low'`)
  - `type ViewNodeKind = 'repo' | 'project' | 'package' | 'endpoint' | 'clientMethod' | 'callSite'`
  - `interface ViewNode { id; kind: ViewNodeKind; label; repo: string | null; parent?: string; meta?: string; tag?: string; verb?: string; badge?: { text; tone: 'warn' | 'bad' }; column?: number }`
  - `interface ViewContainer { id; label; repo }`
  - `interface ViewEdge { id; from; to; label?: string; weight: number; tone: 'normal' | 'warn'; dashed: boolean; confidence: Confidence }`
  - `interface View { depth; nodes; containers; edges; notice?: string }`
  - `MAX_VISIBLE_NODES = 300`
  - `buildView(ix, depth, focus, filters = DEFAULT_FILTERS, maxNodes = MAX_VISIBLE_NODES): View`
  - Edge ids are `${from}>${to}`. Container ids are repo ids (`repo:<name>`).

- [ ] **Step 1: Write the failing tests**

`web/test/colors.test.ts`:

```ts
import { expect, test } from 'vitest';
import { assignRepoColors, fnv1a, repoColor } from '../src/graph/colors';

test('fnv1a matches the reference value', () => expect(fnv1a('a')).toBe(0xe40c292c));

test('up to 8 repos get distinct, deterministic slots', () => {
  const names = ['billing', 'customers', 'gateway', 'orders', 'shared', 'audit', 'search', 'web'];
  const a = assignRepoColors(names), b = assignRepoColors([...names].reverse());
  expect(new Set(a.values()).size).toBe(8);
  expect([...a.entries()].sort()).toEqual([...b.entries()].sort());
  expect(repoColor(a, 'orders')).toBe(`var(--repo-${a.get('orders')})`);
});

test('more than 8 repos reuse slots by hash', () => {
  const names = Array.from({ length: 20 }, (_, i) => `svc${i}`);
  const a = assignRepoColors(names);
  expect(a.size).toBe(20);
  expect([...a.values()].every(v => v >= 0 && v < 8)).toBe(true);
});
```

`web/test/views.test.ts`:

```ts
import { describe, expect, test } from 'vitest';
import { GraphIndex } from '../src/graph/graphIndex';
import { buildView, DEFAULT_FILTERS } from '../src/graph/views';
import { fixtureGraph } from './util/fixture';

const ix = new GraphIndex(fixtureGraph());
const edge = (v: ReturnType<typeof buildView>, id: string) => v.edges.find(e => e.id === id);

describe('repo depth', () => {
  const v = buildView(ix, 'repo', null);
  test('one node per repo, one edge per dependsOn', () => {
    expect(v.nodes.map(n => n.id)).toEqual(['repo:billing', 'repo:customers', 'repo:gateway', 'repo:orders', 'repo:shared']);
    expect(v.edges).toHaveLength(6);
  });
  test('labels, weights, cycle tone and dashed never-called edges', () => {
    expect(edge(v, 'repo:gateway>repo:orders')).toMatchObject({ label: 'Orders.Client · 2', weight: 2, tone: 'normal', dashed: false });
    expect(edge(v, 'repo:billing>repo:orders')?.tone).toBe('warn');
    expect(edge(v, 'repo:orders>repo:billing')?.tone).toBe('warn');
    expect(edge(v, 'repo:orders>repo:customers')?.dashed).toBe(true);
  });
  test('meta and warning badges (info diagnostics do not count)', () => {
    const n = (id: string) => v.nodes.find(x => x.id === id)!;
    expect(n('repo:orders').meta).toBe('3 projects · 2 in · 3 out');
    expect(n('repo:billing').badge).toEqual({ text: '2 ⚠', tone: 'warn' });
    expect(n('repo:customers').badge).toEqual({ text: '1 ⚠', tone: 'warn' });
    expect(n('repo:gateway').badge).toBeUndefined();
  });
});

describe('project depth', () => {
  test('repo containers hold their projects; tests hidden by default', () => {
    const v = buildView(ix, 'project', null);
    expect(v.containers.map(c => c.id)).toEqual(['repo:billing', 'repo:customers', 'repo:gateway', 'repo:orders', 'repo:shared']);
    expect(v.nodes.find(n => n.id === 'proj:orders/Orders.Tests')).toBeUndefined();
    expect(v.nodes.find(n => n.id === 'proj:orders/Orders.Client')).toMatchObject({ parent: 'repo:orders', tag: 'CLIENT · NUGET 3.4.1' });
  });
  test('edges go consumer project → producing project with version and call count; drift is warn', () => {
    const v = buildView(ix, 'project', null);
    expect(edge(v, 'proj:gateway/Gateway.Api>proj:orders/Orders.Client')).toMatchObject({ label: '3.4.1 · 2 calls', tone: 'normal' });
    expect(edge(v, 'proj:billing/Billing.Api>proj:orders/Orders.Client')).toMatchObject({ label: '3.2.0 · 1 call', tone: 'warn' });
    expect(v.nodes.find(n => n.id === 'proj:billing/Billing.Api')?.badge).toEqual({ text: 'drift', tone: 'warn' });
  });
  test('third-party packages appear when enabled', () => {
    const v = buildView(ix, 'project', null, { ...DEFAULT_FILTERS, thirdParty: true });
    expect(v.nodes.find(n => n.id === 'pkg:Refit')).toMatchObject({ kind: 'package', label: 'Refit' });
    expect(edge(v, 'proj:billing/Billing.Client>pkg:Refit')?.label).toBe('7.2.1');
  });
  test('too many projects: scope to the focused repo and its neighbours, else fall back to repos', () => {
    const scoped = buildView(ix, 'project', 'repo:orders', DEFAULT_FILTERS, 7);
    expect(scoped.depth).toBe('project');
    expect(scoped.nodes).toHaveLength(7);
    expect(scoped.notice).toContain('orders');
    const fallback = buildView(ix, 'project', null, DEFAULT_FILTERS, 7);
    expect(fallback.depth).toBe('repo');
    expect(fallback.notice).toContain('8 projects');
  });
});

describe('endpoint depth', () => {
  test('call sites → client methods → endpoint, in columns', () => {
    const v = buildView(ix, 'endpoint', 'ep:orders:GET:/api/orders/{id}');
    const col = (id: string) => v.nodes.find(n => n.id === id)?.column;
    expect(v.nodes).toHaveLength(4);
    expect(col('cs:billing/Billing.Api:InvoiceBuilder.BuildAsync:10')).toBe(0);
    expect(col('cm:Orders.Client:IOrdersClient.GetOrderAsync')).toBe(1);
    expect(col('cm:Orders.Client:OrdersClient.GetOrderAsync')).toBe(1);
    expect(v.nodes.find(n => n.id === 'ep:orders:GET:/api/orders/{id}')).toMatchObject({ column: 2, verb: 'GET', label: '/api/orders/{id}' });
    expect(v.edges.map(e => e.label).sort()).toEqual(['high', 'high', 'medium']);
  });
  test('min confidence drops weaker links', () => {
    const v = buildView(ix, 'endpoint', 'ep:orders:GET:/api/orders/{id}', { ...DEFAULT_FILTERS, minConfidence: 'high' });
    expect(v.edges.every(e => e.confidence !== 'medium')).toBe(true);
  });
  test('a model focus shows every endpoint that carries it', () => {
    const v = buildView(ix, 'endpoint', 'model:Shared.Kernel:Acme.Shared.Money');
    expect(v.nodes.filter(n => n.kind === 'endpoint')).toHaveLength(3);
  });
  test('no focus explains what to do', () => {
    const v = buildView(ix, 'endpoint', null);
    expect(v.nodes).toEqual([]);
    expect(v.notice).toMatch(/Select an endpoint/);
  });
});
```

- [ ] **Step 2: Run to verify failure**

Run: `cd web && npx vitest run test/colors.test.ts test/views.test.ts`
Expected: FAIL, because the modules don't exist yet.

- [ ] **Step 3: Implement**

`web/src/graph/colors.ts`:

```ts
import { ordinal } from './graphIndex';

export const REPO_PALETTE_SIZE = 8;

/** 32-bit FNV-1a over UTF-16 code units. */
export function fnv1a(s: string): number {
  let h = 0x811c9dc5;
  for (let i = 0; i < s.length; i++) {
    h ^= s.charCodeAt(i);
    h = Math.imul(h, 0x01000193) >>> 0;
  }
  return h >>> 0;
}

/** Stable palette slot per repo: hash, then linear probing while free slots remain; beyond 8 repos slots repeat. */
export function assignRepoColors(names: readonly string[]): Map<string, number> {
  const used = new Set<number>();
  const out = new Map<string, number>();
  for (const name of [...new Set(names)].sort(ordinal)) {
    let slot = fnv1a(name) % REPO_PALETTE_SIZE;
    if (used.size < REPO_PALETTE_SIZE) while (used.has(slot)) slot = (slot + 1) % REPO_PALETTE_SIZE;
    used.add(slot);
    out.set(name, slot);
  }
  return out;
}

export const repoColor = (colors: Map<string, number>, repo: string): string => `var(--repo-${colors.get(repo) ?? 0})`;
```

`web/src/graph/views.ts`:

```ts
import { GraphIndex, ordinal } from './graphIndex';
import { endpointsUsingModel } from './queries';
import { confidenceRank, type Confidence, type ProjectKind, type ProjectNode } from './types';

export type Depth = 'repo' | 'project' | 'endpoint';
export interface Filters { api: boolean; client: boolean; library: boolean; test: boolean; thirdParty: boolean; minConfidence: Confidence }
export const DEFAULT_FILTERS: Filters = { api: true, client: true, library: true, test: false, thirdParty: false, minConfidence: 'low' };

export type ViewNodeKind = 'repo' | 'project' | 'package' | 'endpoint' | 'clientMethod' | 'callSite';
export interface Badge { text: string; tone: 'warn' | 'bad' }
export interface ViewNode {
  id: string; kind: ViewNodeKind; label: string; repo: string | null;
  parent?: string; meta?: string; tag?: string; verb?: string; badge?: Badge; column?: number;
}
export interface ViewContainer { id: string; label: string; repo: string }
export interface ViewEdge { id: string; from: string; to: string; label?: string; weight: number; tone: 'normal' | 'warn'; dashed: boolean; confidence: Confidence }
export interface View { depth: Depth; nodes: ViewNode[]; containers: ViewContainer[]; edges: ViewEdge[]; notice?: string }

export const MAX_VISIBLE_NODES = 300;

const plural = (n: number, word: string) => `${n} ${word}${n === 1 ? '' : 's'}`;
const passes = (c: Confidence, f: Filters) => confidenceRank(c) >= confidenceRank(f.minConfidence);
const weak = (c: Confidence) => confidenceRank(c) < confidenceRank('high');

export function buildView(ix: GraphIndex, depth: Depth, focus: string | null, filters: Filters = DEFAULT_FILTERS, maxNodes = MAX_VISIBLE_NODES): View {
  if (depth === 'endpoint') {
    const v = endpointView(ix, focus, filters);
    if (v.nodes.length <= maxNodes) return v;
    return { ...buildView(ix, 'project', focus, filters, maxNodes), notice: `Showing projects — ${v.nodes.length} call-chain nodes is too many to draw; select a single endpoint or client method` };
  }
  if (depth === 'project') {
    const all = projectView(ix, filters, null);
    if (all.nodes.length <= maxNodes) return all;
    const repo = focus ? ix.tryGet(focus)?.repo ?? null : null;
    if (repo) {
      const scoped = projectView(ix, filters, repo);
      if (scoped.nodes.length <= maxNodes)
        return { ...scoped, notice: `Showing ${repo} and its direct neighbours — the workspace has ${all.nodes.length} projects` };
    }
    return { ...repoView(ix, filters), notice: `Showing repos — ${all.nodes.length} projects is too many to draw; double-click a repo to drill in` };
  }
  return repoView(ix, filters);
}

/** Warning/error diagnostics per repo name (a diagnostic counts once per repo it touches). */
function diagnosticsByRepo(ix: GraphIndex): Map<string, { count: number; error: boolean }> {
  const out = new Map<string, { count: number; error: boolean }>();
  for (const d of ix.graph.diagnostics) {
    if (d.severity === 'info') continue;
    const repos = new Set(d.nodeIds.map(id => ix.tryGet(id)?.repo).filter((r): r is string => !!r));
    for (const r of repos) {
      const cur = out.get(r) ?? { count: 0, error: false };
      out.set(r, { count: cur.count + 1, error: cur.error || d.severity === 'error' });
    }
  }
  return out;
}

function repoView(ix: GraphIndex, f: Filters): View {
  const diags = diagnosticsByRepo(ix);
  const projectsPerRepo = new Map<string, number>();
  for (const p of ix.projects.values()) projectsPerRepo.set(p.repo, (projectsPerRepo.get(p.repo) ?? 0) + 1);
  const nodes: ViewNode[] = [...ix.repos.values()].sort((a, b) => ordinal(a.id, b.id)).map(r => {
    const outs = ix.dependenciesOf(r.id).filter(h => h.kind === 'dependsOn').length;
    const ins = ix.dependentsOf(r.id).filter(h => h.kind === 'dependsOn').length;
    const d = diags.get(r.name);
    return {
      id: r.id, kind: 'repo', label: r.name, repo: r.name,
      meta: `${plural(projectsPerRepo.get(r.name) ?? 0, 'project')} · ${ins} in · ${outs} out`,
      badge: d ? { text: `${d.count} ⚠`, tone: d.error ? 'bad' : 'warn' } : undefined,
    };
  });
  const cycles = ix.graph.diagnostics.filter(d => d.kind === 'cycle').map(d => new Set(d.nodeIds));
  const edges: ViewEdge[] = ix.graph.edges.filter(e => e.kind === 'dependsOn' && passes(e.confidence, f)).map(e => ({
    id: `${e.from}>${e.to}`, from: e.from, to: e.to,
    label: `${(e.viaPackages ?? []).join(', ')} · ${e.callCount ?? 0}`,
    weight: e.callCount ?? 0,
    tone: cycles.some(s => s.has(e.from) && s.has(e.to)) ? 'warn' : 'normal',
    dashed: (e.callCount ?? 0) === 0 || weak(e.confidence),
    confidence: e.confidence,
  }));
  return { depth: 'repo', nodes, containers: [], edges };
}

function projectTag(ix: GraphIndex, p: ProjectNode): string {
  switch (p.kind) {
    case 'api': return 'API';
    case 'client': {
      const version = ix.dependentsOf(p.id).find(h => h.kind === 'produces')?.edge.version;
      return `CLIENT · NUGET${version ? ' ' + version : ''}`;
    }
    case 'library': return 'LIBRARY';
    case 'test': return 'TEST';
    default: return 'PROJECT';
  }
}

function kindVisible(k: ProjectKind, f: Filters): boolean {
  return k === 'api' ? f.api : k === 'client' ? f.client : k === 'library' ? f.library : k === 'test' ? f.test : true;
}

function projectView(ix: GraphIndex, f: Filters, scopeRepo: string | null): View {
  const visible = new Map([...ix.projects.values()].filter(p => kindVisible(p.kind, f)).map(p => [p.id, p]));
  const drift = new Set<string>(), driftProjects = new Set<string>();
  for (const d of ix.graph.diagnostics.filter(x => x.kind === 'versionDrift')) {
    const pkg = d.nodeIds.find(id => id.startsWith('pkg:'));
    for (const id of d.nodeIds) if (id.startsWith('proj:')) { driftProjects.add(id); if (pkg) drift.add(`${id}|${pkg}`); }
  }
  const calls = new Map<string, number>();
  for (const e of ix.graph.edges) {
    if (e.kind !== 'invokes') continue;
    const cs = ix.callSites.get(e.from), cm = ix.clientMethods.get(e.to);
    if (cs && cm) calls.set(`${cs.projectId}>${cm.projectId}`, (calls.get(`${cs.projectId}>${cm.projectId}`) ?? 0) + 1);
  }

  const edges: ViewEdge[] = [];
  const packages = new Map<string, ViewNode>();
  const seen = new Set<string>();
  for (const e of ix.graph.edges) {
    if (e.kind !== 'references' || !visible.has(e.from) || !passes(e.confidence, f)) continue;
    const pkg = ix.packages.get(e.to);
    if (!pkg) continue;
    const producer = pkg.producerProjectIds[0];
    let to: string, label: string, weight = 0;
    if (producer) {
      if (!visible.has(producer) || producer === e.from) continue;
      weight = calls.get(`${e.from}>${producer}`) ?? 0;
      to = producer;
      label = `${e.version ?? '?'} · ${plural(weight, 'call')}`;
    } else {
      if (!f.thirdParty) continue;
      to = pkg.id;
      label = e.version ?? '';
      if (!packages.has(pkg.id)) packages.set(pkg.id, { id: pkg.id, kind: 'package', label: pkg.packageId, repo: null, tag: 'NUGET' });
    }
    const id = `${e.from}>${to}`;
    if (seen.has(id)) continue;
    seen.add(id);
    edges.push({ id, from: e.from, to, label, weight, confidence: e.confidence,
      tone: drift.has(`${e.from}|${pkg.id}`) ? 'warn' : 'normal', dashed: producer !== undefined && weight === 0 });
  }

  let keep = new Set([...visible.keys(), ...packages.keys()]);
  if (scopeRepo) {
    const inRepo = new Set([...visible.values()].filter(p => p.repo === scopeRepo).map(p => p.id));
    keep = new Set(inRepo);
    for (const e of edges) if (inRepo.has(e.from) || inRepo.has(e.to)) { keep.add(e.from); keep.add(e.to); }
  }
  const nodes: ViewNode[] = [
    ...[...visible.values()].filter(p => keep.has(p.id)).sort((a, b) => ordinal(a.id, b.id)).map((p): ViewNode => ({
      id: p.id, kind: 'project', label: p.name, repo: p.repo, parent: `repo:${p.repo}`, tag: projectTag(ix, p),
      badge: driftProjects.has(p.id) ? { text: 'drift', tone: 'warn' } : undefined,
    })),
    ...[...packages.values()].filter(n => keep.has(n.id)).sort((a, b) => ordinal(a.id, b.id)),
  ];
  const repos = [...new Set(nodes.filter(n => n.parent).map(n => n.repo!))].sort(ordinal);
  return {
    depth: 'project', nodes,
    containers: repos.map(r => ({ id: `repo:${r}`, label: r, repo: r })),
    edges: edges.filter(e => keep.has(e.from) && keep.has(e.to)),
  };
}

const NO_FOCUS = 'Select an endpoint, client method, project or repo, then double-click it to open its call chain';

function endpointView(ix: GraphIndex, focus: string | null, f: Filters): View {
  const empty = (notice: string): View => ({ depth: 'endpoint', nodes: [], containers: [], edges: [], notice });
  const n = focus ? ix.tryGet(focus) : undefined;
  if (!n) return empty(NO_FOCUS);

  const targets = (cm: string) => ix.dependenciesOf(cm).filter(h => h.kind === 'targets').map(h => h.to);
  let endpoints: string[] = [];
  let cms: string[] = [];
  switch (n.kind) {
    case 'endpoint': endpoints = [n.id]; break;
    case 'clientMethod': cms = [n.id]; endpoints = targets(n.id); break;
    case 'callSite': cms = ix.dependenciesOf(n.id).filter(h => h.kind === 'invokes').map(h => h.to); endpoints = cms.flatMap(targets); break;
    case 'project':
      endpoints = [...ix.endpoints.values()].filter(e => e.projectId === n.id).map(e => e.id);
      cms = [...ix.clientMethods.values()].filter(c => c.projectId === n.id).map(c => c.id);
      break;
    case 'package': {
      const producers = new Set(ix.packages.get(n.id)?.producerProjectIds ?? []);
      cms = [...ix.clientMethods.values()].filter(c => producers.has(c.projectId)).map(c => c.id);
      endpoints = cms.flatMap(targets);
      break;
    }
    case 'repo':
      endpoints = [...ix.endpoints.values()].filter(e => e.repo === n.label).map(e => e.id);
      cms = [...ix.clientMethods.values()].filter(c => c.repo === n.label).map(c => c.id);
      break;
    case 'model': endpoints = endpointsUsingModel(ix, n.id); break;
  }
  const epSet = new Set(endpoints.filter(id => ix.endpoints.has(id)));
  const cmSet = new Set(cms.filter(id => ix.clientMethods.has(id)));
  for (const ep of epSet) for (const h of ix.dependentsOf(ep)) if (h.kind === 'targets' && ix.clientMethods.has(h.from)) cmSet.add(h.from);
  const csSet = new Set<string>();
  for (const cm of cmSet) for (const h of ix.dependentsOf(cm)) if (h.kind === 'invokes' && ix.callSites.has(h.from)) csSet.add(h.from);
  if (epSet.size + cmSet.size + csSet.size === 0) return empty(`${n.label} has no endpoints or client methods to chain`);

  const projectName = (id: string) => ix.projects.get(id)?.name ?? id;
  const nodes: ViewNode[] = [
    ...[...csSet].sort(ordinal).map((id): ViewNode => {
      const cs = ix.callSites.get(id)!;
      return { id, kind: 'callSite', label: cs.containingMember, repo: cs.repo, column: 0, tag: `CALL SITE · ${cs.repo}`, meta: `${projectName(cs.projectId)} · line ${cs.location.line}` };
    }),
    ...[...cmSet].sort(ordinal).map((id): ViewNode => {
      const cm = ix.clientMethods.get(id)!;
      return { id, kind: 'clientMethod', label: `${cm.typeName}.${cm.methodName}`, repo: cm.repo, column: 1, tag: `CLIENT METHOD · ${projectName(cm.projectId)}`, meta: cm.strategy };
    }),
    ...[...epSet].sort(ordinal).map((id): ViewNode => {
      const ep = ix.endpoints.get(id)!;
      return { id, kind: 'endpoint', label: ep.route, verb: ep.verb.toUpperCase(), repo: ep.repo, column: 2, meta: ep.handler };
    }),
  ];
  const inView = new Set(nodes.map(x => x.id));
  const edges: ViewEdge[] = ix.graph.edges
    .filter(e => (e.kind === 'invokes' || e.kind === 'targets') && inView.has(e.from) && inView.has(e.to) && passes(e.confidence, f))
    .map(e => ({ id: `${e.from}>${e.to}`, from: e.from, to: e.to, label: e.confidence, weight: 1, tone: 'normal' as const, dashed: weak(e.confidence), confidence: e.confidence }));
  return { depth: 'endpoint', nodes, containers: [], edges };
}
```

- [ ] **Step 4: Run to verify they pass**

Run: `cd web && npx vitest run && npm run typecheck`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add web/src/graph web/test
git commit -m "feat(web): repo/project/endpoint view models with filters, scale fallback and stable repo colours

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 6: Layout: ELK input per depth, positions, Web Worker engine

**Files:**
- Create: `web/src/layout/elkInput.ts`, `web/src/layout/engine.ts`, `web/src/layout/layout.worker.ts`, `web/test/util/inThreadEngine.ts`
- Test: `web/test/layout.test.ts`

**Interfaces:**
- Consumes: `View`, `ViewNode`, `ViewNodeKind` (Task 5).
- Produces:
  - `NODE_SIZE: Record<ViewNodeKind, { width; height }>`
  - `toElkGraph(view): ElkNode`
  - `interface Box { x; y; width; height }`
  - `interface Positions { nodes: Record<string, Box> }`, where a child's x/y are **relative to its container**, the React Flow `parentId` convention
  - `readPositions(laid: ElkNode): Positions`
  - `interface LayoutEngine { layout(graph: ElkNode): Promise<ElkNode>; dispose(): void }`
  - `createWorkerEngine(): LayoutEngine`
  - `layoutView(engine, view): Promise<Positions>`
  - test-only `inThreadEngine(): LayoutEngine`

- [ ] **Step 1: Write the failing test**

`web/test/util/inThreadEngine.ts`:

```ts
import ELK from 'elkjs/lib/elk.bundled.js';
import type { LayoutEngine } from '../../src/layout/engine';

/** Same elkjs, no worker: jsdom has no Worker, and tests want determinism. */
export function inThreadEngine(): LayoutEngine {
  const elk = new ELK();
  return { layout: g => elk.layout(g), dispose() {} };
}
```

`web/test/layout.test.ts`:

```ts
import { describe, expect, test } from 'vitest';
import { GraphIndex } from '../src/graph/graphIndex';
import { buildView, DEFAULT_FILTERS } from '../src/graph/views';
import { layoutView, NODE_SIZE, type Box } from '../src/layout/engine';
import { toElkGraph } from '../src/layout/elkInput';
import { fixtureGraph } from './util/fixture';
import { inThreadEngine } from './util/inThreadEngine';

const ix = new GraphIndex(fixtureGraph());
const overlap = (a: Box, b: Box) => a.x < b.x + b.width && b.x < a.x + a.width && a.y < b.y + b.height && b.y < a.y + a.height;

describe('toElkGraph', () => {
  test('project depth nests projects inside repo containers', () => {
    const g = toElkGraph(buildView(ix, 'project', null));
    const orders = g.children!.find(c => c.id === 'repo:orders')!;
    expect(orders.children!.map(c => c.id).sort()).toEqual(['proj:orders/Orders.Api', 'proj:orders/Orders.Client']);
    expect(g.layoutOptions!['elk.hierarchyHandling']).toBe('INCLUDE_CHILDREN');
  });
  test('endpoint depth partitions by column', () => {
    const g = toElkGraph(buildView(ix, 'endpoint', 'ep:orders:GET:/api/orders/{id}'));
    expect(g.layoutOptions!['elk.partitioning.activate']).toBe('true');
    expect(g.children!.find(c => c.id === 'ep:orders:GET:/api/orders/{id}')!.layoutOptions!['elk.partitioning.partition']).toBe('2');
  });
});

describe('layoutView', () => {
  test('repo depth: every node placed, no overlaps, dependents left of dependencies', async () => {
    const p = await layoutView(inThreadEngine(), buildView(ix, 'repo', null));
    const ids = Object.keys(p.nodes).sort();
    expect(ids).toEqual(['repo:billing', 'repo:customers', 'repo:gateway', 'repo:orders', 'repo:shared']);
    expect(p.nodes['repo:orders']).toMatchObject(NODE_SIZE.repo);
    for (const a of ids) for (const b of ids) if (a < b) expect(overlap(p.nodes[a]!, p.nodes[b]!)).toBe(false);
    expect(p.nodes['repo:gateway']!.x).toBeLessThan(p.nodes['repo:orders']!.x);
    expect(p.nodes['repo:orders']!.x).toBeLessThan(p.nodes['repo:shared']!.x);
  });
  test('project depth: children positioned inside their container', async () => {
    const p = await layoutView(inThreadEngine(), buildView(ix, 'project', null, DEFAULT_FILTERS));
    const box = p.nodes['repo:orders']!, child = p.nodes['proj:orders/Orders.Api']!;
    expect(child.x).toBeGreaterThanOrEqual(0);
    expect(child.x + child.width).toBeLessThanOrEqual(box.width);
    expect(child.y + child.height).toBeLessThanOrEqual(box.height);
  });
  test('endpoint depth: call site, client method, endpoint left to right', async () => {
    const p = await layoutView(inThreadEngine(), buildView(ix, 'endpoint', 'ep:orders:GET:/api/orders/{id}'));
    const x = (id: string) => p.nodes[id]!.x;
    expect(x('cs:billing/Billing.Api:InvoiceBuilder.BuildAsync:10')).toBeLessThan(x('cm:Orders.Client:IOrdersClient.GetOrderAsync'));
    expect(x('cm:Orders.Client:IOrdersClient.GetOrderAsync')).toBeLessThan(x('ep:orders:GET:/api/orders/{id}'));
  });
  test('an empty view lays out to no positions', async () => {
    expect((await layoutView(inThreadEngine(), buildView(ix, 'endpoint', null))).nodes).toEqual({});
  });
});
```

- [ ] **Step 2: Run to verify failure**

Run: `cd web && npx vitest run test/layout.test.ts`
Expected: FAIL, because `../src/layout/engine` cannot be resolved.

- [ ] **Step 3: Implement**

`web/src/layout/elkInput.ts`:

```ts
import type { ElkExtendedEdge, ElkNode } from 'elkjs/lib/elk-api';
import type { View, ViewNode, ViewNodeKind } from '../graph/views';

export const NODE_SIZE: Record<ViewNodeKind, { width: number; height: number }> = {
  repo: { width: 210, height: 66 },
  project: { width: 190, height: 56 },
  package: { width: 170, height: 44 },
  endpoint: { width: 270, height: 68 },
  clientMethod: { width: 250, height: 62 },
  callSite: { width: 250, height: 62 },
};

const BASE: Record<string, string> = {
  'elk.algorithm': 'layered',
  'elk.direction': 'RIGHT',
  'elk.layered.spacing.nodeNodeBetweenLayers': '110',
  'elk.spacing.nodeNode': '56',
  'elk.layered.crossingMinimization.strategy': 'LAYER_SWEEP',
  'elk.layered.nodePlacement.strategy': 'BRANDES_KOEPF',
};

const sized = (n: ViewNode): ElkNode => ({
  id: n.id, ...NODE_SIZE[n.kind],
  ...(n.column !== undefined ? { layoutOptions: { 'elk.partitioning.partition': String(n.column) } } : {}),
});

const toEdge = (e: { id: string; from: string; to: string }): ElkExtendedEdge => ({ id: e.id, sources: [e.from], targets: [e.to] });

/** Layered, left to right: dependents on the left, dependencies on the right. */
export function toElkGraph(view: View): ElkNode {
  if (view.depth === 'project') {
    const containers: ElkNode[] = view.containers.map(c => ({
      id: c.id,
      layoutOptions: { 'elk.padding': '[top=40,left=18,bottom=18,right=18]', 'elk.spacing.nodeNode': '30' },
      children: view.nodes.filter(n => n.parent === c.id).map(sized),
    }));
    return {
      id: 'root',
      layoutOptions: { ...BASE, 'elk.hierarchyHandling': 'INCLUDE_CHILDREN', 'elk.spacing.nodeNode': '40' },
      children: [...containers, ...view.nodes.filter(n => !n.parent).map(sized)],
      edges: view.edges.map(toEdge),
    };
  }
  const options = view.depth === 'endpoint' ? { ...BASE, 'elk.partitioning.activate': 'true' } : BASE;
  return { id: 'root', layoutOptions: options, children: view.nodes.map(sized), edges: view.edges.map(toEdge) };
}
```

`web/src/layout/engine.ts`:

```ts
import type { ElkNode } from 'elkjs/lib/elk-api';
import type { View } from '../graph/views';
import { toElkGraph } from './elkInput';

export { NODE_SIZE } from './elkInput';

export interface Box { x: number; y: number; width: number; height: number }
/** Children are relative to their container (React Flow's parentId convention); top-level boxes are absolute. */
export interface Positions { nodes: Record<string, Box> }

export interface LayoutEngine { layout(graph: ElkNode): Promise<ElkNode>; dispose(): void }

export function readPositions(root: ElkNode): Positions {
  const nodes: Record<string, Box> = {};
  const walk = (n: ElkNode) => {
    for (const c of n.children ?? []) {
      nodes[c.id] = { x: c.x ?? 0, y: c.y ?? 0, width: c.width ?? 0, height: c.height ?? 0 };
      walk(c);
    }
  };
  walk(root);
  return { nodes };
}

export async function layoutView(engine: LayoutEngine, view: View): Promise<Positions> {
  if (view.nodes.length === 0) return { nodes: {} };
  return readPositions(await engine.layout(toElkGraph(view)));
}

/** elkjs in a Web Worker, so layout never blocks the main thread. The worker is inlined into the single-file bundle. */
export function createWorkerEngine(): LayoutEngine {
  let worker: Worker | null = null;
  let seq = 0;
  const pending = new Map<number, { resolve: (g: ElkNode) => void; reject: (e: Error) => void }>();
  const ensure = async (): Promise<Worker> => {
    if (worker) return worker;
    const { default: LayoutWorker } = await import('./layout.worker?worker&inline');
    const w: Worker = new LayoutWorker();
    w.onmessage = (e: MessageEvent<{ id: number; graph?: ElkNode; error?: string }>) => {
      const p = pending.get(e.data.id);
      if (!p) return;
      pending.delete(e.data.id);
      if (e.data.graph) p.resolve(e.data.graph); else p.reject(new Error(e.data.error ?? 'layout failed'));
    };
    worker = w;
    return w;
  };
  return {
    async layout(graph) {
      const w = await ensure();
      const id = ++seq;
      return new Promise<ElkNode>((resolve, reject) => {
        pending.set(id, { resolve, reject });
        w.postMessage({ id, graph });
      });
    },
    dispose() {
      worker?.terminate();
      worker = null;
      for (const p of pending.values()) p.reject(new Error('layout engine disposed'));
      pending.clear();
    },
  };
}
```

`web/src/layout/layout.worker.ts`:

```ts
import ELK from 'elkjs/lib/elk.bundled.js';
import type { ElkNode } from 'elkjs/lib/elk-api';

// The DOM lib types `self` as Window; describe the worker scope we actually use.
const scope = self as unknown as { onmessage: ((e: MessageEvent<{ id: number; graph: ElkNode }>) => void) | null; postMessage(m: unknown): void };
const elk = new ELK();

scope.onmessage = async e => {
  const { id, graph } = e.data;
  try {
    scope.postMessage({ id, graph: await elk.layout(graph) });
  } catch (err) {
    scope.postMessage({ id, error: String(err) });
  }
};
```

- [ ] **Step 4: Run to verify they pass, and that the worker bundles**

Run: `cd web && npx vitest run && npm run typecheck`
Expected: PASS. (`npm run build` still bundles the old `main.tsx`; the worker is first bundled in Task 8.)

- [ ] **Step 5: Commit**

```bash
git add web/src/layout web/test
git commit -m "feat(web): elkjs layout per depth (compound repos, column partitions) running in a worker

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 7: App shell: tokens, state, deep links, theme, top bar, sidebar, overview

**Files:**
- Create: `web/src/styles/fonts.ts`, `web/src/styles/tokens.css`, `web/src/styles/app.css`
- Create: `web/src/app/state.ts`, `web/src/app/route.ts`, `web/src/app/persist.ts`, `web/src/app/theme.ts`, `web/src/app/App.tsx`
- Create: `web/src/graph/overview.ts`, `web/src/graph/browse.ts`
- Create: `web/src/panels/types.ts`, `web/src/panels/Segmented.tsx`, `web/src/panels/TopBar.tsx`, `web/src/panels/Sidebar.tsx`, `web/src/panels/Overview.tsx`, `web/src/panels/NotFound.tsx`, `web/src/panels/Inspector.tsx`
- Modify: `web/src/main.tsx`
- Test: `web/test/route.test.ts`, `web/test/state.test.ts`, `web/test/overview.test.ts`, `web/test/App.test.tsx`

**Interfaces:**
- Consumes: `parseGraph`, `readInlineGraph`, `readFocus` (Task 3); `GraphIndex`, `QueryError` (Task 4); `Depth`, `Filters`, `DEFAULT_FILTERS`, `assignRepoColors`, `repoColor` (Task 5); `LayoutEngine`, `createWorkerEngine` (Task 6).
- Produces (`route.ts`):
  - `type Route = { kind: 'home' } | { kind: 'node'; id } | { kind: 'impact'; target }`
  - `encodeId(id)`, `parseHash(hash)`, `formatRoute(route)`
- Produces (`state.ts`):
  - `type Highlight = 'up' | 'down' | 'both'`, `type Theme = 'light' | 'dark'`
  - `interface Place { depth: Depth; focus: string | null }`
  - `interface AppState { depth; focus; selection: string | null; trail: Place[]; impact: string | null; filters; highlight; sidebarCollapsed; theme: Theme | null; paletteOpen; notFound: { query; suggestions } | null }`
  - the `Action` union:
    - `setDepth`, `select`, `drill` (`id`, `kind`), `back`, `focusNode` (`place`, `selection`)
    - `setFilters` (`patch`), `setHighlight`, `toggleSidebar`, `setTheme`
    - `enterImpact` (`target`), `exitImpact`, `setPalette` (`open`), `notFound` (`query`, `suggestions`), `dismissNotFound`
    - `reconcile` (`known: (id) => boolean`, `impactValid: boolean`), used by Task 11
  - `initialState(persisted)`, `reducer`
  - `drillTarget(kind, id): Place | null`
  - `placeFor(ix, id): { place: Place; selection: string }`
  - `breadcrumb(ix, state): { label: string; place: Place | null }[]`
- Produces (`persist.ts`): `load<T>(key, fallback)`, `save(key, value)`, `KEYS = { sidebar, theme, positions(workspace, depth) }`.
- Produces (`theme.ts`): `applyTheme(theme)`, `systemPrefersDark()`, `nextTheme(current)`.
- Produces (`overview.ts`): `overviewData(ix) → { links; repos; packages; callSites; endpoints; attention: Diagnostic[] }`, where attention is sorted error → warning → info, then kind, then message.
- Produces (`browse.ts`):
  - `type BrowseTab = 'repos' | 'endpoints' | 'models' | 'packages'`
  - `browseItems(ix, tab, filter) → { id; label; detail; repo: string | null }[]`
  - `diagnosticGroups(ix) → { kind; severity; items: Diagnostic[] }[]`
- Produces (`panels/types.ts`): `interface PanelProps { ix: GraphIndex; state: AppState; dispatch: Dispatch<Action>; colors: Map<string, number> }`.
- Produces (`App.tsx`):
  - `interface AppProps { graph: DepGraph; engine: LayoutEngine; mode: 'export' | 'serve'; initialFocus?: string | null; status?: 'ok' | 'reconnecting'; bundleHtml?: string }`
  - `function App(props)`, which renders `<main className="stage" data-testid="stage">` (Task 8 fills it)
- `Inspector.tsx` in this task shows the overview, or a header for the selected node. Task 9 replaces the selected-node body.

- [ ] **Step 0: Before screenshot**

Build the current bundle (`cd web && npm run build`) and take a Puppeteer screenshot of `web/dist/index.html` at 1440×900 to show the user later. It shows the placeholder "depenk" text. A minimal script, saved in the scratchpad rather than the repo:

```js
// shot.mjs: node shot.mjs <file.html> <out.png> [hash]
import puppeteer from 'puppeteer';
const [file, out, hash = ''] = process.argv.slice(2);
const b = await puppeteer.launch();
const p = await b.newPage();
await p.setViewport({ width: 1440, height: 900 });
await p.goto(new URL(`file:///${file.replace(/\\/g, '/')}${hash}`).href, { waitUntil: 'networkidle0' });
await new Promise(r => setTimeout(r, 800));
await p.screenshot({ path: out });
await b.close();
```

- [ ] **Step 1: Write the failing tests**

`web/test/route.test.ts`:

```ts
import { expect, test } from 'vitest';
import { encodeId, formatRoute, parseHash } from '../src/app/route';

test('ids keep : and / readable, everything else is percent-encoded', () => {
  expect(encodeId('ep:orders:GET:/api/orders/{id}')).toBe('ep:orders:GET:/api/orders/%7Bid%7D');
  expect(formatRoute({ kind: 'node', id: 'ep:orders:GET:/api/orders/{id}' })).toBe('#/ep:orders:GET:/api/orders/%7Bid%7D');
  expect(formatRoute({ kind: 'impact', target: 'OrderDto.Lines' })).toBe('#/impact/OrderDto.Lines');
  expect(formatRoute({ kind: 'home' })).toBe('#/');
});

test('parse accepts encoded and raw forms', () => {
  expect(parseHash('#/ep:orders:GET:/api/orders/%7Bid%7D')).toEqual({ kind: 'node', id: 'ep:orders:GET:/api/orders/{id}' });
  expect(parseHash('#/ep:orders:GET:/api/orders/{id}')).toEqual({ kind: 'node', id: 'ep:orders:GET:/api/orders/{id}' });
  expect(parseHash('#/impact/GET%20%2Fapi%2Forders')).toEqual({ kind: 'impact', target: 'GET /api/orders' });
  expect(parseHash('')).toEqual({ kind: 'home' });
  expect(parseHash('#/')).toEqual({ kind: 'home' });
  expect(parseHash('#/bad%E0%A4%A')).toEqual({ kind: 'node', id: 'bad%E0%A4%A' }); // malformed escapes are kept verbatim
});
```

`web/test/state.test.ts`:

```ts
import { describe, expect, test } from 'vitest';
import { breadcrumb, initialState, placeFor, reducer, type AppState } from '../src/app/state';
import { GraphIndex } from '../src/graph/graphIndex';
import { fixtureGraph } from './util/fixture';

const ix = new GraphIndex(fixtureGraph());
const s0: AppState = initialState({ sidebarCollapsed: false, theme: null });

describe('reducer', () => {
  test('drill pushes the trail; back pops it', () => {
    const a = reducer(s0, { type: 'drill', id: 'repo:orders', kind: 'repo' });
    expect([a.depth, a.focus, a.selection]).toEqual(['project', 'repo:orders', 'repo:orders']);
    const b = reducer(a, { type: 'drill', id: 'proj:orders/Orders.Api', kind: 'project' });
    expect([b.depth, b.focus]).toEqual(['endpoint', 'proj:orders/Orders.Api']);
    const c = reducer(b, { type: 'back' });
    expect([c.depth, c.focus]).toEqual(['project', 'repo:orders']);
    const d = reducer(reducer(c, { type: 'back' }), { type: 'back' });
    expect([d.depth, d.focus]).toEqual(['repo', null]);
  });
  test('setDepth to repo clears focus and trail', () => {
    const a = reducer(reducer(s0, { type: 'drill', id: 'repo:orders', kind: 'repo' }), { type: 'setDepth', depth: 'repo' });
    expect([a.focus, a.trail]).toEqual([null, []]);
  });
  test('impact mode and palette', () => {
    const a = reducer(reducer(s0, { type: 'setPalette', open: true }), { type: 'enterImpact', target: 'OrderDto.Lines' });
    expect([a.impact, a.paletteOpen]).toEqual(['OrderDto.Lines', false]);
    expect(reducer(a, { type: 'exitImpact' }).impact).toBeNull();
  });
  test('reconcile drops what no longer exists', () => {
    const a = { ...s0, depth: 'endpoint' as const, focus: 'ep:gone', selection: 'repo:orders', impact: 'Gone', trail: [{ depth: 'project' as const, focus: 'repo:gone' }] };
    const b = reducer(a, { type: 'reconcile', known: id => id === 'repo:orders', impactValid: false });
    expect([b.depth, b.focus, b.selection, b.impact, b.trail]).toEqual(['repo', null, 'repo:orders', null, []]);
  });
});

test('placeFor picks the depth that shows the node', () => {
  expect(placeFor(ix, 'repo:orders').place).toEqual({ depth: 'repo', focus: null });
  expect(placeFor(ix, 'proj:orders/Orders.Api').place).toEqual({ depth: 'project', focus: 'repo:orders' });
  expect(placeFor(ix, 'ep:orders:GET:/api/orders/{id}').place).toEqual({ depth: 'endpoint', focus: 'ep:orders:GET:/api/orders/{id}' });
  expect(placeFor(ix, 'model:Orders.Client:Acme.Orders.Client.OrderDto').place.depth).toBe('endpoint');
});

test('breadcrumb follows the focus up to its repo', () => {
  const s = { ...s0, depth: 'endpoint' as const, focus: 'ep:orders:GET:/api/orders/{id}' };
  expect(breadcrumb(ix, s).map(c => c.label)).toEqual(['System', 'orders', 'Orders.Api', 'GET /api/orders/{id}']);
  expect(breadcrumb(ix, s0).map(c => c.label)).toEqual(['System']);
});
```

`web/test/overview.test.ts`:

```ts
import { expect, test } from 'vitest';
import { browseItems, diagnosticGroups } from '../src/graph/browse';
import { GraphIndex } from '../src/graph/graphIndex';
import { overviewData } from '../src/graph/overview';
import { fixtureGraph } from './util/fixture';

const ix = new GraphIndex(fixtureGraph());

test('overview numbers and needs-attention order', () => {
  const o = overviewData(ix);
  expect([o.links, o.repos, o.packages, o.callSites, o.endpoints]).toEqual([6, 5, 4, 5, 7]);
  expect(o.attention.map(d => d.kind)).toEqual(['ambiguousRoute', 'cycle', 'versionDrift', 'parseError', 'unusedEndpoint']);
});

test('browse lists filter case-insensitively and sort by label', () => {
  expect(browseItems(ix, 'repos', '').map(i => i.label)).toEqual(['billing', 'customers', 'gateway', 'orders', 'shared']);
  expect(browseItems(ix, 'endpoints', 'ORDERS/{').map(i => i.id)).toEqual(['ep:orders:DELETE:/api/orders/{id}', 'ep:orders:GET:/api/orders/{id}']);
  expect(browseItems(ix, 'models', 'money')[0]).toMatchObject({ label: 'Money', repo: 'shared' });
  expect(browseItems(ix, 'packages', 'refit')[0]).toMatchObject({ id: 'pkg:Refit', detail: 'third-party · 1 consumer' });
});

test('diagnostic groups, worst first', () => {
  expect(diagnosticGroups(ix).map(g => `${g.kind}:${g.items.length}`))
    .toEqual(['ambiguousRoute:1', 'cycle:1', 'versionDrift:1', 'parseError:1', 'unusedEndpoint:1']);
});
```

`web/test/App.test.tsx`:

```tsx
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { expect, test } from 'vitest';
import { App } from '../src/app/App';
import { fixtureGraph } from './util/fixture';
import { inThreadEngine } from './util/inThreadEngine';

const renderApp = () => render(<App graph={fixtureGraph()} engine={inThreadEngine()} mode="export" />);

test('overview shows the headline and needs-attention list', () => {
  renderApp();
  expect(screen.getByRole('heading', { name: '6 links across 5 repos' })).toBeInTheDocument();
  const rows = within(screen.getByRole('list', { name: 'Needs attention' })).getAllByRole('listitem');
  expect(rows).toHaveLength(5);
  expect(rows[0]).toHaveTextContent('ambiguousRoute');
});

test('depth switch and sidebar collapse persist', async () => {
  renderApp();
  await userEvent.click(screen.getByRole('button', { name: 'Project' }));
  expect(screen.getByRole('button', { name: 'Project' })).toHaveAttribute('aria-pressed', 'true');
  await userEvent.click(screen.getByRole('button', { name: 'Collapse sidebar' }));
  expect(screen.getByRole('button', { name: 'Expand sidebar' })).toBeInTheDocument();
  expect(localStorage.getItem('depenk:sidebar')).toBe('true');
});

test('theme toggle sets data-theme and remembers it', async () => {
  renderApp();
  await userEvent.click(screen.getByRole('button', { name: /theme/i }));
  expect(document.documentElement.dataset.theme).toBe('dark');
  expect(localStorage.getItem('depenk:theme')).toBe('"dark"');
});

test('browse list selects a node and updates the hash', async () => {
  renderApp();
  const sidebar = screen.getByRole('complementary', { name: 'Sidebar' });
  await userEvent.click(within(sidebar).getByRole('button', { name: /^orders/ }));
  expect(screen.getByRole('heading', { level: 2, name: 'orders' })).toBeInTheDocument();
  expect(window.location.hash).toBe('#/repo:orders');
});

test('deep link to a missing node shows suggestions', async () => {
  window.location.hash = '#/repo:order';
  renderApp();
  expect(await screen.findByText(/Not found: repo:order/)).toBeInTheDocument();
  await userEvent.click(screen.getByRole('button', { name: 'repo:orders' }));
  expect(screen.getByRole('heading', { level: 2, name: 'orders' })).toBeInTheDocument();
});

test('initial focus from an export opens on that node', () => {
  render(<App graph={fixtureGraph()} engine={inThreadEngine()} mode="export" initialFocus="repo:billing" />);
  expect(screen.getByRole('heading', { level: 2, name: 'billing' })).toBeInTheDocument();
});
```

- [ ] **Step 2: Run to verify failure**

Run: `cd web && npx vitest run test/route.test.ts test/state.test.ts test/overview.test.ts test/App.test.tsx`
Expected: FAIL, because the modules don't exist yet.

- [ ] **Step 3: Styles and fonts**

`web/src/styles/fonts.ts`:

```ts
// Bundled (OFL) so exports work offline; Vite inlines the font files into the single HTML file.
import '@fontsource/ibm-plex-sans/latin-400.css';
import '@fontsource/ibm-plex-sans/latin-500.css';
import '@fontsource/ibm-plex-sans/latin-600.css';
import '@fontsource/ibm-plex-sans-condensed/latin-600.css';
import '@fontsource/ibm-plex-sans-condensed/latin-700.css';
import '@fontsource/ibm-plex-mono/latin-400.css';
import '@fontsource/ibm-plex-mono/latin-500.css';
```

`web/src/styles/tokens.css`:

```css
:root {
  --bg: #eef1f4; --surface: #ffffff; --surface-2: #f6f8fa; --ink: #16202b; --ink-2: #4a5866; --ink-3: #7c8a97;
  --line: #d5dce3; --accent: #0b6e79; --accent-soft: #d8eef0; --grid: #cfd7df;
  --ok: #1d7a46; --ok-soft: #dcf1e4; --warn: #9a6200; --warn-soft: #fbefd6; --bad: #c0362c; --bad-soft: #fbe3e0;
  --m-get: #2463b0; --m-post: #1d7a46; --m-put: #9a6200; --m-delete: #c0362c; --m-patch: #7045a8; --m-other: #5b6875;
  --repo-0: #0b6e79; --repo-1: #b5502a; --repo-2: #7045a8; --repo-3: #2463b0; --repo-4: #1d7a46; --repo-5: #9a6200; --repo-6: #a8326e; --repo-7: #5b6875;
  --font-display: "IBM Plex Sans Condensed", "Arial Narrow", system-ui, sans-serif;
  --font-body: "IBM Plex Sans", system-ui, -apple-system, "Segoe UI", sans-serif;
  --font-mono: "IBM Plex Mono", ui-monospace, "Cascadia Mono", Consolas, monospace;
  color-scheme: light;
}
@media (prefers-color-scheme: dark) {
  :root:not([data-theme="light"]) {
    --bg: #0e1418; --surface: #151d23; --surface-2: #1a242b; --ink: #e3eaef; --ink-2: #a6b4bf; --ink-3: #718290;
    --line: #2a3740; --accent: #4cc2cc; --accent-soft: #143a3f; --grid: #24323b;
    --ok: #5cc98a; --ok-soft: #15301f; --warn: #e2a93b; --warn-soft: #3a2d12; --bad: #f07a6e; --bad-soft: #3d1c19;
    --m-get: #6aa7ef; --m-post: #5cc98a; --m-put: #e2a93b; --m-delete: #f07a6e; --m-patch: #b393ea; --m-other: #92a1ad;
    --repo-0: #4cc2cc; --repo-1: #e98a5f; --repo-2: #b393ea; --repo-3: #6aa7ef; --repo-4: #5cc98a; --repo-5: #e2a93b; --repo-6: #ee82b6; --repo-7: #92a1ad;
    color-scheme: dark;
  }
}
:root[data-theme="dark"] {
  --bg: #0e1418; --surface: #151d23; --surface-2: #1a242b; --ink: #e3eaef; --ink-2: #a6b4bf; --ink-3: #718290;
  --line: #2a3740; --accent: #4cc2cc; --accent-soft: #143a3f; --grid: #24323b;
  --ok: #5cc98a; --ok-soft: #15301f; --warn: #e2a93b; --warn-soft: #3a2d12; --bad: #f07a6e; --bad-soft: #3d1c19;
  --m-get: #6aa7ef; --m-post: #5cc98a; --m-put: #e2a93b; --m-delete: #f07a6e; --m-patch: #b393ea; --m-other: #92a1ad;
  --repo-0: #4cc2cc; --repo-1: #e98a5f; --repo-2: #b393ea; --repo-3: #6aa7ef; --repo-4: #5cc98a; --repo-5: #e2a93b; --repo-6: #ee82b6; --repo-7: #92a1ad;
  color-scheme: dark;
}
```

`web/src/styles/app.css` (covers every component in this plan, so later tasks only add markup):

```css
* { box-sizing: border-box; }
html, body, #root { height: 100%; margin: 0; }
body { background: var(--bg); color: var(--ink); font: 14px/1.5 var(--font-body); }
button, input { font: inherit; color: inherit; }
:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
code, .mono { font-family: var(--font-mono); font-size: 12px; }

.app { height: 100%; display: grid; grid-template-rows: auto auto 1fr; }
.main { display: grid; grid-template-columns: 260px minmax(0, 1fr) 400px; min-height: 0; }
.app.collapsed .main { grid-template-columns: 48px minmax(0, 1fr) 400px; }

/* top bar */
.bar { display: flex; flex-wrap: wrap; align-items: center; gap: 10px 18px; padding: 10px 16px; background: var(--surface); border-bottom: 1px solid var(--line); }
.brand { display: flex; align-items: baseline; gap: 10px; }
.brand h1 { margin: 0; font: 700 20px/1 var(--font-display); }
.brand small { color: var(--ink-3); font-size: 12px; }
.crumbs { display: flex; gap: 6px; align-items: center; font-size: 13px; color: var(--ink-3); }
.crumbs button { border: 0; background: none; padding: 0; color: var(--ink-3); cursor: pointer; }
.crumbs button:last-child { color: var(--ink); font-weight: 600; }
.search { margin-left: auto; flex: 0 1 300px; display: flex; align-items: center; gap: 8px; padding: 6px 10px; border-radius: 8px; border: 1px solid var(--line); background: var(--surface-2); font: 13px var(--font-mono); color: var(--ink-3); cursor: pointer; white-space: nowrap; }
.search kbd { margin-left: auto; font: 11px var(--font-mono); border: 1px solid var(--line); border-radius: 4px; padding: 0 5px; background: var(--surface); }
.icon-btn { border: 1px solid var(--line); background: var(--surface-2); border-radius: 8px; padding: 5px 9px; cursor: pointer; font-size: 13px; color: var(--ink-2); }
.icon-btn:hover { color: var(--ink); }
.target { display: flex; align-items: center; gap: 8px; padding: 4px 10px; border: 2px solid var(--accent); border-radius: 8px; background: var(--surface); font: 13px var(--font-mono); }
.target span { color: var(--ink-3); font: 12px var(--font-body); }
.target button { border: 0; background: none; cursor: pointer; color: var(--ink-3); }
.stat { display: inline-flex; align-items: baseline; gap: 5px; padding: 3px 9px; border-radius: 999px; background: var(--surface-2); border: 1px solid var(--line); font-size: 12px; color: var(--ink-2); }
.stat b { font: 500 13px var(--font-mono); color: var(--ink); }
.stat.bad { background: var(--bad-soft); border-color: transparent; color: var(--bad); }
.stat.bad b { color: var(--bad); }
.banner { padding: 6px 16px; background: var(--warn-soft); color: var(--warn); font-size: 13px; }
.menu { position: relative; }
.menu ul { position: absolute; right: 0; top: calc(100% + 4px); z-index: 10; margin: 0; padding: 4px; list-style: none; background: var(--surface); border: 1px solid var(--line); border-radius: 8px; min-width: 150px; }
.menu li button { width: 100%; text-align: left; border: 0; background: none; padding: 6px 10px; border-radius: 6px; cursor: pointer; }
.menu li button:hover { background: var(--surface-2); }

/* panels */
.side, .rail { background: var(--surface); overflow-y: auto; min-width: 0; }
.side { border-right: 1px solid var(--line); }
.rail { border-left: 1px solid var(--line); }
.pad { padding: 16px 16px 40px; display: flex; flex-direction: column; gap: 18px; }
.side.collapsed { display: flex; flex-direction: column; align-items: center; gap: 8px; padding: 10px 0; }
.side-head { display: flex; justify-content: space-between; align-items: center; }
h3.sec { margin: 0 0 8px; font: 600 11px var(--font-body); letter-spacing: .08em; text-transform: uppercase; color: var(--ink-3); display: flex; justify-content: space-between; }
h3.sec span { font-family: var(--font-mono); letter-spacing: 0; }
.rail h2 { margin: 0; font: 700 22px/1.15 var(--font-display); overflow-wrap: anywhere; }
.subline { color: var(--ink-2); font-size: 13px; margin-top: 4px; overflow-wrap: anywhere; }
.summary { font-size: 14px; color: var(--ink-2); margin: 6px 0 0; }
.summary strong { color: var(--ink); }
.seg { display: inline-flex; border: 1px solid var(--line); border-radius: 8px; overflow: hidden; }
.seg button { padding: 4px 11px; font-size: 12px; color: var(--ink-2); background: var(--surface-2); border: 0; cursor: pointer; }
.seg button + button { border-left: 1px solid var(--line); }
.seg button[aria-pressed="true"] { background: var(--accent); color: var(--surface); }
.tabs { display: flex; gap: 2px; border-bottom: 1px solid var(--line); flex-wrap: wrap; }
.tabs button { padding: 7px 10px; font-size: 13px; color: var(--ink-3); border: 0; background: none; border-bottom: 2px solid transparent; margin-bottom: -1px; cursor: pointer; }
.tabs button[aria-selected="true"] { color: var(--ink); border-color: var(--accent); font-weight: 500; }
.filter { width: 100%; margin-top: 8px; padding: 6px 9px; border: 1px solid var(--line); border-radius: 6px; background: var(--surface-2); font: 13px var(--font-mono); }
.navlist { list-style: none; margin: 8px 0 0; padding: 0; display: flex; flex-direction: column; gap: 1px; }
.navlist button { width: 100%; display: flex; justify-content: space-between; gap: 8px; padding: 5px 8px; border: 0; border-radius: 6px; background: none; font-size: 13px; text-align: left; cursor: pointer; }
.navlist button:hover { background: var(--surface-2); }
.navlist button.on { background: var(--accent-soft); color: var(--accent); font-weight: 500; }
.navlist small { font: 12px var(--font-mono); color: var(--ink-3); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.more { font-size: 12px; color: var(--ink-3); margin: 6px 0 0; }
.check { display: flex; align-items: center; gap: 8px; font-size: 13px; color: var(--ink-2); padding: 2px 0; }
.dot { display: inline-block; width: 9px; height: 9px; border-radius: 3px; margin-right: 6px; flex: none; }
.list { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; border: 1px solid var(--line); border-radius: 8px; overflow: hidden; }
.row { display: grid; grid-template-columns: 62px minmax(0, 1fr); gap: 3px 10px; padding: 9px 12px; background: var(--surface); width: 100%; border: 0; text-align: left; }
button.row { cursor: pointer; }
button.row:hover { background: var(--surface-2); }
.row + .row, li + li > .row { border-top: 1px solid var(--line); }
.row .route { font: 13px var(--font-mono); overflow-wrap: anywhere; }
.row .detail { grid-column: 2; font-size: 12px; color: var(--ink-2); overflow-wrap: anywhere; }
.row.error { box-shadow: inset 3px 0 0 var(--bad); }
.row.warning { box-shadow: inset 3px 0 0 var(--warn); }
.row.info { box-shadow: inset 3px 0 0 var(--ink-3); }
.verb { align-self: start; justify-self: start; font: 600 10.5px/1 var(--font-mono); padding: 4px 6px; border-radius: 4px; color: var(--surface); background: var(--m-other); min-width: 48px; text-align: center; text-transform: uppercase; }
.verb.GET { background: var(--m-get); } .verb.POST { background: var(--m-post); } .verb.PUT { background: var(--m-put); }
.verb.DELETE { background: var(--m-delete); } .verb.PATCH { background: var(--m-patch); }
.verb.warning { background: var(--warn); } .verb.error { background: var(--bad); } .verb.info { background: var(--ink-3); }
.pill { display: inline-block; font: 500 11px var(--font-mono); padding: 1px 7px; border-radius: 999px; background: var(--surface-2); color: var(--ink-2); border: 1px solid var(--line); }
.pill.high, .pill.certain { color: var(--ok); }
.pill.medium { color: var(--warn); }
.pill.low { background: var(--warn-soft); color: var(--warn); border-color: transparent; }
.pill.xrepo { color: var(--accent); border-color: var(--accent); cursor: pointer; background: none; }
.kpis { display: grid; grid-template-columns: repeat(3, 1fr); gap: 8px; }
.kpi { border: 1px solid var(--line); border-radius: 8px; padding: 10px; }
.kpi small { display: block; font-size: 11px; color: var(--ink-3); text-transform: uppercase; letter-spacing: .06em; }
.kpi b { font: 700 28px/1.1 var(--font-display); }
.kpi.bad b { color: var(--bad); }
.legend { display: grid; grid-template-columns: 34px 1fr; gap: 7px 10px; align-items: center; font-size: 12px; color: var(--ink-2); }
.legend svg { width: 34px; height: 10px; }
.action { align-self: flex-start; border: 1px solid var(--accent); color: var(--accent); background: var(--accent-soft); border-radius: 8px; padding: 6px 12px; cursor: pointer; font-weight: 500; }
.empty { color: var(--ink-3); font-size: 13px; padding: 10px 12px; border: 1px dashed var(--line); border-radius: 8px; }
.tree { font: 13px/1.9 var(--font-mono); border: 1px solid var(--line); border-radius: 8px; padding: 8px 12px; }
.tree .t { color: var(--ink-3); }
.tree button.twist { border: 0; background: none; padding: 0 4px 0 0; cursor: pointer; color: var(--ink-3); }
.tree .chg { background: var(--bad-soft); color: var(--bad); border-radius: 4px; padding: 0 4px; }

/* stage + map */
.stage { position: relative; min-width: 0; min-height: 0; overflow: hidden; }
.stage .react-flow { background: var(--bg); }
.react-flow__background circle, .react-flow__background path { fill: var(--grid); }
.floating { position: absolute; left: 12px; top: 12px; z-index: 5; display: flex; gap: 8px; flex-wrap: wrap; }
.chip { display: inline-flex; align-items: center; gap: 6px; padding: 4px 10px; border-radius: 999px; background: var(--surface); border: 1px solid var(--line); font-size: 12px; color: var(--ink-2); cursor: pointer; }
.notice { position: absolute; left: 50%; top: 12px; transform: translateX(-50%); z-index: 6; padding: 6px 12px; border-radius: 8px; background: var(--surface); border: 1px solid var(--line); font-size: 13px; color: var(--ink-2); max-width: 70%; }
.toast { position: absolute; left: 50%; bottom: 20px; transform: translateX(-50%); z-index: 7; padding: 10px 14px; border-radius: 10px; background: var(--surface); border: 1px solid var(--bad); display: flex; gap: 8px; align-items: center; flex-wrap: wrap; font-size: 13px; }
.toast button { border: 1px solid var(--line); background: var(--surface-2); border-radius: 6px; padding: 2px 8px; font: 12px var(--font-mono); cursor: pointer; }
.react-flow__controls { border: 1px solid var(--line); border-radius: 8px; overflow: hidden; box-shadow: none; }
.react-flow__controls-button { background: var(--surface); border-bottom: 1px solid var(--line); fill: var(--ink-2); width: 32px; height: 30px; }
.react-flow__handle { opacity: 0; width: 1px; height: 1px; min-width: 0; min-height: 0; border: 0; }
.dk-node { position: relative; width: 100%; height: 100%; display: flex; flex-direction: column; justify-content: center; gap: 2px; padding: 0 12px 0 16px; background: var(--surface); border: 1.2px solid var(--line); border-radius: 10px; cursor: pointer; transition: opacity .15s, border-color .15s; }
.dk-node .stripe { position: absolute; left: 0; top: 12px; bottom: 12px; width: 4px; border-radius: 2px; }
.dk-node .tag { font: 500 10px var(--font-mono); letter-spacing: .06em; color: var(--ink-3); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.dk-node .title { font: 600 15px/1.2 var(--font-display); color: var(--ink); display: flex; gap: 8px; align-items: center; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.dk-node.project .title { font-size: 13px; }
.dk-node.endpoint .title { font: 13px var(--font-mono); }
.dk-node .meta { font: 11px var(--font-mono); color: var(--ink-3); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.dk-node .badge { position: absolute; right: -8px; top: -10px; font: 600 11px var(--font-mono); padding: 1px 8px; border-radius: 9px; color: var(--surface); }
.dk-node .badge.warn { background: var(--warn); } .dk-node .badge.bad { background: var(--bad); }
.dk-node.selected { border: 2.2px solid var(--accent); }
.dk-node.dim, .dk-node.fade { opacity: .28; }
.dk-node.src { border: 2.6px solid var(--accent); background: var(--accent-soft); }
.dk-node.hit { border: 2.2px solid var(--bad); }
.dk-node.added { animation: dk-in .6s ease-out; }
@keyframes dk-in { from { opacity: 0; transform: scale(.94); } to { opacity: 1; transform: none; } }
.dk-container { width: 100%; height: 100%; border: 1.2px dashed var(--line); border-radius: 14px; background: color-mix(in srgb, var(--surface) 55%, transparent); padding: 10px 14px; font: 600 12px var(--font-mono); color: var(--ink-3); display: flex; align-items: flex-start; }
.react-flow__edge-path { stroke: var(--ink-3); fill: none; stroke-linecap: round; }
.react-flow__edge.warn .react-flow__edge-path { stroke: var(--warn); }
.react-flow__edge.hot .react-flow__edge-path { stroke: var(--bad); }
.react-flow__edge.dashed .react-flow__edge-path { stroke-dasharray: 5 5; }
.react-flow__edge.dim, .react-flow__edge.fade { opacity: .2; }
.dk-edge-label { position: absolute; pointer-events: none; padding: 1px 8px; border-radius: 10px; background: var(--surface); border: 1px solid var(--line); font: 11px var(--font-mono); color: var(--ink-2); white-space: nowrap; }
.dk-edge-label.warn { background: var(--warn-soft); border-color: transparent; color: var(--warn); }
.dk-edge-label.hot { background: var(--bad-soft); border-color: transparent; color: var(--bad); }
.dk-edge-label.dim, .dk-edge-label.fade { opacity: .2; }

/* command palette */
.palette-backdrop { position: fixed; inset: 0; z-index: 20; background: color-mix(in srgb, var(--ink) 25%, transparent); display: flex; justify-content: center; align-items: flex-start; padding-top: 12vh; }
.palette { width: min(620px, 92vw); background: var(--surface); border: 1px solid var(--line); border-radius: 12px; overflow: hidden; }
.palette input { width: 100%; border: 0; border-bottom: 1px solid var(--line); padding: 14px 16px; font: 15px var(--font-mono); background: var(--surface); outline: none; }
.palette ul { list-style: none; margin: 0; padding: 6px; max-height: 50vh; overflow-y: auto; }
.palette li { display: flex; gap: 10px; align-items: baseline; padding: 7px 10px; border-radius: 6px; cursor: pointer; }
.palette li[aria-selected="true"] { background: var(--accent-soft); }
.palette li small { color: var(--ink-3); font: 11px var(--font-mono); text-transform: uppercase; min-width: 90px; }

@media (max-width: 860px) {
  .main, .app.collapsed .main { grid-template-columns: 1fr; grid-template-rows: auto 60vh auto; }
  .side { border-right: 0; border-bottom: 1px solid var(--line); }
  .side.collapsed { flex-direction: row; justify-content: flex-start; padding: 8px 16px; }
  .rail { border-left: 0; }
  .search { flex-basis: 100%; margin-left: 0; }
}
@media (prefers-reduced-motion: reduce) { .dk-node { transition: none; } .dk-node.added { animation: none; } }
```

- [ ] **Step 4: Pure modules**

`web/src/app/route.ts`:

```ts
export type Route = { kind: 'home' } | { kind: 'node'; id: string } | { kind: 'impact'; target: string };

/** encodeURIComponent, keeping ':' and '/' readable. Must match C# Depenk.Server.DeepLink.Encode. */
export const encodeId = (id: string): string => encodeURIComponent(id).replace(/%3A/gi, ':').replace(/%2F/gi, '/');

const decode = (s: string): string => { try { return decodeURIComponent(s); } catch { return s; } };

export function parseHash(hash: string): Route {
  const h = hash.startsWith('#') ? hash.slice(1) : hash;
  if (!h.startsWith('/') || h.length === 1) return { kind: 'home' };
  const body = h.slice(1);
  if (body.startsWith('impact/')) return { kind: 'impact', target: decode(body.slice('impact/'.length)) };
  return { kind: 'node', id: decode(body) };
}

export function formatRoute(r: Route): string {
  switch (r.kind) {
    case 'home': return '#/';
    case 'node': return `#/${encodeId(r.id)}`;
    case 'impact': return `#/impact/${encodeId(r.target)}`;
  }
}
```

`web/src/app/persist.ts`:

```ts
// localStorage can be missing or throw (private windows, file:// in some browsers): every access is guarded.
export function load<T>(key: string, fallback: T): T {
  try {
    const v = localStorage.getItem(key);
    return v === null ? fallback : (JSON.parse(v) as T);
  } catch {
    return fallback;
  }
}

export function save(key: string, value: unknown): void {
  try {
    if (value === null || value === undefined) localStorage.removeItem(key);
    else localStorage.setItem(key, JSON.stringify(value));
  } catch { /* storage unavailable */ }
}

export const KEYS = {
  sidebar: 'depenk:sidebar',
  theme: 'depenk:theme',
  positions: (workspace: string, depth: string) => `depenk:positions:${workspace}:${depth}`,
};
```

`web/src/app/theme.ts`:

```ts
import type { Theme } from './state';

export const systemPrefersDark = (): boolean => window.matchMedia?.('(prefers-color-scheme: dark)').matches ?? false;

/** null follows the OS (prefers-color-scheme); a choice is pinned with data-theme. */
export function applyTheme(theme: Theme | null): void {
  if (theme === null) delete document.documentElement.dataset.theme;
  else document.documentElement.dataset.theme = theme;
}

export const nextTheme = (current: Theme | null): Theme =>
  (current ?? (systemPrefersDark() ? 'dark' : 'light')) === 'dark' ? 'light' : 'dark';
```

`web/src/app/state.ts`:

```ts
import type { GraphIndex, NodeKind } from '../graph/graphIndex';
import { DEFAULT_FILTERS, type Depth, type Filters } from '../graph/views';

export type Highlight = 'up' | 'down' | 'both';
export type Theme = 'light' | 'dark';
export interface Place { depth: Depth; focus: string | null }

export interface AppState {
  depth: Depth; focus: string | null; selection: string | null; trail: Place[];
  impact: string | null; filters: Filters; highlight: Highlight;
  sidebarCollapsed: boolean; theme: Theme | null; paletteOpen: boolean;
  notFound: { query: string; suggestions: string[] } | null;
}

export type Action =
  | { type: 'setDepth'; depth: Depth }
  | { type: 'select'; id: string | null }
  | { type: 'drill'; id: string; kind: NodeKind }
  | { type: 'back' }
  | { type: 'focusNode'; place: Place; selection: string }
  | { type: 'setFilters'; patch: Partial<Filters> }
  | { type: 'setHighlight'; highlight: Highlight }
  | { type: 'toggleSidebar' }
  | { type: 'setTheme'; theme: Theme | null }
  | { type: 'enterImpact'; target: string }
  | { type: 'exitImpact' }
  | { type: 'setPalette'; open: boolean }
  | { type: 'notFound'; query: string; suggestions: string[] }
  | { type: 'dismissNotFound' }
  | { type: 'reconcile'; known: (id: string) => boolean; impactValid: boolean };

export function initialState(persisted: { sidebarCollapsed: boolean; theme: Theme | null }): AppState {
  return {
    depth: 'repo', focus: null, selection: null, trail: [], impact: null, filters: DEFAULT_FILTERS, highlight: 'both',
    sidebarCollapsed: persisted.sidebarCollapsed, theme: persisted.theme, paletteOpen: false, notFound: null,
  };
}

/** Double-click: a repo opens its projects; anything finer opens its call chain. */
export function drillTarget(kind: NodeKind, id: string): Place | null {
  if (kind === 'repo') return { depth: 'project', focus: id };
  return { depth: 'endpoint', focus: id };
}

/** Where a deep link or list click should land so the node is visible. */
export function placeFor(ix: GraphIndex, id: string): { place: Place; selection: string } {
  const n = ix.get(id);
  switch (n.kind) {
    case 'repo': return { place: { depth: 'repo', focus: null }, selection: id };
    case 'project': return { place: { depth: 'project', focus: `repo:${n.repo}` }, selection: id };
    case 'package': return { place: { depth: 'project', focus: n.repo ? `repo:${n.repo}` : null }, selection: id };
    default: return { place: { depth: 'endpoint', focus: id }, selection: id };
  }
}

export function reducer(s: AppState, a: Action): AppState {
  switch (a.type) {
    case 'setDepth': return { ...s, depth: a.depth, focus: a.depth === 'repo' ? null : s.focus, trail: [] };
    case 'select': return { ...s, selection: a.id };
    case 'drill': {
      const place = drillTarget(a.kind, a.id);
      if (!place) return s;
      return { ...s, trail: [...s.trail, { depth: s.depth, focus: s.focus }], ...place, selection: a.id };
    }
    case 'back': {
      const prev = s.trail.at(-1);
      if (prev) return { ...s, ...prev, trail: s.trail.slice(0, -1) };
      return s.depth === 'repo' ? s : { ...s, depth: 'repo', focus: null };
    }
    case 'focusNode':
      return { ...s, trail: [...s.trail, { depth: s.depth, focus: s.focus }], ...a.place, selection: a.selection, notFound: null };
    case 'setFilters': return { ...s, filters: { ...s.filters, ...a.patch } };
    case 'setHighlight': return { ...s, highlight: a.highlight };
    case 'toggleSidebar': return { ...s, sidebarCollapsed: !s.sidebarCollapsed };
    case 'setTheme': return { ...s, theme: a.theme };
    case 'enterImpact': return { ...s, impact: a.target, paletteOpen: false, notFound: null };
    case 'exitImpact': return { ...s, impact: null };
    case 'setPalette': return { ...s, paletteOpen: a.open };
    case 'notFound': return { ...s, notFound: { query: a.query, suggestions: a.suggestions } };
    case 'dismissNotFound': return { ...s, notFound: null };
    case 'reconcile': {
      const focusGone = s.focus !== null && !a.known(s.focus);
      return {
        ...s,
        depth: focusGone ? 'repo' : s.depth,
        focus: focusGone ? null : s.focus,
        trail: s.trail.filter(p => p.focus === null || a.known(p.focus)),
        selection: s.selection !== null && a.known(s.selection) ? s.selection : null,
        impact: a.impactValid ? s.impact : null,
      };
    }
  }
}

/** System / repo / project / node for the current focus (a repo focus is just System / repo). */
export function breadcrumb(ix: GraphIndex, s: AppState): { label: string; place: Place | null }[] {
  const crumbs: { label: string; place: Place | null }[] = [{ label: 'System', place: { depth: 'repo', focus: null } }];
  const n = s.focus ? ix.tryGet(s.focus) : undefined;
  if (!n) return crumbs;
  if (n.repo) crumbs.push({ label: n.repo, place: { depth: 'project', focus: `repo:${n.repo}` } });
  if (n.kind === 'repo') return crumbs;
  const projectId = ix.endpoints.get(n.id)?.projectId ?? ix.clientMethods.get(n.id)?.projectId ?? ix.callSites.get(n.id)?.projectId;
  const project = projectId ? ix.projects.get(projectId) : undefined;
  if (project) crumbs.push({ label: project.name, place: { depth: 'endpoint', focus: project.id } });
  if (n.kind !== 'project' || !project) crumbs.push({ label: n.label, place: null });
  return crumbs;
}
```

`web/src/graph/overview.ts`:

```ts
import { GraphIndex, ordinal } from './graphIndex';
import type { Diagnostic, Severity } from './types';

export const SEVERITY_RANK: Record<Severity, number> = { error: 0, warning: 1, info: 2 };

export function overviewData(ix: GraphIndex) {
  const links = ix.graph.edges.filter(e => e.kind === 'dependsOn');
  const attention: Diagnostic[] = [...ix.graph.diagnostics].sort((a, b) =>
    SEVERITY_RANK[a.severity] - SEVERITY_RANK[b.severity] || ordinal(a.kind, b.kind) || ordinal(a.message, b.message));
  return {
    links: links.length,
    repos: ix.repos.size,
    packages: new Set(links.flatMap(e => e.viaPackages ?? [])).size,
    callSites: ix.callSites.size,
    endpoints: ix.endpoints.size,
    attention,
  };
}
```

`web/src/graph/browse.ts`:

```ts
import { GraphIndex, ordinal } from './graphIndex';
import { SEVERITY_RANK } from './overview';
import type { Diagnostic, Severity } from './types';

export type BrowseTab = 'repos' | 'endpoints' | 'models' | 'packages';
export interface BrowseItem { id: string; label: string; detail: string; repo: string | null }

const simple = (fullName: string) => fullName.slice(fullName.lastIndexOf('.') + 1);

export function browseItems(ix: GraphIndex, tab: BrowseTab, filter: string): BrowseItem[] {
  let items: BrowseItem[];
  switch (tab) {
    case 'repos': {
      const projects = new Map<string, number>();
      for (const p of ix.projects.values()) projects.set(p.repo, (projects.get(p.repo) ?? 0) + 1);
      items = [...ix.repos.values()].map(r => ({ id: r.id, label: r.name, detail: `${projects.get(r.name) ?? 0}`, repo: r.name }));
      break;
    }
    case 'endpoints':
      items = [...ix.endpoints.values()].map(e => ({ id: e.id, label: `${e.verb} ${e.route}`, detail: e.repo, repo: e.repo }));
      break;
    case 'models':
      items = [...ix.models.values()].map(m => ({ id: m.id, label: simple(m.fullName), detail: m.fullName, repo: m.repo || null }));
      break;
    case 'packages':
      items = [...ix.packages.values()].map(p => {
        const consumers = ix.dependentsOf(p.id).filter(h => h.kind === 'references').length;
        const owner = ix.tryGet(p.id)?.repo ?? null;
        return { id: p.id, label: p.packageId, detail: `${owner ?? 'third-party'} · ${consumers} consumer${consumers === 1 ? '' : 's'}`, repo: owner };
      });
      break;
  }
  const q = filter.trim().toLowerCase();
  return items
    .filter(i => q === '' || i.label.toLowerCase().includes(q) || i.id.toLowerCase().includes(q))
    .sort((a, b) => ordinal(a.label.toLowerCase(), b.label.toLowerCase()) || ordinal(a.id, b.id));
}

export interface DiagnosticGroup { kind: string; severity: Severity; items: Diagnostic[] }

export function diagnosticGroups(ix: GraphIndex): DiagnosticGroup[] {
  const groups = new Map<string, DiagnosticGroup>();
  for (const d of ix.graph.diagnostics) {
    const g = groups.get(d.kind) ?? { kind: d.kind, severity: d.severity, items: [] };
    if (SEVERITY_RANK[d.severity] < SEVERITY_RANK[g.severity]) g.severity = d.severity;
    g.items.push(d);
    groups.set(d.kind, g);
  }
  return [...groups.values()].sort((a, b) => SEVERITY_RANK[a.severity] - SEVERITY_RANK[b.severity] || ordinal(a.kind, b.kind));
}
```

- [ ] **Step 5: Panels and the app**

`web/src/panels/types.ts`:

```ts
import type { Dispatch } from 'react';
import type { Action, AppState } from '../app/state';
import type { GraphIndex } from '../graph/graphIndex';

export interface PanelProps { ix: GraphIndex; state: AppState; dispatch: Dispatch<Action>; colors: Map<string, number> }
```

`web/src/panels/Segmented.tsx`:

```tsx
export function Segmented<T extends string>({ label, options, value, onChange }: {
  label: string; options: readonly (readonly [T, string])[]; value: T; onChange: (v: T) => void;
}) {
  return (
    <div className="seg" role="group" aria-label={label}>
      {options.map(([v, text]) => (
        <button key={v} type="button" aria-pressed={v === value} onClick={() => onChange(v)}>{text}</button>
      ))}
    </div>
  );
}
```

`web/src/panels/TopBar.tsx`:

```tsx
import type { ReactNode } from 'react';
import { breadcrumb } from '../app/state';
import { nextTheme } from '../app/theme';
import type { PanelProps } from './types';

export function TopBar({ ix, state, dispatch, impactSummary, exportMenu }: PanelProps & { impactSummary?: ReactNode; exportMenu?: ReactNode }) {
  const scanned = ix.graph.generatedAt ? new Date(ix.graph.generatedAt) : null;
  return (
    <header className="bar">
      <div className="brand">
        <h1>depenk</h1>
        {scanned && !isNaN(scanned.getTime()) && <small>scanned {scanned.toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' })}</small>}
      </div>
      {impactSummary ?? (
        <nav className="crumbs" aria-label="Breadcrumb">
          {breadcrumb(ix, state).map((c, i, all) => (
            <span key={i}>
              <button type="button" disabled={!c.place} onClick={() => c.place && dispatch({ type: 'focusNode', place: c.place, selection: c.place.focus ?? state.selection ?? '' })}>{c.label}</button>
              {i < all.length - 1 && ' / '}
            </span>
          ))}
        </nav>
      )}
      <button type="button" className="search" onClick={() => dispatch({ type: 'setPalette', open: true })}>
        ⌕ Find anything… <kbd>Ctrl K</kbd>
      </button>
      {exportMenu}
      <button type="button" className="icon-btn" aria-label="Toggle light/dark theme"
        onClick={() => dispatch({ type: 'setTheme', theme: nextTheme(state.theme) })}>◐</button>
    </header>
  );
}
```

`web/src/panels/Sidebar.tsx`:

```tsx
import { useState } from 'react';
import { placeFor } from '../app/state';
import { browseItems, diagnosticGroups, type BrowseTab } from '../graph/browse';
import { repoColor } from '../graph/colors';
import type { Depth } from '../graph/views';
import type { Confidence } from '../graph/types';
import { Segmented } from './Segmented';
import type { PanelProps } from './types';

const DEPTHS = [['repo', 'Repo'], ['project', 'Project'], ['endpoint', 'Endpoint']] as const;
const TABS = [['repos', 'Repos'], ['endpoints', 'Endpoints'], ['models', 'Models'], ['packages', 'Packages']] as const;
const SHOW = [['api', 'API projects'], ['client', 'Client packages'], ['library', 'Libraries'], ['test', 'Test projects'], ['thirdParty', 'Third-party packages']] as const;
const LIST_CAP = 200;

export function Sidebar({ ix, state, dispatch, colors }: PanelProps) {
  const [tab, setTab] = useState<BrowseTab>('repos');
  const [filter, setFilter] = useState('');
  const [open, setOpen] = useState<string | null>(null);
  const setDepth = (depth: Depth) => dispatch({ type: 'setDepth', depth });
  const focus = (id: string) => dispatch({ type: 'focusNode', ...placeFor(ix, id) });

  if (state.sidebarCollapsed)
    return (
      <aside className="side collapsed" aria-label="Sidebar">
        <button type="button" className="icon-btn" aria-label="Expand sidebar" onClick={() => dispatch({ type: 'toggleSidebar' })}>»</button>
        {DEPTHS.map(([d, label]) => (
          <button key={d} type="button" className="icon-btn" aria-label={label} aria-pressed={state.depth === d} onClick={() => setDepth(d)}>{label[0]}</button>
        ))}
      </aside>
    );

  const items = browseItems(ix, tab, filter);
  return (
    <aside className="side" aria-label="Sidebar">
      <div className="pad">
        <div>
          <div className="side-head">
            <h3 className="sec">Depth</h3>
            <button type="button" className="icon-btn" aria-label="Collapse sidebar" onClick={() => dispatch({ type: 'toggleSidebar' })}>«</button>
          </div>
          <Segmented label="Depth" options={DEPTHS} value={state.depth} onChange={setDepth} />
        </div>

        <div>
          <div className="tabs" role="tablist" aria-label="Browse">
            {TABS.map(([t, label]) => (
              <button key={t} type="button" role="tab" aria-selected={tab === t} onClick={() => { setTab(t); setFilter(''); }}>{label}</button>
            ))}
          </div>
          <input className="filter" placeholder="Filter…" aria-label={`Filter ${tab}`} value={filter} onChange={e => setFilter(e.target.value)} />
          <ul className="navlist">
            {items.slice(0, LIST_CAP).map(i => (
              <li key={i.id}>
                <button type="button" className={state.selection === i.id ? 'on' : ''} onClick={() => focus(i.id)}>
                  <span>{i.repo && <i className="dot" style={{ background: repoColor(colors, i.repo) }} />}{i.label}</span>
                  <small>{i.detail}</small>
                </button>
              </li>
            ))}
          </ul>
          {items.length > LIST_CAP && <p className="more">+{items.length - LIST_CAP} more — refine the filter</p>}
        </div>

        <div>
          <h3 className="sec">Show</h3>
          {SHOW.map(([key, label]) => (
            <label key={key} className="check">
              <input type="checkbox" checked={state.filters[key]} onChange={e => dispatch({ type: 'setFilters', patch: { [key]: e.target.checked } })} />
              {label}
            </label>
          ))}
        </div>

        <div>
          <h3 className="sec">Highlight</h3>
          <Segmented label="Highlight" options={[['up', 'Up'], ['down', 'Down'], ['both', 'Both']] as const} value={state.highlight}
            onChange={h => dispatch({ type: 'setHighlight', highlight: h })} />
        </div>

        <div>
          <h3 className="sec">Min confidence</h3>
          <Segmented<Confidence> label="Minimum confidence" options={[['low', 'Any'], ['medium', 'Med'], ['high', 'High']] as const}
            value={state.filters.minConfidence} onChange={c => dispatch({ type: 'setFilters', patch: { minConfidence: c } })} />
        </div>

        <div>
          <h3 className="sec">Diagnostics <span>{ix.graph.diagnostics.length}</span></h3>
          <ul className="navlist">
            {diagnosticGroups(ix).map(g => (
              <li key={g.kind}>
                <button type="button" aria-expanded={open === g.kind} onClick={() => setOpen(open === g.kind ? null : g.kind)}>
                  <span style={{ color: g.severity === 'info' ? 'var(--ink-3)' : g.severity === 'error' ? 'var(--bad)' : 'var(--warn)' }}>● {g.kind}</span>
                  <small>{g.items.length}</small>
                </button>
                {open === g.kind && (
                  <ul className="navlist">
                    {g.items.map((d, i) => (
                      <li key={i}>
                        <button type="button" disabled={!d.nodeIds.some(id => ix.tryGet(id))}
                          onClick={() => { const id = d.nodeIds.find(x => ix.tryGet(x)); if (id) focus(id); }}>
                          <small style={{ whiteSpace: 'normal' }}>{d.message}</small>
                        </button>
                      </li>
                    ))}
                  </ul>
                )}
              </li>
            ))}
          </ul>
        </div>
      </div>
    </aside>
  );
}
```

`web/src/panels/Overview.tsx`:

```tsx
import { placeFor } from '../app/state';
import { browseItems } from '../graph/browse';
import { repoColor } from '../graph/colors';
import { overviewData } from '../graph/overview';
import type { PanelProps } from './types';

export function Overview({ ix, dispatch, colors }: PanelProps) {
  const o = overviewData(ix);
  const focus = (id: string) => ix.tryGet(id) && dispatch({ type: 'focusNode', ...placeFor(ix, id) });
  return (
    <div className="pad">
      <div>
        <h2>{o.links} links across {o.repos} repos</h2>
        <p className="summary">
          Repos depend on each other through <strong>{o.packages} client packages</strong>. {o.callSites} call sites reach {o.endpoints} endpoints.
          Select a repo or an arrow to see what's behind it.
        </p>
      </div>
      <section>
        <h3 className="sec">Needs attention <span>{o.attention.length}</span></h3>
        {o.attention.length === 0 ? <div className="empty">Nothing to fix: no drift, cycles or ambiguous links.</div> : (
          <ul className="list" aria-label="Needs attention">
            {o.attention.map((d, i) => (
              <li key={i}>
                <button type="button" className={`row ${d.severity}`} onClick={() => d.nodeIds[0] && focus(d.nodeIds[0])}>
                  <span className={`verb ${d.severity}`}>{d.severity === 'info' ? 'INFO' : d.severity === 'error' ? 'ERROR' : 'WARN'}</span>
                  <span className="route">{d.kind}</span>
                  <span className="detail">{d.message}</span>
                </button>
              </li>
            ))}
          </ul>
        )}
      </section>
      <section>
        <h3 className="sec">Repos <span>{o.repos}</span></h3>
        <ul className="list" aria-label="Repos">
          {browseItems(ix, 'repos', '').map(r => (
            <li key={r.id}>
              <button type="button" className="row" style={{ gridTemplateColumns: '1fr' }} onClick={() => focus(r.id)}>
                <span className="route"><i className="dot" style={{ background: repoColor(colors, r.label) }} />{r.label}</span>
                <span className="detail" style={{ gridColumn: 1 }}>{r.detail} projects</span>
              </button>
            </li>
          ))}
        </ul>
      </section>
      <section>
        <h3 className="sec">Legend</h3>
        <div className="legend">
          <svg viewBox="0 0 34 10" aria-hidden="true"><path d="M2 5H30" style={{ stroke: 'var(--ink-3)', strokeWidth: 2.5 }} /></svg>
          <span>Depends on, through a client package (thicker = more calls)</span>
          <svg viewBox="0 0 34 10" aria-hidden="true"><path d="M2 5H30" style={{ stroke: 'var(--warn)', strokeWidth: 2.5 }} /></svg>
          <span>Part of a dependency cycle, or version drift</span>
          <svg viewBox="0 0 34 10" aria-hidden="true"><path d="M2 5H30" style={{ stroke: 'var(--ink-3)', strokeWidth: 2, strokeDasharray: '5 5' }} /></svg>
          <span>Low/medium confidence, or referenced but never called</span>
        </div>
      </section>
    </div>
  );
}
```

`web/src/panels/NotFound.tsx`:

```tsx
import { placeFor } from '../app/state';
import type { PanelProps } from './types';

export function NotFound({ ix, state, dispatch }: PanelProps) {
  if (!state.notFound) return null;
  const { query, suggestions } = state.notFound;
  return (
    <div className="toast" role="alert">
      <span>Not found: {query}</span>
      {suggestions.filter(id => ix.tryGet(id)).map(id => (
        <button key={id} type="button" onClick={() => dispatch({ type: 'focusNode', ...placeFor(ix, id) })}>{id}</button>
      ))}
      <button type="button" aria-label="Dismiss" onClick={() => dispatch({ type: 'dismissNotFound' })}>✕</button>
    </div>
  );
}
```

`web/src/panels/Inspector.tsx` (Task 9 replaces the selected-node body):

```tsx
import { Overview } from './Overview';
import type { PanelProps } from './types';

export function Inspector(props: PanelProps) {
  const n = props.state.selection ? props.ix.tryGet(props.state.selection) : undefined;
  return (
    <aside className="rail" aria-label="Inspector">
      {!n ? <Overview {...props} /> : (
        <div className="pad">
          <div><h2>{n.label}</h2><div className="subline">{n.kind}{n.repo ? ` · ${n.repo}` : ''}</div></div>
        </div>
      )}
    </aside>
  );
}
```

`web/src/app/App.tsx`:

```tsx
import { useEffect, useMemo, useReducer, type Dispatch } from 'react';
import { assignRepoColors } from '../graph/colors';
import { GraphIndex, QueryError } from '../graph/graphIndex';
import type { DepGraph } from '../graph/types';
import type { LayoutEngine } from '../layout/engine';
import { Inspector } from '../panels/Inspector';
import { NotFound } from '../panels/NotFound';
import { Sidebar } from '../panels/Sidebar';
import { TopBar } from '../panels/TopBar';
import type { PanelProps } from '../panels/types';
import { KEYS, load, save } from './persist';
import { formatRoute, parseHash, type Route } from './route';
import { initialState, placeFor, reducer, type Action, type AppState, type Theme } from './state';
import { applyTheme } from './theme';

export interface AppProps {
  graph: DepGraph; engine: LayoutEngine; mode: 'export' | 'serve';
  initialFocus?: string | null; status?: 'ok' | 'reconnecting';
  /** Serve mode: the bundle HTML, for "Export → HTML" (Task 11). */
  bundleHtml?: string;
}

function navigate(ix: GraphIndex, route: Route, dispatch: Dispatch<Action>) {
  if (route.kind === 'home') return;
  if (route.kind === 'impact') { dispatch({ type: 'enterImpact', target: route.target }); return; }
  try {
    dispatch({ type: 'focusNode', ...placeFor(ix, ix.resolveAny(route.id)) });
  } catch (e) {
    if (!(e instanceof QueryError)) throw e;
    dispatch({ type: 'notFound', query: route.id, suggestions: e.suggestions });
  }
}

function hashFor(s: AppState): string {
  if (s.impact) return formatRoute({ kind: 'impact', target: s.impact });
  if (s.selection) return formatRoute({ kind: 'node', id: s.selection });
  return formatRoute({ kind: 'home' });
}

export function App({ graph, engine, mode, initialFocus = null, status = 'ok', bundleHtml }: AppProps) {
  const ix = useMemo(() => new GraphIndex(graph), [graph]);
  const colors = useMemo(() => assignRepoColors(graph.repos.map(r => r.name)), [graph]);
  const [state, dispatch] = useReducer(reducer, undefined,
    () => initialState({ sidebarCollapsed: load(KEYS.sidebar, false), theme: load<Theme | null>(KEYS.theme, null) }));

  useEffect(() => { applyTheme(state.theme); save(KEYS.theme, state.theme); }, [state.theme]);
  useEffect(() => save(KEYS.sidebar, state.sidebarCollapsed), [state.sidebarCollapsed]);

  // hash → state; an export's --focus applies when the URL has no hash of its own
  useEffect(() => {
    const go = () => navigate(ix, parseHash(window.location.hash), dispatch);
    const route = parseHash(window.location.hash);
    if (route.kind === 'home' && initialFocus) navigate(ix, { kind: 'node', id: initialFocus }, dispatch);
    else go();
    window.addEventListener('hashchange', go);
    return () => window.removeEventListener('hashchange', go);
    // ix is intentionally excluded: live graph updates must not re-navigate (Task 11 reconciles instead)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // state → hash (replaceState: no hashchange event, no history spam)
  useEffect(() => {
    const h = hashFor(state);
    const current = window.location.hash || '#/';
    if (h !== current) history.replaceState(null, '', h);
  }, [state.selection, state.impact]); // eslint-disable-line react-hooks/exhaustive-deps

  const panel: PanelProps = { ix, state, dispatch, colors };
  void engine; void mode; void bundleHtml; // used from Tasks 8 and 11
  return (
    <div className={`app${state.sidebarCollapsed ? ' collapsed' : ''}`}>
      <TopBar {...panel} />
      {status === 'reconnecting' ? <div role="status" className="banner">Lost connection — retrying</div> : <div />}
      <div className="main">
        <Sidebar {...panel} />
        <main className="stage" data-testid="stage">
          <NotFound {...panel} />
        </main>
        <Inspector {...panel} />
      </div>
    </div>
  );
}
```

`web/src/main.tsx` (export mode only for now; Task 11 adds serve mode):

```tsx
import { createRoot } from 'react-dom/client';
import { App } from './app/App';
import { parseGraph, readFocus, readInlineGraph } from './graph/load';
import { createWorkerEngine } from './layout/engine';
import './styles/fonts';
import './styles/tokens.css';
import './styles/app.css';

const root = createRoot(document.getElementById('root')!);
const inline = readInlineGraph(document);
const loaded = inline === null ? null : parseGraph(inline);

if (loaded?.ok) {
  root.render(<App graph={loaded.graph} engine={createWorkerEngine()} mode="export" initialFocus={readFocus(document)} />);
} else if (loaded && loaded.reason === 'schema') {
  root.render(<p className="pad">This diagram was made by a newer depenk (graph schema {String(loaded.found)}). Upgrade depenk to view it.</p>);
} else {
  root.render(<p className="pad">No graph is embedded in this page. Run <code>depenk export</code> or <code>depenk serve</code>.</p>);
}
```

- [ ] **Step 6: Run tests, typecheck and build**

Run: `cd web && npx vitest run && npm run build`
Expected: PASS, and `check-bundle: ok`.

- [ ] **Step 7: After screenshot**

Make an export-like file to look at: copy `web/dist/index.html` and replace `<script type="application/json" id="depenk-graph"></script>` with the fixture graph inside that tag. Do this with a small Node one-off in the scratchpad (Task 11 adds `buildExportHtml` for this). Screenshot it in light mode, then in dark mode via `?` → toggle or `page.emulateMediaFeatures([{ name: 'prefers-color-scheme', value: 'dark' }])`. Show the user the before and after screenshots. The stage is still empty; the map arrives in Task 8.

- [ ] **Step 8: Commit**

```bash
git add web
git commit -m "feat(web): app shell: tokens and fonts, state and deep links, theme, top bar, sidebar, overview

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 8: Map canvas: React Flow nodes and edges, hover highlight, drill-in, drag, keyboard

**Files:**
- Create: `web/src/map/geometry.ts`, `web/src/map/neighbours.ts`, `web/src/map/nodes.tsx`, `web/src/map/edges.tsx`, `web/src/map/MapCanvas.tsx`, `web/src/map/MapStage.tsx`
- Modify: `web/src/app/App.tsx` (render `MapStage` in the stage)
- Test: `web/test/geometry.test.ts`, `web/test/neighbours.test.ts`, `web/test/MapCanvas.test.tsx`

**Interfaces:**
- Consumes: `View`, `ViewEdge`, `buildView` (Task 5); `Positions`, `layoutView`, `LayoutEngine` (Task 6); `PanelProps`, `Highlight`, `placeFor`, `KEYS`, `load`, `save` (Task 7).
- Produces (`geometry.ts`): `curve(sx, sy, tx, ty, paired) → { d; labelX; labelY }`, `strokeWidth(calls)`.
- Produces (`neighbours.ts`):
  - `interface ImpactMarks { source: Set<string>; hit: Map<string, number>; edges: Set<string> }` (Task 10 fills this)
  - `related(view, id, highlight): Set<string>`
  - `nodeClass(id, ctx): string`
  - `edgeClass(edge, ctx): { className; marker; labelClass }`
  - `nextByKey(view, positions, selection, key): string | null`
- Produces (`MapCanvas.tsx`): `MapCanvas(props: { view; positions: Positions | null; selection; highlight; impact: ImpactMarks | null; colors; added?: Set<string>; onSelect(id | null); onDrill(id); onMove(id, x, y) })`. The wrapper has `data-ready="true"` once positions are drawn, and nodes carry `data-testid="node-<id>"`.
- Produces (`MapStage.tsx`): `MapStage(props: PanelProps & { engine: LayoutEngine; workspaceKey: string; impact: ImpactMarks | null; added?: Set<string> })`. It marks `performance.mark('depenk-ready')` the first time a layout is applied (Task 15 measures this).

- [ ] **Step 0: Before screenshot** of the built bundle with the fixture graph inlined, made the same way as Task 7, Step 7.

- [ ] **Step 1: Write the failing tests**

`web/test/geometry.test.ts`:

```ts
import { expect, test } from 'vitest';
import { curve, strokeWidth } from '../src/map/geometry';

test('a single edge bends gently, label at the midpoint', () => {
  const c = curve(0, 0, 100, 0, false);
  expect(c.d).toBe('M0,0 Q50,10 100,0');
  expect([c.labelX, c.labelY]).toEqual([50, 5]);
});

test('a two-way pair bends to opposite sides; labels sit near their own source', () => {
  const ab = curve(0, 0, 100, 0, true), ba = curve(100, 0, 0, 0, true);
  expect(ab.d).toBe('M0,0 Q50,46 100,0');
  expect(ba.d).toBe('M100,0 Q50,-46 0,0');
  expect(ab.labelX).toBeCloseTo(30);
  expect(ba.labelX).toBeCloseTo(70);
});

test('stroke width grows with calls and is capped', () => {
  expect(strokeWidth(0)).toBeCloseTo(1.4);
  expect(strokeWidth(2)).toBeCloseTo(2.6);
  expect(strokeWidth(50)).toBe(5);
});
```

`web/test/neighbours.test.ts`:

```ts
import { describe, expect, test } from 'vitest';
import { GraphIndex } from '../src/graph/graphIndex';
import { buildView } from '../src/graph/views';
import { edgeClass, nextByKey, nodeClass, related } from '../src/map/neighbours';
import type { Positions } from '../src/layout/engine';
import { fixtureGraph } from './util/fixture';

const view = buildView(new GraphIndex(fixtureGraph()), 'repo', null);
const e = (id: string) => view.edges.find(x => x.id === id)!;

describe('related', () => {
  test('down = dependencies, up = dependents, both = union', () => {
    expect([...related(view, 'repo:gateway', 'down')].sort()).toEqual(['repo:gateway', 'repo:orders']);
    expect([...related(view, 'repo:orders', 'up')].sort()).toEqual(['repo:billing', 'repo:gateway', 'repo:orders']);
    expect(related(view, 'repo:orders', 'both').size).toBe(5);
  });
});

describe('classes', () => {
  test('hover dims the unrelated; impact overrides', () => {
    const near = related(view, 'repo:gateway', 'both');
    expect(nodeClass('repo:shared', { selection: null, near, impact: null })).toBe('dim');
    expect(nodeClass('repo:gateway', { selection: 'repo:gateway', near, impact: null })).toBe('selected');
    const impact = { source: new Set(['repo:orders']), hit: new Map([['repo:billing', 1]]), edges: new Set(['repo:billing>repo:orders']) };
    expect(nodeClass('repo:orders', { selection: null, near: null, impact })).toBe('src');
    expect(nodeClass('repo:billing', { selection: null, near: null, impact })).toBe('hit');
    expect(nodeClass('repo:shared', { selection: null, near: null, impact })).toBe('fade');
    expect(edgeClass(e('repo:billing>repo:orders'), { focusId: null, highlight: 'both', impact }).className).toContain('hot');
  });
  test('cycle edges are warn with a warn arrow; never-called edges dashed', () => {
    expect(edgeClass(e('repo:billing>repo:orders'), { focusId: null, highlight: 'both', impact: null })).toMatchObject({ marker: 'dk-arrow-warn', labelClass: 'warn' });
    expect(edgeClass(e('repo:orders>repo:customers'), { focusId: null, highlight: 'both', impact: null }).className).toContain('dashed');
    expect(edgeClass(e('repo:gateway>repo:orders'), { focusId: 'repo:shared', highlight: 'both', impact: null }).className).toContain('dim');
  });
});

describe('nextByKey', () => {
  const positions: Positions = { nodes: Object.fromEntries(view.nodes.map((n, i) => [n.id, { x: 0, y: i * 100, width: 10, height: 10 }])) };
  test('right follows a dependency, left a dependent, down the next node in the column', () => {
    expect(nextByKey(view, positions, 'repo:gateway', 'ArrowRight')).toBe('repo:orders');
    expect(nextByKey(view, positions, 'repo:orders', 'ArrowLeft')).toBe('repo:billing');
    expect(nextByKey(view, positions, 'repo:billing', 'ArrowDown')).toBe('repo:customers');
    expect(nextByKey(view, positions, 'repo:billing', 'ArrowUp')).toBeNull();
    expect(nextByKey(view, positions, null, 'ArrowDown')).toBe('repo:billing');
    expect(nextByKey(view, positions, 'repo:gateway', 'Enter')).toBeNull();
  });
});
```

`web/test/MapCanvas.test.tsx`:

```tsx
import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, test, vi } from 'vitest';
import { GraphIndex } from '../src/graph/graphIndex';
import { assignRepoColors } from '../src/graph/colors';
import { buildView } from '../src/graph/views';
import { layoutView } from '../src/layout/engine';
import { MapCanvas } from '../src/map/MapCanvas';
import { fixtureGraph } from './util/fixture';
import { inThreadEngine } from './util/inThreadEngine';

const g = fixtureGraph();
const view = buildView(new GraphIndex(g), 'repo', null);
const colors = assignRepoColors(g.repos.map(r => r.name));

async function setup() {
  const positions = await layoutView(inThreadEngine(), view);
  const props = { onSelect: vi.fn(), onDrill: vi.fn(), onMove: vi.fn() };
  render(<MapCanvas view={view} positions={positions} selection={null} highlight="both" impact={null} colors={colors} {...props} />);
  return props;
}

describe('MapCanvas', () => {
  test('draws one node per repo and reports ready', async () => {
    await setup();
    for (const r of ['billing', 'customers', 'gateway', 'orders', 'shared']) expect(screen.getByTestId(`node-repo:${r}`)).toHaveTextContent(r);
    expect(document.querySelector('[data-ready="true"]')).not.toBeNull();
  });
  test('click selects, double-click drills, empty canvas clears', async () => {
    const p = await setup();
    fireEvent.click(screen.getByTestId('node-repo:orders'));
    expect(p.onSelect).toHaveBeenLastCalledWith('repo:orders');
    fireEvent.doubleClick(screen.getByTestId('node-repo:orders'));
    expect(p.onDrill).toHaveBeenCalledWith('repo:orders');
  });
  test('hover dims nodes that are not neighbours', async () => {
    await setup();
    fireEvent.mouseEnter(screen.getByTestId('node-repo:gateway').closest('.react-flow__node')!);
    expect(screen.getByTestId('node-repo:shared')).toHaveClass('dim');
    expect(screen.getByTestId('node-repo:orders')).not.toHaveClass('dim');
  });
  test('without positions nothing is drawn and the map is not ready', () => {
    render(<MapCanvas view={view} positions={null} selection={null} highlight="both" impact={null} colors={colors} onSelect={vi.fn()} onDrill={vi.fn()} onMove={vi.fn()} />);
    expect(document.querySelector('[data-ready="true"]')).toBeNull();
  });
});
```

- [ ] **Step 2: Run to verify failure**

Run: `cd web && npx vitest run test/geometry.test.ts test/neighbours.test.ts test/MapCanvas.test.tsx`
Expected: FAIL, because the modules don't exist yet.

- [ ] **Step 3: Implement**

`web/src/map/geometry.ts`:

```ts
/** Quadratic edge from (sx,sy) to (tx,ty). A two-way pair bends further, to opposite sides, with labels near each source. */
export function curve(sx: number, sy: number, tx: number, ty: number, paired: boolean): { d: string; labelX: number; labelY: number } {
  const len = Math.hypot(tx - sx, ty - sy) || 1;
  const bend = paired ? 46 : Math.min(len * 0.1, 22);
  const mx = (sx + tx) / 2, my = (sy + ty) / 2;
  const cx = mx - ((ty - sy) / len) * bend, cy = my + ((tx - sx) / len) * bend;
  const t = paired ? 0.3 : 0.5;
  const q = (a: number, c: number, b: number) => (1 - t) ** 2 * a + 2 * t * (1 - t) * c + t * t * b;
  const r = (v: number) => Math.round(v * 100) / 100;
  return { d: `M${r(sx)},${r(sy)} Q${r(cx)},${r(cy)} ${r(tx)},${r(ty)}`, labelX: q(sx, cx, tx), labelY: q(sy, cy, ty) };
}

export const strokeWidth = (calls: number): number => Math.min(1.4 + 0.6 * calls, 5);
```

`web/src/map/neighbours.ts`:

```ts
import type { Highlight } from '../app/state';
import { ordinal } from '../graph/graphIndex';
import type { View, ViewEdge } from '../graph/views';
import type { Positions } from '../layout/engine';

/** Impact mode marks: the target's node(s), affected nodes with their affected call-site counts, and hot edge ids. */
export interface ImpactMarks { source: Set<string>; hit: Map<string, number>; edges: Set<string> }

export function related(view: View, id: string, highlight: Highlight): Set<string> {
  const s = new Set([id]);
  for (const e of view.edges) {
    if (highlight !== 'up' && e.from === id) s.add(e.to);
    if (highlight !== 'down' && e.to === id) s.add(e.from);
  }
  return s;
}

export function nodeClass(id: string, ctx: { selection: string | null; near: Set<string> | null; impact: ImpactMarks | null; added?: Set<string> }): string {
  const c: string[] = [];
  if (id === ctx.selection) c.push('selected');
  if (ctx.impact) c.push(ctx.impact.source.has(id) ? 'src' : ctx.impact.hit.has(id) ? 'hit' : 'fade');
  else if (ctx.near && !ctx.near.has(id)) c.push('dim');
  if (ctx.added?.has(id)) c.push('added');
  return c.join(' ');
}

export function edgeClass(e: ViewEdge, ctx: { focusId: string | null; highlight: Highlight; impact: ImpactMarks | null }): { className: string; marker: string; labelClass: string } {
  const tone = ctx.impact ? (ctx.impact.edges.has(e.id) ? 'hot' : 'fade') : e.tone === 'warn' ? 'warn' : '';
  const touches = ctx.focusId !== null && ((ctx.highlight !== 'up' && e.from === ctx.focusId) || (ctx.highlight !== 'down' && e.to === ctx.focusId));
  const dim = !ctx.impact && ctx.focusId !== null && !touches ? 'dim' : '';
  const classes = [tone, e.dashed ? 'dashed' : '', dim].filter(Boolean);
  return {
    className: classes.join(' '),
    marker: tone === 'hot' ? 'dk-arrow-bad' : tone === 'warn' ? 'dk-arrow-warn' : 'dk-arrow',
    labelClass: [tone, dim].filter(Boolean).join(' '),
  };
}

/** Arrow keys: right = first dependency, left = first dependent, up/down = previous/next node in the same column or container. */
export function nextByKey(view: View, positions: Positions | null, selection: string | null, key: string): string | null {
  if (!['ArrowRight', 'ArrowLeft', 'ArrowUp', 'ArrowDown'].includes(key)) return null;
  if (selection === null) return [...view.nodes].map(n => n.id).sort(ordinal)[0] ?? null;
  if (key === 'ArrowRight') return view.edges.filter(e => e.from === selection).map(e => e.to).sort(ordinal)[0] ?? null;
  if (key === 'ArrowLeft') return view.edges.filter(e => e.to === selection).map(e => e.from).sort(ordinal)[0] ?? null;
  const me = view.nodes.find(n => n.id === selection);
  if (!me) return null;
  const y = (id: string) => positions?.nodes[id]?.y ?? 0;
  const column = view.nodes
    .filter(n => n.parent === me.parent && n.column === me.column && (me.column !== undefined || Math.abs((positions?.nodes[n.id]?.x ?? 0) - (positions?.nodes[me.id]?.x ?? 0)) < 40))
    .sort((a, b) => y(a.id) - y(b.id) || ordinal(a.id, b.id));
  const i = column.findIndex(n => n.id === selection);
  return column[key === 'ArrowDown' ? i + 1 : i - 1]?.id ?? null;
}
```

`web/src/map/nodes.tsx`:

```tsx
import { Handle, Position, type Node, type NodeProps } from '@xyflow/react';
import type { ViewNode } from '../graph/views';

export type DepNodeData = { node: ViewNode; color?: string; className: string; hitCount?: number };
export type ContainerData = { label: string; color: string };

export function DepNode({ data }: NodeProps<Node<DepNodeData, 'dep'>>) {
  const n = data.node;
  return (
    <div className={`dk-node ${n.kind} ${data.className}`} data-testid={`node-${n.id}`} title={n.label}>
      <Handle type="target" position={Position.Left} isConnectable={false} />
      {data.color && <span className="stripe" style={{ background: data.color }} />}
      {n.tag && <span className="tag">{n.tag}</span>}
      <span className="title">{n.verb && <span className={`verb ${n.verb}`}>{n.verb}</span>}{n.label}</span>
      {n.meta && <span className="meta">{n.meta}</span>}
      {data.hitCount
        ? <span className="badge bad">{data.hitCount}</span>
        : n.badge && <span className={`badge ${n.badge.tone}`}>{n.badge.text}</span>}
      <Handle type="source" position={Position.Right} isConnectable={false} />
    </div>
  );
}

export function ContainerNode({ data }: NodeProps<Node<ContainerData, 'container'>>) {
  return <div className="dk-container"><i className="dot" style={{ background: data.color }} />{data.label}</div>;
}
```

`web/src/map/edges.tsx`:

```tsx
import { BaseEdge, EdgeLabelRenderer, type Edge, type EdgeProps } from '@xyflow/react';
import { curve, strokeWidth } from './geometry';

export type DepEdgeData = { label?: string; weight: number; paired: boolean; marker: string; labelClass: string };

export function DepEdge({ id, sourceX, sourceY, targetX, targetY, data }: EdgeProps<Edge<DepEdgeData, 'dep'>>) {
  const { d, labelX, labelY } = curve(sourceX, sourceY, targetX, targetY, data?.paired ?? false);
  return (
    <>
      <BaseEdge id={id} path={d} markerEnd={`url(#${data?.marker ?? 'dk-arrow'})`} style={{ strokeWidth: strokeWidth(data?.weight ?? 0) }} />
      {data?.label && (
        <EdgeLabelRenderer>
          <div className={`dk-edge-label ${data.labelClass}`} style={{ transform: `translate(-50%, -50%) translate(${labelX}px, ${labelY}px)` }}>
            {data.label}
          </div>
        </EdgeLabelRenderer>
      )}
    </>
  );
}
```

`web/src/map/MapCanvas.tsx`:

```tsx
import {
  Background, BackgroundVariant, Controls, ReactFlow, ReactFlowProvider, useNodesState, useReactFlow, type Edge, type Node,
} from '@xyflow/react';
import '@xyflow/react/dist/base.css';
import { useEffect, useMemo, useRef, useState } from 'react';
import type { Highlight } from '../app/state';
import { repoColor } from '../graph/colors';
import type { View } from '../graph/views';
import type { Positions } from '../layout/engine';
import { DepEdge, type DepEdgeData } from './edges';
import { edgeClass, nextByKey, nodeClass, related, type ImpactMarks } from './neighbours';
import { ContainerNode, DepNode, type ContainerData, type DepNodeData } from './nodes';

const nodeTypes = { dep: DepNode, container: ContainerNode };
const edgeTypes = { dep: DepEdge };

export interface MapCanvasProps {
  view: View; positions: Positions | null; selection: string | null; highlight: Highlight;
  impact: ImpactMarks | null; colors: Map<string, number>; added?: Set<string>;
  onSelect(id: string | null): void; onDrill(id: string): void; onMove(id: string, x: number, y: number): void;
}

export function MapCanvas(props: MapCanvasProps) {
  return <ReactFlowProvider><Canvas {...props} /></ReactFlowProvider>;
}

function Markers() {
  const arrow = (id: string, color: string) => (
    <marker id={id} viewBox="0 0 10 10" refX="9" refY="5" markerWidth="7" markerHeight="7" orient="auto-start-reverse">
      <path d="M0 0 10 5 0 10z" style={{ fill: color }} />
    </marker>
  );
  return (
    <svg width="0" height="0" style={{ position: 'absolute' }} aria-hidden="true">
      <defs>{arrow('dk-arrow', 'var(--ink-3)')}{arrow('dk-arrow-warn', 'var(--warn)')}{arrow('dk-arrow-bad', 'var(--bad)')}</defs>
    </svg>
  );
}

function Canvas({ view, positions, selection, highlight, impact, colors, added, onSelect, onDrill, onMove }: MapCanvasProps) {
  const [hover, setHover] = useState<string | null>(null);
  const dragging = useRef(false);
  const live = useRef(new Map<string, { x: number; y: number }>());
  const { fitView } = useReactFlow();
  const fittedFor = useRef<string | null>(null);
  const focusId = hover ?? selection;
  const near = useMemo(() => (focusId ? related(view, focusId, highlight) : null), [view, focusId, highlight]);

  useEffect(() => { live.current.clear(); }, [positions]);

  const nodes = useMemo<Node[]>(() => {
    if (!positions) return [];
    const box = (id: string) => positions.nodes[id] ?? { x: 0, y: 0, width: 0, height: 0 };
    const at = (id: string) => live.current.get(id) ?? { x: box(id).x, y: box(id).y };
    const containers = view.containers.map((c): Node<ContainerData, 'container'> => ({
      id: c.id, type: 'container', position: at(c.id), draggable: false, selectable: false,
      style: { width: box(c.id).width, height: box(c.id).height }, data: { label: c.label, color: repoColor(colors, c.repo) },
    }));
    const deps = view.nodes.map((n): Node<DepNodeData, 'dep'> => ({
      id: n.id, type: 'dep', position: at(n.id), parentId: n.parent, extent: n.parent ? 'parent' : undefined,
      style: { width: box(n.id).width, height: box(n.id).height },
      data: {
        node: n, color: n.repo ? repoColor(colors, n.repo) : undefined,
        className: nodeClass(n.id, { selection, near, impact, added }),
        hitCount: impact?.hit.get(n.id),
      },
    }));
    return [...containers, ...deps];
  }, [view, positions, colors, selection, near, impact, added]);

  const [rfNodes, setRfNodes, onNodesChange] = useNodesState<Node>(nodes);
  useEffect(() => setRfNodes(nodes), [nodes, setRfNodes]);

  const edges = useMemo<Edge[]>(() => {
    const pairs = new Set(view.edges.map(e => `${e.from}>${e.to}`));
    return view.edges.map((e): Edge<DepEdgeData, 'dep'> => {
      const cls = edgeClass(e, { focusId, highlight, impact });
      return {
        id: e.id, source: e.from, target: e.to, type: 'dep', className: cls.className, selectable: false,
        data: { label: e.label, weight: e.weight, paired: pairs.has(`${e.to}>${e.from}`), marker: cls.marker, labelClass: cls.labelClass },
      };
    });
  }, [view, focusId, highlight, impact]);

  // fit once per view (not on every live refresh, which must keep the user's viewport)
  const viewKey = `${view.depth}|${view.nodes.length}|${view.nodes[0]?.id ?? ''}`;
  useEffect(() => {
    if (!positions || fittedFor.current === viewKey) return;
    fittedFor.current = viewKey;
    requestAnimationFrame(() => fitView({ padding: 0.15, duration: 0 }));
  }, [positions, viewKey, fitView]);

  return (
    <div className="map" style={{ width: '100%', height: '100%' }} data-ready={positions ? 'true' : 'false'} tabIndex={0}
      aria-label="Dependency map" role="application"
      onKeyDown={e => {
        const next = nextByKey(view, positions, selection, e.key);
        if (next) { e.preventDefault(); onSelect(next); }
      }}>
      <Markers />
      <ReactFlow
        nodes={rfNodes} edges={edges} nodeTypes={nodeTypes} edgeTypes={edgeTypes} onNodesChange={onNodesChange}
        onNodeClick={(_, n) => n.type === 'dep' && onSelect(n.id)}
        onNodeDoubleClick={(_, n) => n.type === 'dep' && onDrill(n.id)}
        onPaneClick={() => onSelect(null)}
        onNodeMouseEnter={(_, n) => { if (!dragging.current && n.type === 'dep') setHover(n.id); }}
        onNodeMouseLeave={() => { if (!dragging.current) setHover(null); }}
        onNodeDragStart={() => { dragging.current = true; setHover(null); }}
        onNodeDrag={(_, n) => live.current.set(n.id, n.position)}
        onNodeDragStop={(_, n) => { dragging.current = false; live.current.set(n.id, n.position); onMove(n.id, n.position.x, n.position.y); }}
        nodesConnectable={false} deleteKeyCode={null} zoomOnDoubleClick={false} minZoom={0.1} maxZoom={2.5}
        proOptions={{ hideAttribution: true }}
      >
        <Background variant={BackgroundVariant.Dots} gap={22} size={1.1} />
        <Controls showInteractive={false} position="bottom-right" />
      </ReactFlow>
    </div>
  );
}
```

`web/src/map/MapStage.tsx`:

```tsx
import { useCallback, useEffect, useMemo, useState } from 'react';
import { KEYS, load, save } from '../app/persist';
import { buildView } from '../graph/views';
import { layoutView, type LayoutEngine, type Positions } from '../layout/engine';
import type { PanelProps } from '../panels/types';
import { MapCanvas } from './MapCanvas';
import type { ImpactMarks } from './neighbours';

type Saved = Record<string, { x: number; y: number }>;
let readyMarked = false;

export function MapStage({ ix, state, dispatch, colors, engine, workspaceKey, impact, added }: PanelProps & {
  engine: LayoutEngine; workspaceKey: string; impact: ImpactMarks | null; added?: Set<string>;
}) {
  const view = useMemo(() => buildView(ix, state.depth, state.focus, state.filters), [ix, state.depth, state.focus, state.filters]);
  const [positions, setPositions] = useState<Positions | null>(null);
  const [resetCount, setResetCount] = useState(0);
  const key = KEYS.positions(workspaceKey, view.depth);

  useEffect(() => {
    let current = true;
    layoutView(engine, view).then(p => {
      if (!current) return;
      const saved = load<Saved>(key, {});
      for (const [id, xy] of Object.entries(saved)) if (p.nodes[id]) p.nodes[id] = { ...p.nodes[id]!, ...xy };
      setPositions(p);
      if (!readyMarked) { readyMarked = true; performance.mark?.('depenk-ready'); }
    }).catch(err => console.error('depenk: layout failed', err));
    return () => { current = false; };
  }, [engine, view, key, resetCount]);

  const onMove = useCallback((id: string, x: number, y: number) => {
    save(key, { ...load<Saved>(key, {}), [id]: { x, y } });
    setPositions(p => (p && p.nodes[id] ? { nodes: { ...p.nodes, [id]: { ...p.nodes[id]!, x, y } } } : p));
  }, [key]);

  return (
    <>
      <div className="floating">
        {state.trail.length > 0 && <button type="button" className="chip" onClick={() => dispatch({ type: 'back' })}>← Back</button>}
        <button type="button" className="chip" onClick={() => { save(key, null); setResetCount(c => c + 1); }}>Reset layout</button>
      </div>
      {view.notice && <div className="notice" role="note">{view.notice}</div>}
      <MapCanvas
        view={view} positions={positions} selection={state.selection} highlight={state.highlight} impact={impact} colors={colors} added={added}
        onSelect={id => dispatch({ type: 'select', id })}
        onDrill={id => { const n = ix.tryGet(id); if (n) dispatch({ type: 'drill', id, kind: n.kind }); }}
        onMove={onMove}
      />
    </>
  );
}
```

In `web/src/app/App.tsx`:
- add `import { MapStage } from '../map/MapStage';`
- delete the line `void engine; void mode; void bundleHtml; // used from Tasks 8 and 11`, and add `void mode; void bundleHtml; // used from Task 11` in its place
- replace

```tsx
        <main className="stage" data-testid="stage">
          <NotFound {...panel} />
        </main>
```

with

```tsx
        <main className="stage" data-testid="stage">
          <MapStage {...panel} engine={engine} workspaceKey={graph.workspace ?? 'default'} impact={null} />
          <NotFound {...panel} />
        </main>
```

- [ ] **Step 4: Run tests, typecheck and build**

Run: `cd web && npx vitest run && npm run build`
Expected: PASS. The build now inlines the layout worker, and `check-bundle: ok` still reports a single file.

- [ ] **Step 5: After screenshot and a manual check**

Screenshot the inlined fixture page at Repo depth (light and dark), then at `#/repo:orders` after choosing Project depth, then at `#/ep:orders:GET:/api/orders/%7Bid%7D`. Show the user the before and after screenshots. Check by eye that the billing ⇄ orders pair curves apart and that nothing overlaps.

- [ ] **Step 6: Commit**

```bash
git add web
git commit -m "feat(web): React Flow map with ServiceMap-style nodes and edges, hover highlight, drill-in, drag, keyboard

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 9: Inspector: per-kind tabs, contract model tree, callers

**Files:**
- Create: `web/src/graph/details.ts`, `web/src/panels/ModelTreeView.tsx`
- Modify: `web/src/panels/Inspector.tsx` (full rewrite)
- Test: `web/test/details.test.ts`, `web/test/Inspector.test.tsx`

**Interfaces:**
- Consumes: `GraphIndex`, `relax`, `endpointsUsingModel` (Task 4); `PanelProps`, `placeFor` (Task 7).
- Produces (`details.ts`):
  - `CallerRow { callSiteId; repo; member; line; clientMethodId; clientMethodLabel; endpointId: string | null; endpointLabel: string | null; confidence }`
  - `repoDetail(ix, id)` → `{ repo; projects; publishes: { packageNodeId; packageId; version; consumers: { projectId; repo; version }[] }[]; dependsOn: RepoLinkRow[]; dependents: RepoLinkRow[]; calledBy: CallerRow[]; endpoints }`, where `RepoLinkRow { repoId; repo; via: string[]; calls }`
  - `projectDetail(ix, id)` → `{ project; publishes: { packageNodeId; packageId; version } | null; references: { packageNodeId; packageId; version; producerRepo }[]; endpoints; clientMethods }`
  - `endpointDetail(ix, id)` → `{ endpoint; request: ModelLink[]; responses: { statusCode; typeName; models: ModelLink[] }[]; clientMethods: { id; label; signature; packageId; version; confidence }[]; callers: CallerRow[] }`, where `ModelLink { id; fullName; repo; crossRepo }`
  - `packageDetail(ix, id)` → `{ pkg; producer: ProjectNode | null; latest: string | null; consumers: { projectId; project; repo; version }[]; versions: { version; count; behind }[] }`
  - `modelTree(ix, id, depth = 2): ModelTreeData`, where `ModelTreeData { id; fullName; kind; repo; enumValues?; fields: FieldTree[] }` and `FieldTree { name; typeName; nullable; collection; crossRepo; typeIds: string[]; types: ModelTreeData[] | null }`
  - `modelUsedBy(ix, id)` → `{ endpoints: { id; label; repo; relation: 'accepts' | 'returns' | 'nested' }[]; containedIn: string[] }`
- Produces (`Inspector.tsx`):
  - tab buttons use `role="tab"`; tab names are as spec §4.1
  - every kind has a "What breaks if this changes?" button, which dispatches `enterImpact` with the node id
  - a model's heading (`h2`) is its simple name

- [ ] **Step 0: Before screenshot** with a node selected (e.g. `#/ep:orders:GET:/api/orders/%7Bid%7D`), as in Task 8.

- [ ] **Step 1: Write the failing tests**

`web/test/details.test.ts`:

```ts
import { expect, test } from 'vitest';
import { endpointDetail, modelTree, modelUsedBy, packageDetail, projectDetail, repoDetail } from '../src/graph/details';
import { GraphIndex } from '../src/graph/graphIndex';
import { fixtureGraph } from './util/fixture';

const ix = new GraphIndex(fixtureGraph());

test('repoDetail: publishes, links and callers from other repos', () => {
  const d = repoDetail(ix, 'repo:orders');
  expect(d.projects.map(p => p.name)).toEqual(['Orders.Api', 'Orders.Client', 'Orders.Tests']);
  expect(d.publishes).toEqual([{ packageNodeId: 'pkg:Orders.Client', packageId: 'Orders.Client', version: '3.4.1',
    consumers: [{ projectId: 'proj:billing/Billing.Api', repo: 'billing', version: '3.2.0' }, { projectId: 'proj:gateway/Gateway.Api', repo: 'gateway', version: '3.4.1' }] }]);
  expect(d.dependents.map(l => l.repo)).toEqual(['billing', 'gateway']);
  expect(d.calledBy.map(c => `${c.repo}:${c.member}`)).toEqual(['billing:InvoiceBuilder.BuildAsync', 'gateway:OrdersProxy.Create', 'gateway:OrdersProxy.List']);
  expect(d.endpoints).toHaveLength(4);
});

test('projectDetail: references with producer repo', () => {
  const d = projectDetail(ix, 'proj:billing/Billing.Api');
  expect(d.references.map(r => `${r.packageId}@${r.version}→${r.producerRepo}`)).toEqual(['Customers.Client@2.0.0→customers', 'Orders.Client@3.2.0→orders']);
  expect(projectDetail(ix, 'proj:orders/Orders.Client').publishes).toEqual({ packageNodeId: 'pkg:Orders.Client', packageId: 'Orders.Client', version: '3.4.1' });
});

test('endpointDetail: contract, client methods and callers', () => {
  const d = endpointDetail(ix, 'ep:orders:GET:/api/orders/{id}');
  expect(d.responses[0]!.models.map(m => m.fullName)).toEqual(['Acme.Orders.Client.OrderDto']);
  expect(d.clientMethods.map(c => `${c.label}@${c.packageId}`)).toEqual(['IOrdersClient.GetOrderAsync@Orders.Client', 'OrdersClient.GetOrderAsync@Orders.Client']);
  expect(d.callers.map(c => c.member)).toEqual(['InvoiceBuilder.BuildAsync']);
});

test('packageDetail: versions behind latest are flagged', () => {
  const d = packageDetail(ix, 'pkg:Orders.Client');
  expect(d.latest).toBe('3.4.1');
  expect(d.versions).toEqual([{ version: '3.2.0', count: 1, behind: true }, { version: '3.4.1', count: 1, behind: false }]);
});

test('modelTree marks cross-repo fields and expands nested models', () => {
  const t = modelTree(ix, 'model:Orders.Client:Acme.Orders.Client.OrderDto');
  const f = (name: string) => t.fields.find(x => x.name === name)!;
  expect(f('Customer')).toMatchObject({ crossRepo: true, typeIds: ['model:Customers.Client:Acme.Customers.Client.CustomerDto'] });
  expect(f('Lines').types![0]!.fields.map(x => x.name)).toEqual(['Sku', 'Qty', 'UnitPrice']);
  expect(f('Lines').types![0]!.fields[2]!.types).not.toBeNull(); // depth 2 reaches Money
  expect(modelTree(ix, 'model:Orders.Client:Acme.Orders.Client.OrderDto', 0).fields[3]!.types).toBeNull();
});

test('modelUsedBy distinguishes direct and nested use', () => {
  const u = modelUsedBy(ix, 'model:Orders.Client:Acme.Orders.Client.OrderLineDto');
  expect(u.endpoints.every(e => e.relation === 'nested')).toBe(true);
  expect(u.containedIn).toEqual(['model:Orders.Client:Acme.Orders.Client.CreateOrderRequest', 'model:Orders.Client:Acme.Orders.Client.OrderDto']);
});
```

`web/test/Inspector.test.tsx`:

```tsx
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { expect, test } from 'vitest';
import { App } from '../src/app/App';
import { fixtureGraph } from './util/fixture';
import { inThreadEngine } from './util/inThreadEngine';

const open = (hash: string) => { window.location.hash = hash; render(<App graph={fixtureGraph()} engine={inThreadEngine()} mode="export" />); };
const inspector = () => screen.getByRole('complementary', { name: 'Inspector' });

test('endpoint: contract tree, cross-repo jump, callers tab', async () => {
  open('#/ep:orders:GET:/api/orders/%7Bid%7D');
  const rail = inspector();
  expect(within(rail).getByRole('heading', { level: 2 })).toHaveTextContent('/api/orders/{id}');
  expect(within(rail).getByText('OrderDto')).toBeInTheDocument();
  await userEvent.click(within(rail).getByRole('tab', { name: /Callers/ }));
  expect(within(rail).getByText('InvoiceBuilder.BuildAsync')).toBeInTheDocument();
  await userEvent.click(within(rail).getByRole('tab', { name: 'Contract' }));
  await userEvent.click(within(rail).getByRole('button', { name: '↗ customers' }));
  expect(within(inspector()).getByRole('heading', { level: 2, name: 'CustomerDto' })).toBeInTheDocument();
});

test('repo: called-by tab lists call sites in other repos', async () => {
  open('#/repo:orders');
  await userEvent.click(within(inspector()).getByRole('tab', { name: /Called by/ }));
  expect(within(inspector()).getAllByRole('listitem')).toHaveLength(3);
});

test('impact button enters impact mode for the selection', async () => {
  open('#/repo:billing');
  await userEvent.click(within(inspector()).getByRole('button', { name: 'What breaks if this changes?' }));
  expect(window.location.hash).toBe('#/impact/repo:billing');
});
```

- [ ] **Step 2: Run to verify failure**

Run: `cd web && npx vitest run test/details.test.ts test/Inspector.test.tsx`
Expected: FAIL, because `../src/graph/details` cannot be resolved.

- [ ] **Step 3: Implement**

`web/src/graph/details.ts`:

```ts
import { GraphIndex, type Hop, ordinal } from './graphIndex';
import { endpointsUsingModel, relax } from './queries';
import type { Confidence, EndpointNode, ModelKind, ProjectNode } from './types';

export interface CallerRow {
  callSiteId: string; repo: string; member: string; line: number; clientMethodId: string; clientMethodLabel: string;
  endpointId: string | null; endpointLabel: string | null; confidence: Confidence;
}

function callersOf(ix: GraphIndex, clientMethodIds: readonly string[]): CallerRow[] {
  const rows: CallerRow[] = [];
  for (const cmId of clientMethodIds) {
    const cm = ix.clientMethods.get(cmId);
    if (!cm) continue;
    const epId = ix.dependenciesOf(cmId).find(h => h.kind === 'targets')?.to ?? null;
    for (const h of ix.dependentsOf(cmId)) {
      const cs = h.kind === 'invokes' ? ix.callSites.get(h.from) : undefined;
      if (!cs) continue;
      rows.push({
        callSiteId: cs.id, repo: cs.repo, member: cs.containingMember, line: cs.location.line,
        clientMethodId: cm.id, clientMethodLabel: `${cm.typeName}.${cm.methodName}`,
        endpointId: epId, endpointLabel: epId ? ix.tryGet(epId)?.label ?? null : null, confidence: h.confidence,
      });
    }
  }
  return rows.sort((a, b) => ordinal(a.repo, b.repo) || ordinal(a.member, b.member) || a.line - b.line);
}

const byRoute = (a: EndpointNode, b: EndpointNode) => ordinal(a.route, b.route) || ordinal(a.verb, b.verb);

export interface RepoLinkRow { repoId: string; repo: string; via: string[]; calls: number }

export function repoDetail(ix: GraphIndex, id: string) {
  const repo = ix.repos.get(id)!;
  const projects = [...ix.projects.values()].filter(p => p.repo === repo.name).sort((a, b) => ordinal(a.id, b.id));
  const ids = new Set(projects.map(p => p.id));
  const publishes = ix.graph.edges
    .filter(e => e.kind === 'produces' && ids.has(e.from))
    .map(e => ({
      packageNodeId: e.to,
      packageId: ix.packages.get(e.to)?.packageId ?? e.to,
      version: e.version ?? null,
      consumers: ix.dependentsOf(e.to).filter(h => h.kind === 'references')
        .map(h => ({ projectId: h.from, repo: ix.projects.get(h.from)?.repo ?? '', version: h.edge.version ?? null }))
        .sort((a, b) => ordinal(a.projectId, b.projectId)),
    }))
    .sort((a, b) => ordinal(a.packageId, b.packageId));
  const link = (h: Hop, other: string): RepoLinkRow => ({ repoId: other, repo: ix.tryGet(other)?.label ?? other, via: h.edge.viaPackages ?? [], calls: h.edge.callCount ?? 0 });
  const dependsOn = ix.dependenciesOf(id).filter(h => h.kind === 'dependsOn').map(h => link(h, h.to)).sort((a, b) => ordinal(a.repo, b.repo));
  const dependents = ix.dependentsOf(id).filter(h => h.kind === 'dependsOn').map(h => link(h, h.from)).sort((a, b) => ordinal(a.repo, b.repo));
  const ownMethods = [...ix.clientMethods.values()].filter(c => c.repo === repo.name).map(c => c.id);
  const calledBy = callersOf(ix, ownMethods).filter(c => c.repo !== repo.name);
  const endpoints = [...ix.endpoints.values()].filter(e => e.repo === repo.name).sort(byRoute);
  return { repo, projects, publishes, dependsOn, dependents, calledBy, endpoints };
}

export function projectDetail(ix: GraphIndex, id: string) {
  const project = ix.projects.get(id)!;
  const produced = ix.dependentsOf(id).find(h => h.kind === 'produces');
  const references = ix.dependenciesOf(id).filter(h => h.kind === 'references').map(h => {
    const pkg = ix.packages.get(h.to);
    const producer = pkg?.producerProjectIds[0];
    return { packageNodeId: h.to, packageId: pkg?.packageId ?? h.to, version: h.edge.version ?? null, producerRepo: producer ? ix.projects.get(producer)?.repo ?? null : null };
  }).sort((a, b) => ordinal(a.packageId, b.packageId));
  return {
    project,
    publishes: produced ? { packageNodeId: produced.from, packageId: ix.packages.get(produced.from)?.packageId ?? produced.from, version: produced.edge.version ?? null } : null,
    references,
    endpoints: [...ix.endpoints.values()].filter(e => e.projectId === id).sort(byRoute),
    clientMethods: [...ix.clientMethods.values()].filter(c => c.projectId === id).sort((a, b) => ordinal(a.id, b.id)),
  };
}

export interface ModelLink { id: string; fullName: string; repo: string; crossRepo: boolean }

export function endpointDetail(ix: GraphIndex, id: string) {
  const endpoint = ix.endpoints.get(id)!;
  const deps = ix.dependenciesOf(id);
  const link = (modelId: string): ModelLink[] => {
    const m = ix.models.get(modelId);
    return m ? [{ id: m.id, fullName: m.fullName, repo: m.repo, crossRepo: !!m.repo && m.repo !== endpoint.repo }] : [];
  };
  const request = deps.filter(h => h.kind === 'accepts').flatMap(h => link(h.to));
  const responses = endpoint.responses.map(r => ({
    statusCode: r.statusCode, typeName: r.typeName,
    models: deps.filter(h => h.kind === 'returns' && (h.edge.statusCode ?? 200) === r.statusCode).flatMap(h => link(h.to)),
  }));
  const clientMethods = ix.dependentsOf(id).filter(h => h.kind === 'targets' && ix.clientMethods.has(h.from)).map(h => {
    const cm = ix.clientMethods.get(h.from)!;
    const produced = ix.dependentsOf(cm.projectId).find(x => x.kind === 'produces');
    return {
      id: cm.id, label: `${cm.typeName}.${cm.methodName}`, signature: cm.signature,
      packageId: produced ? ix.packages.get(produced.from)?.packageId ?? null : null, version: produced?.edge.version ?? null, confidence: h.confidence,
    };
  }).sort((a, b) => ordinal(a.id, b.id));
  return { endpoint, request, responses, clientMethods, callers: callersOf(ix, clientMethods.map(c => c.id)) };
}

export function packageDetail(ix: GraphIndex, id: string) {
  const pkg = ix.packages.get(id)!;
  const producer: ProjectNode | null = pkg.producerProjectIds.map(p => ix.projects.get(p)).find(p => p !== undefined) ?? null;
  const latest = ix.dependenciesOf(id).find(h => h.kind === 'produces')?.edge.version ?? null;
  const consumers = ix.dependentsOf(id).filter(h => h.kind === 'references').map(h => {
    const p = ix.projects.get(h.from);
    return { projectId: h.from, project: p?.name ?? h.from, repo: p?.repo ?? '', version: h.edge.version ?? null };
  }).sort((a, b) => ordinal(a.projectId, b.projectId));
  const counts = new Map<string, number>();
  for (const c of consumers) counts.set(c.version ?? '?', (counts.get(c.version ?? '?') ?? 0) + 1);
  const versions = [...counts].map(([version, count]) => ({ version, count, behind: latest !== null && version !== latest }))
    .sort((a, b) => ordinal(a.version, b.version));
  return { pkg, producer, latest, consumers, versions };
}

export interface FieldTree { name: string; typeName: string; nullable: boolean; collection: boolean; crossRepo: boolean; typeIds: string[]; types: ModelTreeData[] | null }
export interface ModelTreeData { id: string; fullName: string; kind: ModelKind; repo: string; enumValues?: string[]; fields: FieldTree[] }

/** Field tree, expanding nested models `depth` levels (cycles become stubs), as C# QueryService.GetModel. */
export function modelTree(ix: GraphIndex, id: string, depth = 2, path = new Set<string>()): ModelTreeData {
  const m = ix.models.get(id)!;
  path.add(id);
  const childHops = ix.dependenciesOf(id).filter(h => h.kind === 'fieldOf' && ix.models.has(h.to));
  const fields = m.fields.map((f): FieldTree => {
    const typeIds = childHops.filter(h => h.edge.fieldName === f.name).map(h => h.to);
    const crossRepo = typeIds.some(t => { const r = ix.models.get(t)!.repo; return !!r && r !== m.repo; });
    const types = depth > 0 && typeIds.length > 0
      ? typeIds.map(t => (path.has(t) ? stub(ix, t) : modelTree(ix, t, depth - 1, path)))
      : null;
    return { name: f.name, typeName: f.typeName, nullable: f.nullable, collection: f.collection, crossRepo, typeIds, types };
  });
  path.delete(id);
  return { id: m.id, fullName: m.fullName, kind: m.kind, repo: m.repo, enumValues: m.enumValues, fields };
}

function stub(ix: GraphIndex, id: string): ModelTreeData {
  const m = ix.models.get(id)!;
  return { id: m.id, fullName: m.fullName, kind: m.kind, repo: m.repo, enumValues: m.enumValues, fields: [] };
}

export function modelUsedBy(ix: GraphIndex, id: string) {
  const direct = new Map<string, 'accepts' | 'returns'>();
  for (const h of ix.dependentsOf(id))
    if ((h.kind === 'accepts' || h.kind === 'returns') && ix.endpoints.has(h.from) && !direct.has(h.from)) direct.set(h.from, h.kind);
  const endpoints = endpointsUsingModel(ix, id).map(epId => {
    const n = ix.get(epId);
    return { id: epId, label: n.label, repo: n.repo ?? '', relation: direct.get(epId) ?? ('nested' as const) };
  });
  const containedIn = [...relax(ix, id, h => h.kind === 'fieldOf').keys()].filter(k => k !== id).sort(ordinal);
  return { endpoints, containedIn };
}
```

`web/src/panels/ModelTreeView.tsx`:

```tsx
import { useState } from 'react';
import { modelTree, type ModelTreeData } from '../graph/details';
import type { GraphIndex } from '../graph/graphIndex';

const simple = (fullName: string) => fullName.slice(fullName.lastIndexOf('.') + 1);

export function ModelTreeView({ ix, tree, onOpen, highlightField }: {
  ix: GraphIndex; tree: ModelTreeData; onOpen: (modelId: string) => void; highlightField?: string | null;
}) {
  return (
    <div className="tree">
      <div>▾ <b>{simple(tree.fullName)}</b> <span className="t">{tree.kind} · {tree.repo}</span></div>
      <Fields ix={ix} tree={tree} level={1} onOpen={onOpen} highlightField={highlightField} />
    </div>
  );
}

function Fields({ ix, tree, level, onOpen, highlightField }: { ix: GraphIndex; tree: ModelTreeData; level: number; onOpen: (id: string) => void; highlightField?: string | null }) {
  // nested same-repo types start open at the first level; cross-repo types start closed (one click to jump instead)
  const [open, setOpen] = useState<Set<string>>(() => new Set(tree.fields.filter(f => level === 1 && f.types && !f.crossRepo).map(f => f.name)));
  const [loaded, setLoaded] = useState<Record<string, ModelTreeData[]>>({});
  if (tree.enumValues?.length) return <div style={{ paddingLeft: level * 18 }} className="t">{tree.enumValues.join(' | ')}</div>;
  return (
    <>
      {tree.fields.map(f => {
        const types = f.types ?? loaded[f.name] ?? null;
        const isOpen = open.has(f.name);
        const toggle = () => {
          if (!types) setLoaded(l => ({ ...l, [f.name]: f.typeIds.map(t => modelTree(ix, t, 1)) }));
          setOpen(s => { const n = new Set(s); if (n.has(f.name)) n.delete(f.name); else n.add(f.name); return n; });
        };
        const repo = f.crossRepo ? ix.models.get(f.typeIds[0]!)?.repo : null;
        return (
          <div key={f.name}>
            <div style={{ paddingLeft: level * 18 }}>
              {f.typeIds.length > 0
                ? <button type="button" className="twist" aria-expanded={isOpen} aria-label={`${isOpen ? 'Collapse' : 'Expand'} ${f.name}`} onClick={toggle}>{isOpen ? '▾' : '▸'}</button>
                : <span className="twist" style={{ display: 'inline-block', width: 12 }} />}
              <span className={highlightField === f.name && level === 1 ? 'chg' : ''}>{f.name} <span className="t">: {f.typeName}{f.nullable ? '?' : ''}</span></span>
              {repo && <> <button type="button" className="pill xrepo" onClick={() => onOpen(f.typeIds[0]!)}>↗ {repo}</button></>}
            </div>
            {isOpen && types?.map(t => <Fields key={t.id} ix={ix} tree={t} level={level + 1} onOpen={onOpen} />)}
          </div>
        );
      })}
    </>
  );
}
```

`web/src/panels/Inspector.tsx` (replaces the Task 7 version):

```tsx
import { useState, type ReactNode } from 'react';
import { placeFor } from '../app/state';
import { endpointDetail, modelTree, modelUsedBy, packageDetail, projectDetail, repoDetail, type CallerRow } from '../graph/details';
import type { NodeRef } from '../graph/graphIndex';
import { ModelTreeView } from './ModelTreeView';
import { Overview } from './Overview';
import type { PanelProps } from './types';

const simple = (fullName: string) => fullName.slice(fullName.lastIndexOf('.') + 1);

export function Inspector(props: PanelProps & { impactPanel?: ReactNode }) {
  const n = props.state.selection ? props.ix.tryGet(props.state.selection) : undefined;
  return (
    <aside className="rail" aria-label="Inspector">
      {props.impactPanel ?? (!n ? <Overview {...props} /> : <Selected key={n.id} node={n} {...props} />)}
    </aside>
  );
}

function Tabs({ tabs, children }: { tabs: string[]; children: (tab: string) => ReactNode }) {
  const [tab, setTab] = useState(tabs[0]!);
  return (
    <>
      <div className="tabs" role="tablist">
        {tabs.map(t => <button key={t} type="button" role="tab" aria-selected={t === tab} onClick={() => setTab(t)}>{t}</button>)}
      </div>
      <div role="tabpanel">{children(tab)}</div>
    </>
  );
}

function Selected({ node, ix, dispatch }: PanelProps & { node: NodeRef }) {
  const go = (id: string) => ix.tryGet(id) && dispatch({ type: 'focusNode', ...placeFor(ix, id) });
  const header = (title: ReactNode, sub: ReactNode) => (
    <>
      <div><h2>{title}</h2><div className="subline">{sub}</div></div>
      <button type="button" className="action" onClick={() => dispatch({ type: 'enterImpact', target: node.id })}>What breaks if this changes?</button>
    </>
  );
  const callers = (rows: CallerRow[]) => rows.length === 0 ? <div className="empty">No call sites found in the scanned repos.</div> : (
    <ul className="list">
      {rows.map(c => (
        <li key={c.callSiteId}>
          <button type="button" className="row" onClick={() => go(c.callSiteId)}>
            <span className="verb">{c.repo}</span><span className="route">{c.member}</span>
            <span className="detail">line {c.line} → <code>{c.clientMethodLabel}</code>{c.endpointLabel ? <> → {c.endpointLabel}</> : null} <span className={`pill ${c.confidence}`}>{c.confidence}</span></span>
          </button>
        </li>
      ))}
    </ul>
  );

  switch (node.kind) {
    case 'repo': {
      const d = repoDetail(ix, node.id);
      return (
        <div className="pad">
          {header(node.label, <><code>{d.repo.path}/</code> · {d.projects.length} projects{d.repo.headSha ? ` · HEAD ${d.repo.headSha.slice(0, 7)}` : ''}</>)}
          <Tabs tabs={['Overview', 'Publishes', `Called by (${d.calledBy.length})`, `Endpoints (${d.endpoints.length})`]}>{tab => {
            if (tab === 'Overview') return (
              <div className="pad" style={{ padding: '12px 0' }}>
                <div className="kpis">
                  <div className="kpi"><small>Endpoints</small><b>{d.endpoints.length}</b></div>
                  <div className="kpi"><small>Dependents</small><b>{d.dependents.length}</b></div>
                  <div className="kpi"><small>Depends on</small><b>{d.dependsOn.length}</b></div>
                </div>
                <ul className="list">{d.projects.map(p => <li key={p.id}><button type="button" className="row" onClick={() => go(p.id)}><span className="verb">{p.kind}</span><span className="route">{p.name}</span></button></li>)}</ul>
                <ul className="list" aria-label="Depends on">{d.dependsOn.map(l => <li key={l.repoId}><button type="button" className="row" onClick={() => go(l.repoId)}><span className="verb">→</span><span className="route">{l.repo}</span><span className="detail">{l.via.join(', ')} · {l.calls} calls</span></button></li>)}</ul>
              </div>
            );
            if (tab === 'Publishes') return d.publishes.length === 0 ? <div className="empty">Publishes no packages.</div> : (
              <ul className="list">{d.publishes.map(p => (
                <li key={p.packageNodeId}><button type="button" className="row" onClick={() => go(p.packageNodeId)}>
                  <span className="verb">NUGET</span><span className="route">{p.packageId} {p.version}</span>
                  <span className="detail">consumed by {p.consumers.map(c => `${c.repo} (${c.version ?? '?'}${c.version !== p.version ? ' ⚠' : ''})`).join(', ') || 'nobody'}</span>
                </button></li>
              ))}</ul>
            );
            if (tab.startsWith('Called by')) return callers(d.calledBy);
            return <ul className="list">{d.endpoints.map(e => <li key={e.id}><button type="button" className="row" onClick={() => go(e.id)}><span className={`verb ${e.verb.toUpperCase()}`}>{e.verb}</span><span className="route">{e.route}</span><span className="detail"><code>{e.handler}</code></span></button></li>)}</ul>;
          }}</Tabs>
        </div>
      );
    }
    case 'project': {
      const d = projectDetail(ix, node.id);
      return (
        <div className="pad">
          {header(node.label, <>{d.project.kind} · <code>{d.project.path}</code></>)}
          <Tabs tabs={['Overview', `References (${d.references.length})`, `Endpoints (${d.endpoints.length})`]}>{tab => {
            if (tab === 'Overview') return (
              <ul className="list">
                <li className="row"><span className="verb">SDK</span><span className="route">{d.project.sdk ?? '—'}</span></li>
                <li className="row"><span className="verb">PKG</span><span className="route">{d.publishes ? `${d.publishes.packageId} ${d.publishes.version ?? ''}` : 'not published'}</span></li>
                <li className="row"><span className="verb">API</span><span className="route">{d.clientMethods.length} client methods · {d.endpoints.length} endpoints</span></li>
              </ul>
            );
            if (tab.startsWith('References')) return <ul className="list">{d.references.map(r => <li key={r.packageNodeId}><button type="button" className="row" onClick={() => go(r.packageNodeId)}><span className="verb">NUGET</span><span className="route">{r.packageId} {r.version}</span><span className="detail">{r.producerRepo ? `from ${r.producerRepo}` : 'third-party'}</span></button></li>)}</ul>;
            return <ul className="list">{d.endpoints.map(e => <li key={e.id}><button type="button" className="row" onClick={() => go(e.id)}><span className={`verb ${e.verb.toUpperCase()}`}>{e.verb}</span><span className="route">{e.route}</span></button></li>)}</ul>;
          }}</Tabs>
        </div>
      );
    }
    case 'endpoint': {
      const d = endpointDetail(ix, node.id);
      const ep = d.endpoint;
      return (
        <div className="pad">
          {header(<><span className={`verb ${ep.verb.toUpperCase()}`}>{ep.verb}</span> {ep.route}</>, <><code>{ep.handler}</code> · {ep.location.path}:{ep.location.line}</>)}
          <Tabs tabs={['Contract', `Callers (${d.callers.length})`, 'Location']}>{tab => {
            if (tab === 'Contract') return (
              <div className="pad" style={{ padding: '12px 0' }}>
                <section><h3 className="sec">Parameters</h3>{ep.parameters.length === 0 ? <div className="empty">None</div> : (
                  <ul className="list">{ep.parameters.map(p => <li key={p.name} className="row"><span className="verb">{p.source}</span><span className="route">{p.name} : {p.typeName}{p.required ? '' : '?'}</span></li>)}</ul>
                )}</section>
                {d.request.map(m => <section key={m.id}><h3 className="sec">Request body</h3><ModelTreeView ix={ix} tree={modelTree(ix, m.id)} onOpen={go} /></section>)}
                {d.responses.map(r => (
                  <section key={r.statusCode}><h3 className="sec">Returns <span>{r.statusCode}</span></h3>
                    {r.models.length === 0 ? <div className="empty"><code>{r.typeName}</code></div> : r.models.map(m => <ModelTreeView key={m.id} ix={ix} tree={modelTree(ix, m.id)} onOpen={go} />)}
                  </section>
                ))}
                <section><h3 className="sec">How to call</h3>{d.clientMethods.length === 0 ? <div className="empty">No client package method targets this endpoint.</div> : (
                  <ul className="list">{d.clientMethods.map(c => <li key={c.id}><button type="button" className="row" onClick={() => go(c.id)}><span className="verb">CLIENT</span><span className="route">{c.label}</span><span className="detail">{c.packageId} {c.version} <span className={`pill ${c.confidence}`}>{c.confidence}</span></span></button></li>)}</ul>
                )}</section>
              </div>
            );
            if (tab.startsWith('Callers')) return callers(d.callers);
            return <ul className="list"><li className="row"><span className="verb">FILE</span><span className="route">{ep.location.path}:{ep.location.line}</span><span className="detail">{ep.handler}</span></li></ul>;
          }}</Tabs>
        </div>
      );
    }
    case 'model': {
      const m = ix.models.get(node.id)!;
      const used = modelUsedBy(ix, node.id);
      return (
        <div className="pad">
          {header(simple(m.fullName), <>{m.kind} · {m.fullName} · {m.repo}</>)}
          <Tabs tabs={['Fields', `Used by (${used.endpoints.length})`]}>{tab => tab === 'Fields'
            ? <ModelTreeView ix={ix} tree={modelTree(ix, node.id)} onOpen={go} />
            : (
              <ul className="list">
                {used.endpoints.map(e => <li key={e.id}><button type="button" className="row" onClick={() => go(e.id)}><span className="verb">{e.relation}</span><span className="route">{e.label}</span><span className="detail">{e.repo}</span></button></li>)}
                {used.containedIn.map(id => <li key={id}><button type="button" className="row" onClick={() => go(id)}><span className="verb">INSIDE</span><span className="route">{simple(ix.models.get(id)?.fullName ?? id)}</span></button></li>)}
              </ul>
            )}</Tabs>
        </div>
      );
    }
    case 'package': {
      const d = packageDetail(ix, node.id);
      return (
        <div className="pad">
          {header(d.pkg.packageId, <>{d.producer ? `published by ${d.producer.repo}/${d.producer.name}` : 'third-party'}{d.latest ? ` · latest ${d.latest}` : ''}</>)}
          <Tabs tabs={[`Consumers (${d.consumers.length})`, 'Versions']}>{tab => tab.startsWith('Consumers')
            ? <ul className="list">{d.consumers.map(c => <li key={c.projectId}><button type="button" className="row" onClick={() => go(c.projectId)}><span className="verb">{c.repo}</span><span className="route">{c.project}</span><span className="detail">{c.version ?? '?'}</span></button></li>)}</ul>
            : <ul className="list">{d.versions.map(v => <li key={v.version} className={`row ${v.behind ? 'warning' : ''}`}><span className="verb">{v.count}×</span><span className="route">{v.version}{v.behind ? ' — behind latest' : ''}</span></li>)}</ul>}
          </Tabs>
        </div>
      );
    }
    default: {
      const cm = ix.clientMethods.get(node.id), cs = ix.callSites.get(node.id);
      const loc = cm?.location ?? cs?.location;
      return (
        <div className="pad">
          {header(node.label, <>{node.kind === 'clientMethod' ? 'client method' : 'call site'} · {node.repo}</>)}
          {cm && <div className="subline"><code>{cm.signature}</code></div>}
          {loc && <ul className="list"><li className="row"><span className="verb">FILE</span><span className="route">{loc.path}:{loc.line}</span></li></ul>}
        </div>
      );
    }
  }
}
```

- [ ] **Step 4: Run tests, typecheck and build**

Run: `cd web && npx vitest run && npm run build`
Expected: PASS.

- [ ] **Step 5: After screenshot**

Take after screenshots of the endpoint contract tab, the repo "Called by" tab and a model's fields, in light and dark mode. Show the user the before and after screenshots.

- [ ] **Step 6: Commit**

```bash
git add web
git commit -m "feat(web): inspector tabs per kind with contract model tree, callers and cross-repo jumps

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 10: Command palette (Ctrl+K) and impact mode

**Files:**
- Create: `web/src/graph/impactMarks.ts`, `web/src/panels/CommandPalette.tsx`, `web/src/panels/ImpactPanel.tsx`
- Modify: `web/src/map/MapStage.tsx` (takes the impact result and computes marks), `web/src/app/App.tsx` (impact computation, keyboard, palette, top-bar chip)
- Test: `web/test/impactMarks.test.ts`, `web/test/Impact.test.tsx`

**Interfaces:**
- Consumes: `computeImpact`, `ImpactResult` (Task 4); `View` (Task 5); `ImpactMarks` (Task 8); `Inspector`'s `impactPanel` prop (Task 9); `TopBar`'s `impactSummary` prop (Task 7).
- Produces:
  - `impactMarks(ix, view, result): ImpactMarks`. Source is the target, its project or its repo, whichever this view draws. Hit is every affected drawn node, with its affected call-site count. Hot edges are those from a hit node to a hit or source node.
  - `CommandPalette(props: PanelProps)`. The `impact <target>` prefix enters impact mode.
  - `ImpactPanel(props: PanelProps & { result: ImpactResult })`, with a heading "Changing X affects N other repo(s)". Lists are capped at 200 with "+N more".
  - `MapStage` prop `impact` becomes `ImpactResult | null` (it was `ImpactMarks | null`).
- Keys: Ctrl+K / Cmd+K opens the palette. Esc closes the palette, else leaves impact mode, else clears the selection. Backspace, outside inputs, goes back.

- [ ] **Step 0: Before screenshot** (any state), as in Task 8.

- [ ] **Step 1: Write the failing tests**

`web/test/impactMarks.test.ts`:

```ts
import { expect, test } from 'vitest';
import { GraphIndex } from '../src/graph/graphIndex';
import { impactMarks } from '../src/graph/impactMarks';
import { computeImpact } from '../src/graph/queries';
import { buildView } from '../src/graph/views';
import { fixtureGraph } from './util/fixture';

const ix = new GraphIndex(fixtureGraph());

test('repo depth: the target repo is the source; consumers are hit with their call-site counts', () => {
  const m = impactMarks(ix, buildView(ix, 'repo', null), computeImpact(ix, 'OrderDto.Lines'));
  expect([...m.source]).toEqual(['repo:orders']);
  expect(Object.fromEntries(m.hit)).toEqual({ 'repo:billing': 1, 'repo:gateway': 2 });
  expect([...m.edges].sort()).toEqual(['repo:billing>repo:orders', 'repo:gateway>repo:orders']);
});

test('endpoint depth: the endpoint itself is the source', () => {
  const view = buildView(ix, 'endpoint', 'ep:billing:POST:/api/invoices');
  const m = impactMarks(ix, view, computeImpact(ix, 'POST /api/invoices'));
  expect([...m.source]).toEqual(['ep:billing:POST:/api/invoices']);
  expect(m.hit.get('cs:orders/Orders.Api:InvoiceNotifier.NotifyAsync:7')).toBe(1);
  expect(m.edges.size).toBe(2);
});
```

`web/test/Impact.test.tsx`:

```tsx
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { expect, test } from 'vitest';
import { App } from '../src/app/App';
import { fixtureGraph } from './util/fixture';
import { inThreadEngine } from './util/inThreadEngine';

const renderApp = () => render(<App graph={fixtureGraph()} engine={inThreadEngine()} mode="export" />);
const inspector = () => screen.getByRole('complementary', { name: 'Inspector' });

test('Ctrl+K finds a node and Enter focuses it', async () => {
  renderApp();
  await userEvent.keyboard('{Control>}k{/Control}');
  await userEvent.type(screen.getByRole('combobox', { name: 'Search' }), 'GetOrderAsync{Enter}');
  expect(screen.queryByRole('dialog')).toBeNull();
  expect(within(inspector()).getByRole('heading', { level: 2 })).toHaveTextContent('OrdersClient.GetOrderAsync');
});

test('"impact <target>" enters impact mode; Esc leaves it', async () => {
  renderApp();
  await userEvent.keyboard('{Control>}k{/Control}');
  await userEvent.type(screen.getByRole('combobox', { name: 'Search' }), 'impact OrderDto.Lines{Enter}');
  expect(within(inspector()).getByRole('heading', { level: 2, name: 'Changing OrderDto.Lines affects 2 other repos' })).toBeInTheDocument();
  expect(screen.getByText('3 call sites')).toBeInTheDocument();
  expect(window.location.hash).toBe('#/impact/OrderDto.Lines');
  await userEvent.keyboard('{Escape}');
  expect(within(inspector()).getByRole('heading', { level: 2, name: '6 links across 5 repos' })).toBeInTheDocument();
});

test('an unknown impact target shows not found with suggestions', async () => {
  window.location.hash = '#/impact/OrderDto.Nope';
  renderApp();
  expect(await screen.findByText(/Not found: OrderDto.Nope/)).toBeInTheDocument();
  expect(within(inspector()).getByRole('heading', { level: 2, name: '6 links across 5 repos' })).toBeInTheDocument();
});

test('Backspace goes back up after a drill', async () => {
  window.location.hash = '#/ep:orders:GET:/api/orders/%7Bid%7D';
  renderApp();
  expect(screen.getByRole('button', { name: 'Endpoint' })).toHaveAttribute('aria-pressed', 'true');
  await userEvent.keyboard('{Backspace}');
  expect(screen.getByRole('button', { name: 'Repo' })).toHaveAttribute('aria-pressed', 'true');
});
```

- [ ] **Step 2: Run to verify failure**

Run: `cd web && npx vitest run test/impactMarks.test.ts test/Impact.test.tsx`
Expected: FAIL, because `impactMarks` doesn't exist and there's no palette or impact panel yet.

- [ ] **Step 3: Implement**

`web/src/graph/impactMarks.ts`:

```ts
import type { ImpactMarks } from '../map/neighbours';
import type { GraphIndex } from './graphIndex';
import type { ImpactResult } from './queries';
import type { View } from './views';

function owningProject(ix: GraphIndex, id: string): string | undefined {
  return ix.endpoints.get(id)?.projectId ?? ix.clientMethods.get(id)?.projectId ?? ix.callSites.get(id)?.projectId ?? ix.models.get(id)?.projectId;
}

export function impactMarks(ix: GraphIndex, view: View, r: ImpactResult): ImpactMarks {
  const affected = new Set([r.repos, r.projects, r.endpoints, r.clientMethods, r.callSites, r.models].flat().map(a => a.id));
  const drawn = new Set(view.nodes.map(n => n.id));
  const target = ix.get(r.target);
  const candidates = [r.target, owningProject(ix, r.target), target.repo ? `repo:${target.repo}` : undefined];
  const first = candidates.find((id): id is string => id !== undefined && drawn.has(id));
  const source = new Set(first ? [first] : []);

  const hit = new Map<string, number>();
  for (const n of view.nodes) {
    if (source.has(n.id) || !affected.has(n.id)) continue;
    const count = r.callSites.filter(cs =>
      n.kind === 'repo' ? cs.repo === n.label
        : n.kind === 'project' ? ix.callSites.get(cs.id)?.projectId === n.id
          : cs.id === n.id).length;
    hit.set(n.id, count);
  }
  const marked = (id: string) => source.has(id) || hit.has(id);
  const edges = new Set(view.edges.filter(e => hit.has(e.from) && marked(e.to)).map(e => e.id));
  return { source, hit, edges };
}
```

`web/src/panels/CommandPalette.tsx`:

```tsx
import { useMemo, useState } from 'react';
import { placeFor } from '../app/state';
import type { NodeKind } from '../graph/graphIndex';
import type { PanelProps } from './types';

const KIND_LABEL: Record<NodeKind, string> = {
  repo: 'repo', project: 'project', package: 'package', endpoint: 'endpoint', clientMethod: 'client method', callSite: 'call site', model: 'model',
};
type Row = { key: string; kind: string; id: string; label: string; impact: boolean };

export function CommandPalette({ ix, dispatch }: PanelProps) {
  const [q, setQ] = useState('');
  const [active, setActive] = useState(0);
  const impactPrefix = /^impact\s+/i.test(q);
  const term = (impactPrefix ? q.replace(/^impact\s+/i, '') : q).trim();

  const rows = useMemo<Row[]>(() => {
    if (term === '') return [];
    const ranked = ix.suggest(term, 20).map(id => ix.get(id));
    // grouped by kind, groups in the order of their best match, so Enter still takes the best match
    const order: NodeKind[] = [];
    for (const n of ranked) if (!order.includes(n.kind)) order.push(n.kind);
    const grouped = order.flatMap(k => ranked.filter(n => n.kind === k))
      .map(n => ({ key: n.id, kind: KIND_LABEL[n.kind], id: n.id, label: n.label, impact: impactPrefix }));
    return impactPrefix ? [{ key: '§impact', kind: 'impact', id: term, label: `What breaks if ${term} changes?`, impact: true }, ...grouped] : grouped;
  }, [ix, term, impactPrefix]);

  const close = () => dispatch({ type: 'setPalette', open: false });
  const pick = (row: Row | undefined) => {
    if (!row) return;
    if (row.impact) { dispatch({ type: 'enterImpact', target: row.id }); return; }
    dispatch({ type: 'focusNode', ...placeFor(ix, row.id) });
    close();
  };

  return (
    <div className="palette-backdrop" onMouseDown={e => { if (e.target === e.currentTarget) close(); }}>
      <div className="palette" role="dialog" aria-label="Find anything">
        <input autoFocus role="combobox" aria-label="Search" aria-expanded={rows.length > 0} aria-controls="palette-results"
          placeholder="Repo, endpoint, model, package…   (prefix with “impact ” for what-breaks)"
          value={q}
          onChange={e => { setQ(e.target.value); setActive(0); }}
          onKeyDown={e => {
            if (e.key === 'ArrowDown') { e.preventDefault(); setActive(a => Math.min(a + 1, rows.length - 1)); }
            else if (e.key === 'ArrowUp') { e.preventDefault(); setActive(a => Math.max(a - 1, 0)); }
            else if (e.key === 'Enter') { e.preventDefault(); pick(rows[active]); }
            else if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); close(); }
          }} />
        <ul id="palette-results" role="listbox">
          {rows.map((r, i) => (
            <li key={r.key} role="option" aria-selected={i === active} onMouseEnter={() => setActive(i)} onMouseDown={e => { e.preventDefault(); pick(r); }}>
              <small>{r.kind}</small><span>{r.label}</span>
            </li>
          ))}
        </ul>
      </div>
    </div>
  );
}
```

`web/src/panels/ImpactPanel.tsx`:

```tsx
import { placeFor } from '../app/state';
import type { ImpactResult } from '../graph/queries';
import type { PanelProps } from './types';

const CAP = 200;
const simple = (s: string) => s.slice(s.lastIndexOf('.') + 1);
const plural = (n: number, w: string) => `${n} ${w}${n === 1 ? '' : 's'}`;

export function impactLabel(ix: PanelProps['ix'], r: ImpactResult): string {
  const t = ix.get(r.target);
  return r.field ? `${simple(t.label)}.${r.field}` : t.kind === 'model' ? simple(t.label) : t.label;
}

export function ImpactPanel({ ix, dispatch, result: r }: PanelProps & { result: ImpactResult }) {
  const t = ix.get(r.target);
  const others = r.repos.filter(x => x.label !== t.repo);
  const affectedRepos = new Set(r.repos.map(x => x.label));
  const untouched = [...ix.repos.values()].map(x => x.name).filter(n => n !== t.repo && !affectedRepos.has(n));
  const cms = new Set(r.clientMethods.map(c => c.id));
  const go = (id: string) => dispatch({ type: 'focusNode', ...placeFor(ix, id) });
  const chain = (csId: string) => {
    const cm = ix.dependenciesOf(csId).find(h => h.kind === 'invokes' && cms.has(h.to))?.to;
    const ep = cm ? ix.dependenciesOf(cm).find(h => h.kind === 'targets')?.to : undefined;
    return [cm && ix.tryGet(cm)?.label, ep && ix.tryGet(ep)?.label].filter(Boolean).join(' → ');
  };
  return (
    <div className="pad">
      <div>
        <h2>Changing {impactLabel(ix, r)} affects {plural(others.length, 'other repo')}</h2>
        <p className="summary">Everything that depends on it, directly or through client packages. Confidence is the weakest link on the best path.</p>
      </div>
      <div className="kpis">
        <div className="kpi bad"><small>Repos</small><b>{r.repos.length}</b></div>
        <div className="kpi bad"><small>Endpoints</small><b>{r.endpoints.length}</b></div>
        <div className="kpi bad"><small>Call sites</small><b>{r.callSites.length}</b></div>
      </div>
      {r.field && (
        <section><h3 className="sec">Field</h3>
          <div className="tree"><div>{simple(t.label)} <span className="t">{t.repo}</span></div><div style={{ paddingLeft: 18 }}><span className="chg">{r.field}</span></div></div>
        </section>
      )}
      <section>
        <h3 className="sec">Affected call sites <span>{r.callSites.length}</span></h3>
        {r.callSites.length === 0 ? <div className="empty">No call sites reach it.</div> : (
          <ul className="list">
            {r.callSites.slice(0, CAP).map(cs => (
              <li key={cs.id}><button type="button" className="row error" onClick={() => go(cs.id)}>
                <span className="verb">{cs.repo}</span><span className="route">{cs.label}</span>
                <span className="detail">{chain(cs.id)} <span className={`pill ${cs.confidence}`}>{cs.confidence}</span></span>
              </button></li>
            ))}
          </ul>
        )}
        {r.callSites.length > CAP && <p className="more">+{r.callSites.length - CAP} more</p>}
      </section>
      <section>
        <h3 className="sec">Affected endpoints <span>{r.endpoints.length}</span></h3>
        <ul className="list">{r.endpoints.slice(0, CAP).map(e => <li key={e.id}><button type="button" className="row" onClick={() => go(e.id)}><span className="verb">{e.repo}</span><span className="route">{e.label}</span><span className="detail"><span className={`pill ${e.confidence}`}>{e.confidence}</span></span></button></li>)}</ul>
        {r.endpoints.length > CAP && <p className="more">+{r.endpoints.length - CAP} more</p>}
      </section>
      <section><h3 className="sec">Not affected</h3><div className="subline">{untouched.length ? untouched.join(', ') : 'Every other repo is affected.'}</div></section>
      <button type="button" className="action" onClick={() => dispatch({ type: 'exitImpact' })}>Exit impact view (Esc)</button>
    </div>
  );
}
```

`web/src/map/MapStage.tsx`. Make these edits:
- import `impactMarks`:

```tsx
import { impactMarks } from '../graph/impactMarks';
import type { ImpactResult } from '../graph/queries';
```

- remove `import type { ImpactMarks } from './neighbours';`
- change the prop type `impact: ImpactMarks | null` to `impact: ImpactResult | null`
- after the `view` memo, add:

```tsx
  const marks = useMemo(() => (impact ? impactMarks(ix, view, impact) : null), [ix, view, impact]);
```

- pass `impact={marks}` (instead of `impact={impact}`) to `MapCanvas`.

`web/src/app/App.tsx`. Make these edits:
- add these imports:

```tsx
import { useEffect, useMemo, useReducer, type Dispatch } from 'react'; // (already present; unchanged)
import { computeImpact, type ImpactResult } from '../graph/queries';
import { CommandPalette } from '../panels/CommandPalette';
import { ImpactPanel, impactLabel } from '../panels/ImpactPanel';
```

- after the `colors` memo, add the impact computation and its error handling:

```tsx
  const impact = useMemo((): ImpactResult | QueryError | null => {
    if (!state.impact) return null;
    try { return computeImpact(ix, state.impact); } catch (e) { if (e instanceof QueryError) return e; throw e; }
  }, [ix, state.impact]);
  const impactResult = impact instanceof QueryError ? null : impact;

  useEffect(() => {
    if (impact instanceof QueryError && state.impact) {
      dispatch({ type: 'notFound', query: state.impact, suggestions: impact.suggestions });
      dispatch({ type: 'exitImpact' });
    }
  }, [impact, state.impact]);

  // keyboard: Ctrl/Cmd+K palette, Esc closes/leaves/clears, Backspace goes back (never inside inputs)
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      const typing = e.target instanceof HTMLElement && (e.target.matches('input, textarea, [contenteditable]'));
      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'k') { e.preventDefault(); dispatch({ type: 'setPalette', open: true }); return; }
      if (typing) return;
      if (e.key === 'Escape') {
        if (stateRef.current.paletteOpen) dispatch({ type: 'setPalette', open: false });
        else if (stateRef.current.impact) dispatch({ type: 'exitImpact' });
        else dispatch({ type: 'select', id: null });
      } else if (e.key === 'Backspace') { e.preventDefault(); dispatch({ type: 'back' }); }
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, []);
```

  and right after the `useReducer` line add a ref that mirrors state for the key handler:

```tsx
  const stateRef = useRef(state);
  stateRef.current = state;
```

  (add `useRef` to the `react` import).
- replace everything from the line `const panel: PanelProps = { ix, state, dispatch, colors };` to the end of the component's `return (...)` with:

```tsx
  const panel: PanelProps = { ix, state, dispatch, colors };
  void mode; void bundleHtml; // used from Task 11
  const impactSummary = impactResult ? (
    <>
      <div className="target"><span>Impact of</span> {impactLabel(ix, impactResult)}
        <button type="button" aria-label="Exit impact view" onClick={() => dispatch({ type: 'exitImpact' })}>✕</button></div>
      <span className="stat bad"><b>{impactResult.repos.length}</b>repos affected</span>
      <span className="stat bad">{impactResult.callSites.length} call sites</span>
    </>
  ) : undefined;
  return (
    <div className={`app${state.sidebarCollapsed ? ' collapsed' : ''}`}>
      <TopBar {...panel} impactSummary={impactSummary} />
      {status === 'reconnecting' ? <div role="status" className="banner">Lost connection — retrying</div> : <div />}
      <div className="main">
        <Sidebar {...panel} />
        <main className="stage" data-testid="stage">
          <MapStage {...panel} engine={engine} workspaceKey={graph.workspace ?? 'default'} impact={impactResult} />
          <NotFound {...panel} />
        </main>
        <Inspector {...panel} impactPanel={impactResult ? <ImpactPanel {...panel} result={impactResult} /> : undefined} />
      </div>
      {state.paletteOpen && <CommandPalette {...panel} />}
    </div>
  );
```

- [ ] **Step 4: Run tests, typecheck and build**

Run: `cd web && npx vitest run && npm run build`
Expected: PASS.

- [ ] **Step 5: After screenshot**

Screenshot `#/impact/OrderDto.Lines` at Repo depth in light and dark mode, plus the open palette. Show the user the before and after screenshots, and compare them with mockup D.

- [ ] **Step 6: Commit**

```bash
git add web
git commit -m "feat(web): Ctrl+K palette and impact mode (map marks, pinned target, affected call sites)

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 11: Serve-mode live updates, Export menu, and reconciling state after a rescan

**Files:**
- Create: `web/src/app/exportHtml.ts`, `web/src/app/live.ts`, `web/src/panels/ExportMenu.tsx`
- Modify: `web/src/main.tsx` (serve mode), `web/src/app/App.tsx` (export menu, reconcile, added nodes; drop the unused `bundleHtml` prop)
- Test: `web/test/exportHtml.test.ts`, `web/test/live.test.ts`, `web/test/Reconcile.test.tsx`

**Interfaces:**
- Produces (`exportHtml.ts`):
  - `GRAPH_PLACEHOLDER`, `FOCUS_PLACEHOLDER`, the exact strings from Task 3
  - `escapeJsonForHtml(json)`
  - `buildExportHtml(bundleHtml, graphJson, focus): string`, which throws `Error('bundle has no data placeholders')` if either placeholder is missing
- The shared escape test vector:
  - input: `{"a":"</script><b>&` + U+2028 + `"}`
  - output: `{"a":"\u003c/script\u003e\u003cb\u003e\u0026\u2028"}`
  - Task 12's C# test asserts the same output.
- Produces (`live.ts`):
  - `type LiveStatus = 'loading' | 'ok' | 'reconnecting' | 'schema'`
  - `startLive(onGraph, onStatus, deps?) → stop()`
  - Behaviour:
    - fetches `/api/graph` with `If-None-Match`, treating 304 as no change
    - subscribes to `/api/events` (`graph-changed`, `open`)
    - retries failures after 1 s, doubling up to 30 s
  - `deps` is injectable: `{ fetch, eventSource, setTimeout, clearTimeout }`
- Produces (`ExportMenu.tsx`): `ExportMenu({ graph, mode, focus })`, offering PNG and SVG of the canvas, plus HTML in serve mode only (it fetches `/` and inlines the graph).
- `AppProps` loses `bundleHtml`.

- [ ] **Step 0: Before screenshot** of the top bar, as in Task 8.

- [ ] **Step 1: Write the failing tests**

`web/test/exportHtml.test.ts`:

```ts
import { expect, test } from 'vitest';
import { buildExportHtml, escapeJsonForHtml, FOCUS_PLACEHOLDER, GRAPH_PLACEHOLDER } from '../src/app/exportHtml';
import { parseGraph, readFocus, readInlineGraph } from '../src/graph/load';
import { fixtureJson } from './util/fixture';

test('shared escape vector (also asserted by C# DiagramExporterTests)', () => {
  expect(escapeJsonForHtml('{"a":"</script><b>&\u2028"}')).toBe('{"a":"\\u003c/script\\u003e\\u003cb\\u003e\\u0026\\u2028"}');
});

test('the graph round-trips out of the page and a hostile name stays data', () => {
  const graph = JSON.parse(fixtureJson().replace(/^\uFEFF/, ''));
  graph.models[0].fullName = '</script><script>alert(1)</script>';
  const html = buildExportHtml(`<html><head>${FOCUS_PLACEHOLDER}${GRAPH_PLACEHOLDER}</head><body></body></html>`, JSON.stringify(graph), 'a"b');
  expect(html).not.toContain('<script>alert(1)');
  const doc = new DOMParser().parseFromString(html, 'text/html');
  expect(readFocus(doc)).toBe('a"b');
  const r = parseGraph(readInlineGraph(doc)!);
  if (!r.ok) throw new Error('did not parse');
  expect(r.graph.models[0]!.fullName).toBe('</script><script>alert(1)</script>');
});

test('a bundle without placeholders is rejected', () => {
  expect(() => buildExportHtml('<html></html>', '{}', null)).toThrow('bundle has no data placeholders');
});
```

`web/test/live.test.ts`:

```ts
import { afterEach, beforeEach, expect, test, vi } from 'vitest';
import { startLive, type LiveStatus } from '../src/app/live';
import { fixtureJson } from './util/fixture';

type Reply = { status: number; body?: string; etag?: string } | 'fail';
function harness(replies: Reply[]) {
  const calls: (string | null)[] = [];
  const listeners = new Map<string, () => void>();
  const es = { addEventListener: (t: string, f: () => void) => listeners.set(t, f), onerror: null as ((e: unknown) => void) | null, close: vi.fn() };
  const fetch = vi.fn(async (_url: string, init?: RequestInit) => {
    calls.push((init?.headers as Record<string, string> | undefined)?.['If-None-Match'] ?? null);
    const r = replies.shift() ?? { status: 304 };
    if (r === 'fail') throw new Error('offline');
    return new Response(r.status === 304 ? null : r.body ?? '', { status: r.status, headers: r.etag ? { ETag: r.etag } : {} });
  });
  const graphs: unknown[] = [], statuses: LiveStatus[] = [];
  const stop = startLive(g => graphs.push(g), s => statuses.push(s), { fetch, eventSource: () => es, setTimeout, clearTimeout });
  return { calls, graphs, statuses, emit: (t: string) => listeners.get(t)!(), es, stop };
}
const settle = () => vi.advanceTimersByTimeAsync(0);

beforeEach(() => vi.useFakeTimers());
afterEach(() => vi.useRealTimers());

test('loads, then refetches with If-None-Match on graph-changed; 304 changes nothing', async () => {
  const h = harness([{ status: 200, body: fixtureJson(), etag: '"v1"' }, { status: 304 }]);
  await settle();
  expect(h.graphs).toHaveLength(1);
  h.emit('graph-changed');
  await settle();
  expect(h.calls).toEqual([null, '"v1"']);
  expect(h.graphs).toHaveLength(1);
  expect(h.statuses.at(-1)).toBe('ok');
});

test('failures report reconnecting and retry with backoff', async () => {
  const h = harness(['fail', 'fail', { status: 200, body: fixtureJson(), etag: '"v1"' }]);
  await settle();
  expect(h.statuses).toEqual(['reconnecting']);
  await vi.advanceTimersByTimeAsync(1000);
  expect(h.calls).toHaveLength(2);
  await vi.advanceTimersByTimeAsync(1999);
  expect(h.calls).toHaveLength(2);
  await vi.advanceTimersByTimeAsync(1);
  expect(h.graphs).toHaveLength(1);
  expect(h.statuses.at(-1)).toBe('ok');
});

test('a newer schema stops with "schema"', async () => {
  const h = harness([{ status: 200, body: '{"schemaVersion":2}' }]);
  await settle();
  expect(h.statuses).toEqual(['schema']);
  h.stop();
  expect(h.es.close).toHaveBeenCalled();
});
```

`web/test/Reconcile.test.tsx`:

```tsx
import { render, screen, within } from '@testing-library/react';
import { expect, test } from 'vitest';
import { App } from '../src/app/App';
import type { DepGraph } from '../src/graph/types';
import { fixtureGraph } from './util/fixture';
import { inThreadEngine } from './util/inThreadEngine';

const without = (g: DepGraph, id: string): DepGraph => ({
  ...g, endpoints: g.endpoints.filter(e => e.id !== id), edges: g.edges.filter(e => e.from !== id && e.to !== id),
});

test('a rescan that removes the selected, focused node falls back without crashing', () => {
  window.location.hash = '#/ep:orders:GET:/api/orders/%7Bid%7D';
  const engine = inThreadEngine();
  const g = fixtureGraph();
  const { rerender } = render(<App graph={g} engine={engine} mode="serve" />);
  expect(screen.getByRole('button', { name: 'Endpoint' })).toHaveAttribute('aria-pressed', 'true');
  rerender(<App graph={without(g, 'ep:orders:GET:/api/orders/{id}')} engine={engine} mode="serve" />);
  expect(screen.getByRole('button', { name: 'Repo' })).toHaveAttribute('aria-pressed', 'true');
  expect(within(screen.getByRole('complementary', { name: 'Inspector' })).getByRole('heading', { level: 2, name: '6 links across 5 repos' })).toBeInTheDocument();
});

test('a rescan that keeps the selection keeps it', () => {
  window.location.hash = '#/repo:orders';
  const engine = inThreadEngine();
  const g = fixtureGraph();
  const { rerender } = render(<App graph={g} engine={engine} mode="serve" />);
  rerender(<App graph={{ ...g, generatedAt: '2026-10-03T00:00:00Z' }} engine={engine} mode="serve" />);
  expect(within(screen.getByRole('complementary', { name: 'Inspector' })).getByRole('heading', { level: 2, name: 'orders' })).toBeInTheDocument();
});
```

- [ ] **Step 2: Run to verify failure**

Run: `cd web && npx vitest run test/exportHtml.test.ts test/live.test.ts test/Reconcile.test.tsx`
Expected: FAIL. The modules are missing, and `Reconcile` fails because the removed focus is never reconciled.

- [ ] **Step 3: Implement**

`web/src/app/exportHtml.ts`:

```ts
// Must match the placeholders in web/index.html and C# Depenk.Server.DiagramBundle.
export const GRAPH_PLACEHOLDER = '<script type="application/json" id="depenk-graph"></script>';
export const FOCUS_PLACEHOLDER = '<meta name="depenk-focus" content="">';

/** Makes JSON safe inside <script>: no '</script>', no HTML-significant characters, no JS line terminators. */
export const escapeJsonForHtml = (json: string): string =>
  json.replace(/</g, '\\u003c').replace(/>/g, '\\u003e').replace(/&/g, '\\u0026').replace(/\u2028/g, '\\u2028').replace(/\u2029/g, '\\u2029');

const escapeAttr = (s: string): string =>
  s.replace(/&/g, '&amp;').replace(/"/g, '&quot;').replace(/'/g, '&#39;').replace(/</g, '&lt;').replace(/>/g, '&gt;');

export function buildExportHtml(bundleHtml: string, graphJson: string, focus: string | null): string {
  if (!bundleHtml.includes(GRAPH_PLACEHOLDER) || !bundleHtml.includes(FOCUS_PLACEHOLDER)) throw new Error('bundle has no data placeholders');
  return bundleHtml
    .replace(GRAPH_PLACEHOLDER, () => `<script type="application/json" id="depenk-graph">${escapeJsonForHtml(graphJson)}</script>`)
    .replace(FOCUS_PLACEHOLDER, () => `<meta name="depenk-focus" content="${escapeAttr(focus ?? '')}">`);
}
```

`web/src/app/live.ts`:

```ts
import { parseGraph } from '../graph/load';
import type { DepGraph } from '../graph/types';

export type LiveStatus = 'loading' | 'ok' | 'reconnecting' | 'schema';
export interface EventSourceLike { addEventListener(type: string, fn: () => void): void; onerror: ((e: unknown) => void) | null; close(): void }
export interface LiveDeps {
  fetch: (url: string, init?: RequestInit) => Promise<Response>;
  eventSource: (url: string) => EventSourceLike;
  setTimeout: (fn: () => void, ms: number) => unknown;
  clearTimeout: (handle: never) => void;
}

const browserDeps = (): LiveDeps => ({
  fetch: (u, i) => fetch(u, i),
  eventSource: u => new EventSource(u) as unknown as EventSourceLike,
  setTimeout: (f, ms) => window.setTimeout(f, ms),
  clearTimeout: h => window.clearTimeout(h),
});

/** `depenk serve` mode: load /api/graph, reload on SSE graph-changed (and after reconnects), back off on failure. */
export function startLive(onGraph: (g: DepGraph) => void, onStatus: (s: LiveStatus) => void, deps: LiveDeps = browserDeps()): () => void {
  let etag: string | null = null;
  let delay = 1000;
  let timer: unknown = null;
  let stopped = false;
  let inFlight = false;
  let again = false;

  const retry = () => {
    if (stopped) return;
    if (timer !== null) deps.clearTimeout(timer as never);
    timer = deps.setTimeout(() => { timer = null; void load(); }, delay);
    delay = Math.min(delay * 2, 30_000);
  };

  const load = async (): Promise<void> => {
    if (stopped) return;
    if (inFlight) { again = true; return; }
    inFlight = true;
    try {
      const res = await deps.fetch('/api/graph', { headers: etag ? { 'If-None-Match': etag } : {} });
      if (res.status === 304) { delay = 1000; onStatus('ok'); return; }
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      const r = parseGraph(await res.text());
      if (!r.ok && r.reason === 'schema') { stopped = true; onStatus('schema'); return; }
      if (!r.ok) throw new Error(r.message);
      etag = res.headers.get('ETag');
      delay = 1000;
      onStatus('ok');
      onGraph(r.graph);
    } catch {
      onStatus('reconnecting');
      retry();
    } finally {
      inFlight = false;
      if (again && !stopped) { again = false; void load(); }
    }
  };

  const es = deps.eventSource('/api/events');
  es.addEventListener('graph-changed', () => void load());
  es.addEventListener('open', () => void load()); // after a reconnect the graph may have changed while we were away
  es.onerror = () => { if (!stopped) onStatus('reconnecting'); };
  void load();
  return () => {
    stopped = true;
    if (timer !== null) deps.clearTimeout(timer as never);
    es.close();
  };
}
```

`web/src/panels/ExportMenu.tsx`:

```tsx
import { toPng, toSvg } from 'html-to-image';
import { useState } from 'react';
import { buildExportHtml } from '../app/exportHtml';
import type { DepGraph } from '../graph/types';

function download(href: string, name: string) {
  const a = document.createElement('a');
  a.href = href;
  a.download = name;
  a.click();
}

export function ExportMenu({ graph, mode, focus }: { graph: DepGraph; mode: 'export' | 'serve'; focus: string | null }) {
  const [open, setOpen] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const run = (f: () => Promise<void>) => () => { setOpen(false); f().catch(e => setError(String(e))); };
  const image = (kind: 'png' | 'svg') => run(async () => {
    const el = document.querySelector<HTMLElement>('.stage .react-flow');
    if (!el) return;
    const backgroundColor = getComputedStyle(document.body).backgroundColor;
    download(kind === 'png' ? await toPng(el, { backgroundColor, pixelRatio: 2 }) : await toSvg(el, { backgroundColor }), `depenk.${kind}`);
  });
  const html = run(async () => {
    const bundle = await (await fetch('/')).text();
    const url = URL.createObjectURL(new Blob([buildExportHtml(bundle, JSON.stringify(graph), focus)], { type: 'text/html' }));
    download(url, 'depenk.html');
    setTimeout(() => URL.revokeObjectURL(url), 1000);
  });
  return (
    <div className="menu">
      <button type="button" className="icon-btn" aria-haspopup="menu" aria-expanded={open} onClick={() => setOpen(o => !o)}>Export ▾</button>
      {open && (
        <ul role="menu">
          <li><button type="button" role="menuitem" onClick={image('png')}>PNG image</button></li>
          <li><button type="button" role="menuitem" onClick={image('svg')}>SVG image</button></li>
          {mode === 'serve' && <li><button type="button" role="menuitem" onClick={html}>HTML file (offline)</button></li>}
        </ul>
      )}
      {error && <span role="alert" className="stat bad">{error}</span>}
    </div>
  );
}
```

`web/src/app/App.tsx` edits:
- remove `bundleHtml` from `AppProps`, from the destructuring, and from the `void mode; void bundleHtml;` line (delete that line)
- `import { ExportMenu } from '../panels/ExportMenu';` and add `useState` to the `react` import
- after the keyboard effect, add the rescan reconciliation:

```tsx
  // live rescans (serve mode): keep what still exists, drop the rest, and fade in what is new
  const prevIds = useRef<Set<string> | null>(null);
  const [added, setAdded] = useState<Set<string>>(() => new Set());
  useEffect(() => {
    const ids = new Set([...ix.nodes()].map(n => n.id));
    if (prevIds.current) {
      const before = prevIds.current;
      setAdded(new Set([...ids].filter(id => !before.has(id))));
      let impactValid = true;
      if (stateRef.current.impact) { try { computeImpact(ix, stateRef.current.impact); } catch { impactValid = false; } }
      dispatch({ type: 'reconcile', known: id => ids.has(id), impactValid });
    }
    prevIds.current = ids;
  }, [ix]);
```

- pass the export menu to the top bar and `added` to the stage:

```tsx
      <TopBar {...panel} impactSummary={impactSummary} exportMenu={<ExportMenu graph={graph} mode={mode} focus={state.selection} />} />
```

```tsx
          <MapStage {...panel} engine={engine} workspaceKey={graph.workspace ?? 'default'} impact={impactResult} added={added} />
```

`web/src/main.tsx` (full replacement):

```tsx
import { useEffect, useMemo, useState } from 'react';
import { createRoot } from 'react-dom/client';
import { App } from './app/App';
import { startLive, type LiveStatus } from './app/live';
import { parseGraph, readFocus, readInlineGraph } from './graph/load';
import type { DepGraph } from './graph/types';
import { createWorkerEngine } from './layout/engine';
import './styles/fonts';
import './styles/tokens.css';
import './styles/app.css';

const upgrade = (found: unknown) => (
  <p className="pad">This graph was made by a newer depenk (graph schema {String(found)}). Upgrade depenk to view it.</p>
);

/** Serve mode: the page has no inlined graph; it comes from /api/graph and updates over SSE. */
function Live() {
  const engine = useMemo(createWorkerEngine, []);
  const [graph, setGraph] = useState<DepGraph | null>(null);
  const [status, setStatus] = useState<LiveStatus>('loading');
  useEffect(() => startLive(setGraph, setStatus), []);
  if (status === 'schema') return upgrade('newer than 1');
  if (!graph) return <p className="pad">{status === 'reconnecting' ? 'Cannot reach depenk serve — retrying…' : 'Loading graph…'}</p>;
  return <App graph={graph} engine={engine} mode="serve" status={status === 'reconnecting' ? 'reconnecting' : 'ok'} />;
}

const root = createRoot(document.getElementById('root')!);
const inline = readInlineGraph(document);
if (inline === null) {
  root.render(<Live />);
} else {
  const loaded = parseGraph(inline);
  if (loaded.ok) root.render(<App graph={loaded.graph} engine={createWorkerEngine()} mode="export" initialFocus={readFocus(document)} />);
  else if (loaded.reason === 'schema') root.render(upgrade(loaded.found));
  else root.render(<p className="pad">The embedded graph could not be read: {loaded.message}</p>);
}
```

- [ ] **Step 4: Run tests, typecheck and build**

Run: `cd web && npx vitest run && npm run build`
Expected: PASS.

- [ ] **Step 5: After screenshot**

Screenshot the inlined fixture page with the Export menu open. Show the user the before and after screenshots.

- [ ] **Step 6: Commit**

```bash
git add web
git commit -m "feat(web): serve-mode live updates over SSE, Export menu (PNG/SVG/HTML), state kept across rescans

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 12: Embedded bundle, `DiagramExporter` and `depenk export`

**Files:**
- Create: `src/Depenk.Server/DiagramBundle.cs`, `src/Depenk.Server/DiagramExporter.cs`, `src/Depenk.Server/DeepLink.cs`, `tests/Depenk.Tests/TestUtil/Cli.cs`
- Modify: `src/Depenk.Server/Depenk.Server.csproj` (BuildWeb and EmbedWeb targets), `src/depenk/Program.cs` (`export` command)
- Test: `tests/Depenk.Tests/Server/DiagramExporterTests.cs`, `tests/Depenk.Tests/Server/CliDiagramTests.cs`

**Interfaces:**
- Consumes: `GraphStore` (Task 1); `AtomicFile` (Plan 2 fixes); `web/dist/index.html` with the two placeholders (Task 3).
- Produces:
  - `DiagramBundle.GraphPlaceholder`, `DiagramBundle.FocusPlaceholder`, `DiagramBundle.Html` (the embedded resource `Depenk.Server.index.html`)
  - `DiagramExporter.EscapeJson(string)`
  - `DiagramExporter.Render(string bundleHtml, DepGraph graph, string? focus)`
  - `DiagramExporter.DefaultPath(string workspace)`
  - `DiagramExporter.Export(GraphStore store, string? path, string? focus, string? bundleHtml = null) → string fullPath`. This rescans if the graph is stale, resolves the focus via `ResolveAny` (throwing `QueryException` when it doesn't resolve) and writes atomically.
  - `DeepLink.Encode(string id)` and `DeepLink.For(Uri baseUrl, string? id)`
  - `Cli.Dll()` and `Cli.RunAsync(args, timeout?) → (Exit, Stdout, Stderr)`
- CLI: `depenk export [--workspace] [--out <file>] [--focus <node>]` prints the absolute path. Exit codes: 0 ok, 1 write or unexpected failure, 2 invalid config or unknown `--focus`, 3 workspace not found.

- [ ] **Step 1: Write the failing tests**

`tests/Depenk.Tests/TestUtil/Cli.cs`:

```csharp
using System.Diagnostics;

namespace Depenk.Tests.TestUtil;

/// <summary>Runs the built CLI (src/depenk/bin/&lt;Configuration&gt;/net9.0/depenk.dll) as a separate process.</summary>
public static class Cli
{
    public static string Dll()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Depenk.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var config = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}") ? "Release" : "Debug";
        var dll = Path.Combine(dir!.FullName, "src", "depenk", "bin", config, "net9.0", "depenk.dll");
        Assert.True(File.Exists(dll), $"CLI not built at {dll}");
        return dll;
    }

    public static async Task<(int Exit, string Stdout, string Stderr)> RunAsync(IEnumerable<string> args, TimeSpan? timeout = null)
    {
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add(Dll());
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(90));
        await p.WaitForExitAsync(cts.Token);
        return (p.ExitCode, await stdout, await stderr);
    }
}
```

`tests/Depenk.Tests/Server/DiagramExporterTests.cs`:

```csharp
using Depenk.Core;
using Depenk.Core.Model;
using Depenk.Query;
using Depenk.Server;

namespace Depenk.Tests.Server;

public class DiagramExporterTests
{
    private const string Bundle = "<html><head>" + DiagramBundle.FocusPlaceholder + DiagramBundle.GraphPlaceholder + "</head><body></body></html>";

    private static string InlineJson(string html)
    {
        const string open = "id=\"depenk-graph\">";
        var start = html.IndexOf(open, StringComparison.Ordinal) + open.Length;
        return html[start..html.IndexOf("</script>", start, StringComparison.Ordinal)];
    }

    private static DepGraph Tiny(string modelName) => new()
    {
        Repos = { new RepoNode("repo:a", "a", "a", null, false) },
        Models = { new ModelNode("model:x", "a", null, modelName, ModelKind.Class, [], null, null) },
    };

    [Fact]
    public void SharedEscapeVector_MatchesTheUi() =>
        Assert.Equal("{\"a\":\"\\u003c/script\\u003e\\u003cb\\u003e\\u0026\\u2028\"}", DiagramExporter.EscapeJson("{\"a\":\"</script><b>&\u2028\"}"));

    [Fact]
    public void Render_InlinesTheGraph_AndHostileNamesStayData()
    {
        const string hostile = "</script><script>alert(1)</script>";
        var html = DiagramExporter.Render(Bundle, Tiny(hostile), null);

        Assert.DoesNotContain("<script>alert", html);
        Assert.Equal(hostile, GraphJson.Deserialize(InlineJson(html)).Models.Single().FullName);
        Assert.Contains("<meta name=\"depenk-focus\" content=\"\">", html);
    }

    [Fact]
    public void Render_EncodesTheFocusAttribute() =>
        Assert.Contains("content=\"a&quot;b\"", DiagramExporter.Render(Bundle, Tiny("M"), "a\"b"));

    [Fact]
    public void Render_WithoutPlaceholders_Throws() =>
        Assert.Throws<InvalidOperationException>(() => DiagramExporter.Render("<html></html>", Tiny("M"), null));

    [Fact]
    public void EmbeddedBundle_IsSelfContained_WithBothPlaceholders()
    {
        var html = DiagramBundle.Html;
        Assert.Contains(DiagramBundle.GraphPlaceholder, html);
        Assert.Contains(DiagramBundle.FocusPlaceholder, html);
        Assert.DoesNotContain("<script src", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fonts.googleapis", html);
    }

    [Fact]
    public void Export_WritesTheDefaultPath_WithTheResolvedFocus()
    {
        using var ws = FixtureScanTests.CopyFixture();
        var path = DiagramExporter.Export(new GraphStore(ws.Root), null, "orders", Bundle);

        Assert.Equal(DiagramExporter.DefaultPath(Path.GetFullPath(ws.Root)), path);
        var html = File.ReadAllText(path);
        Assert.Contains("content=\"repo:orders\"", html);
        Assert.Equal(5, GraphJson.Deserialize(InlineJson(html)).Repos.Count);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
    }

    [Fact]
    public void Export_UnknownFocus_IsNotFound_AndWritesNothing()
    {
        using var ws = FixtureScanTests.CopyFixture();
        var ex = Assert.Throws<QueryException>(() => DiagramExporter.Export(new GraphStore(ws.Root), null, "ordrs", Bundle));
        Assert.Equal("not_found", ex.Code);
        Assert.Contains("repo:orders", ex.Suggestions);
        Assert.False(File.Exists(DiagramExporter.DefaultPath(ws.Root)));
    }

    [Fact]
    public void DeepLink_MatchesTheUiEncoding()
    {
        Assert.Equal("ep:orders:GET:/api/orders/%7Bid%7D", DeepLink.Encode("ep:orders:GET:/api/orders/{id}"));
        Assert.Equal("http://127.0.0.1:5000/#/repo:orders", DeepLink.For(new Uri("http://127.0.0.1:5000/"), "repo:orders"));
        Assert.Equal("http://127.0.0.1:5000/", DeepLink.For(new Uri("http://127.0.0.1:5000/"), null));
    }
}
```

`tests/Depenk.Tests/Server/CliDiagramTests.cs`:

```csharp
using Depenk.Tests.TestUtil;

namespace Depenk.Tests.Server;

public class CliDiagramTests
{
    [Fact]
    public async Task Export_PrintsThePath_OfASelfContainedFile()
    {
        using var ws = FixtureScanTests.CopyFixture();
        var (exit, stdout, stderr) = await Cli.RunAsync(["export", "--workspace", ws.Root]);

        Assert.True(exit == 0, stderr);
        var path = stdout.Trim();
        Assert.True(File.Exists(path), path);
        Assert.Contains("id=\"depenk-graph\">{", File.ReadAllText(path));
    }

    [Fact]
    public async Task Export_UnknownFocus_ExitsWith2_AndSuggests()
    {
        using var ws = FixtureScanTests.CopyFixture();
        var (exit, _, stderr) = await Cli.RunAsync(["export", "--workspace", ws.Root, "--focus", "ordrs"]);
        Assert.Equal(2, exit);
        Assert.Contains("repo:orders", stderr);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet build tests/Depenk.Tests`
Expected: FAIL with `CS0103` for `DiagramExporter`, `DiagramBundle` and `DeepLink`.

- [ ] **Step 3: Implement**

`src/Depenk.Server/DiagramBundle.cs`:

```csharp
namespace Depenk.Server;

/// <summary>The single-file UI (web/dist/index.html), embedded at build time.</summary>
public static class DiagramBundle
{
    // Must match web/index.html and web/src/app/exportHtml.ts byte for byte.
    public const string GraphPlaceholder = "<script type=\"application/json\" id=\"depenk-graph\"></script>";
    public const string FocusPlaceholder = "<meta name=\"depenk-focus\" content=\"\">";
    public const string ResourceName = "Depenk.Server.index.html";

    private static readonly Lazy<string> Embedded = new(() =>
    {
        using var stream = typeof(DiagramBundle).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("The diagram UI is not embedded in this build; run `npm ci && npm run build` in web/ and rebuild.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    public static string Html => Embedded.Value;
}
```

`src/Depenk.Server/DiagramExporter.cs`:

```csharp
using System.Net;
using System.Text.Json;
using Depenk.Core;
using Depenk.Core.Model;

namespace Depenk.Server;

public static class DiagramExporter
{
    private static readonly JsonSerializerOptions Compact = new(GraphJson.Options) { WriteIndented = false };

    /// <summary>JSON that is safe inside &lt;script&gt;: same rules as the UI's escapeJsonForHtml.</summary>
    public static string EscapeJson(string json) =>
        json.Replace("<", "\\u003c").Replace(">", "\\u003e").Replace("&", "\\u0026").Replace("\u2028", "\\u2028").Replace("\u2029", "\\u2029");

    public static string Render(string bundleHtml, DepGraph graph, string? focus)
    {
        if (!bundleHtml.Contains(DiagramBundle.GraphPlaceholder, StringComparison.Ordinal)
            || !bundleHtml.Contains(DiagramBundle.FocusPlaceholder, StringComparison.Ordinal))
            throw new InvalidOperationException("The diagram bundle has no data placeholders.");
        var json = EscapeJson(JsonSerializer.Serialize(graph, Compact));
        return bundleHtml
            .Replace(DiagramBundle.GraphPlaceholder, $"<script type=\"application/json\" id=\"depenk-graph\">{json}</script>", StringComparison.Ordinal)
            .Replace(DiagramBundle.FocusPlaceholder, $"<meta name=\"depenk-focus\" content=\"{WebUtility.HtmlEncode(focus ?? "")}\">", StringComparison.Ordinal);
    }

    public static string DefaultPath(string workspace) => Path.Combine(workspace, ".depenk", "depenk.html");

    /// <summary>Writes the self-contained diagram and returns its full path. Exports never contain source code.</summary>
    public static string Export(GraphStore store, string? path, string? focus, string? bundleHtml = null)
    {
        var snapshot = store.Current();
        if (snapshot.Stale) snapshot = store.Rescan();
        var focusId = focus is null ? null : snapshot.Index.ResolveAny(focus);
        var target = Path.GetFullPath(path ?? DefaultPath(store.Workspace));
        AtomicFile.WriteAllText(target, Render(bundleHtml ?? DiagramBundle.Html, snapshot.Graph, focusId));
        return target;
    }
}
```

`src/Depenk.Server/DeepLink.cs`:

```csharp
namespace Depenk.Server;

/// <summary>Deep links into the UI: #/&lt;id&gt; with the same encoding as web/src/app/route.ts encodeId.</summary>
public static class DeepLink
{
    public static string Encode(string id) => Uri.EscapeDataString(id).Replace("%3A", ":").Replace("%2F", "/");

    public static string For(Uri baseUrl, string? id) => id is null ? baseUrl.ToString() : $"{baseUrl}#/{Encode(id)}";
}
```

Replace `src/Depenk.Server/Depenk.Server.csproj` with:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <WebDir>$([MSBuild]::NormalizeDirectory('$(MSBuildThisFileDirectory)', '..', '..', 'web'))</WebDir>
    <WebBundle>$(WebDir)dist\index.html</WebBundle>
  </PropertyGroup>
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\Depenk.Query\Depenk.Query.csproj" />
    <ProjectReference Include="..\Depenk.Analysis\Depenk.Analysis.csproj" />
  </ItemGroup>
  <ItemGroup>
    <WebInputs Include="$(WebDir)src\**\*;$(WebDir)index.html;$(WebDir)package.json;$(WebDir)package-lock.json;$(WebDir)vite.config.ts;$(WebDir)tsconfig.json" />
  </ItemGroup>

  <!-- Rebuild the UI when its sources change. Set DepenkSkipWebBuild=true when CI has already built web/. -->
  <Target Name="BuildWeb" BeforeTargets="BeforeBuild" Inputs="@(WebInputs)" Outputs="$(WebBundle)" Condition="'$(DepenkSkipWebBuild)' != 'true'">
    <Exec Command="npm ci" WorkingDirectory="$(WebDir)" Condition="!Exists('$(WebDir)node_modules\.package-lock.json')" />
    <Exec Command="npm run build" WorkingDirectory="$(WebDir)" />
  </Target>

  <Target Name="EmbedWeb" AfterTargets="BuildWeb" BeforeTargets="BeforeBuild">
    <Error Condition="!Exists('$(WebBundle)')" Text="web/dist/index.html is missing. Run `npm ci` and `npm run build` in web/ (or build without DepenkSkipWebBuild=true)." />
    <ItemGroup>
      <EmbeddedResource Include="$(WebBundle)" LogicalName="Depenk.Server.index.html" />
    </ItemGroup>
  </Target>
</Project>
```

`src/depenk/Program.cs`:
- add `using Depenk.Query;` (for `QueryException`) if it is not already there
- add these options next to the others:

```csharp
var outOption = new Option<FileInfo?>("--out", "Where to write the HTML file (default: <workspace>/.depenk/depenk.html)");
var focusOption = new Option<string?>("--focus", "Node to open on: an id, repo name, package id, \"VERB /route\" or model name");
```

- add this command before `var root = …`:

```csharp
var export = new Command("export", "Write the interactive diagram as one self-contained HTML file (opens offline)")
    { workspaceOption, outOption, focusOption };
export.SetHandler(ctx =>
{
    var ws = ResolveWorkspace(ctx.ParseResult.GetValueForOption(workspaceOption));
    if (!WorkspaceExists(ws)) { ctx.ExitCode = 3; return; }
    Console.Error.WriteLine($"Workspace: {ws}");
    try
    {
        var path = DiagramExporter.Export(new GraphStore(ws), ctx.ParseResult.GetValueForOption(outOption)?.FullName,
            ctx.ParseResult.GetValueForOption(focusOption));
        Console.WriteLine(path);
        ctx.ExitCode = 0;
    }
    catch (ConfigException ex) { Console.Error.WriteLine(ex.Message); ctx.ExitCode = 2; }
    catch (QueryException ex)
    {
        Console.Error.WriteLine($"depenk: {ex.Message}{(ex.Suggestions.Count > 0 ? $" (did you mean: {string.Join(", ", ex.Suggestions)})" : "")}");
        ctx.ExitCode = 2;
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"depenk: could not write the diagram: {ex.Message}");
        ctx.ExitCode = 1;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"depenk: export failed: {ex.GetType().Name}: {ex.Message}");
        ctx.ExitCode = 1;
    }
});
```

- change the root command to `new RootCommand("depenk: cross-repo C# dependency explorer") { scan, export, mcp, query }`
- update the exit-code comment at the top to read: `2 invalid depenk.yml, --json or --focus`

- [ ] **Step 4: Run tests**

Run: `dotnet build Depenk.sln` (this runs `npm ci` / `npm run build` the first time; expect 0 warnings), then `dotnet test tests/Depenk.Tests --filter "FullyQualifiedName~Depenk.Tests.Server"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: depenk export writes the self-contained diagram; UI bundle embedded in Depenk.Server

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 13: `DiagramServer` (loopback, ETag, SSE, Host guard), workspace watcher, `depenk serve`

**Files:**
- Create: `src/Depenk.Server/WorkspaceWatcher.cs`, `src/Depenk.Server/DiagramServer.cs`
- Modify: `src/depenk/Program.cs` (`serve` command), `tests/Depenk.Tests/Server/CliDiagramTests.cs` (serve test)
- Test: `tests/Depenk.Tests/Server/WorkspaceWatcherTests.cs`, `tests/Depenk.Tests/Server/DiagramServerTests.cs`

**Interfaces:**
- Consumes: `GraphStore` (Task 1); `DiagramBundle` (Task 12).
- Produces:
  - `WorkspaceWatcher(string workspace, Action onChange, TimeSpan? debounce = null) : IDisposable`, plus `static bool IsRelevant(string relativePath)`.
  - `DiagramServerOptions(int Port = 0, bool Watch = true, string? BundleHtml = null, TimeSpan? Debounce = null, TimeSpan? Heartbeat = null)`.
  - `DiagramServer : IAsyncDisposable`:
    - `static Task<DiagramServer> StartAsync(GraphStore store, DiagramServerOptions? options = null, CancellationToken ct = default)`
    - `Uri Url` (always `http://127.0.0.1:<port>/`) and `int Port`
    - `void Rescan()` (logs failures to stderr and keeps the last good graph)
    - `void NotifyGraphChanged()`
  - When the port is taken, `StartAsync` throws `IOException`.
- CLI: `depenk serve [--workspace] [--port <n>] [--no-open]` prints `depenk serve: <url>` and runs until Ctrl+C. Exit codes: 1 if the port is in use, 2 for invalid config, 3 if the workspace is not found.

- [ ] **Step 1: Write the failing tests**

`tests/Depenk.Tests/Server/WorkspaceWatcherTests.cs`:

```csharp
using Depenk.Server;

namespace Depenk.Tests.Server;

public class WorkspaceWatcherTests
{
    [Theory]
    [InlineData("orders/src/Orders.Api/OrdersController.cs", true)]
    [InlineData("orders/src/Orders.Api/Orders.Api.csproj", true)]
    [InlineData("Directory.Build.props", true)]
    [InlineData("orders/build/Common.targets", true)]
    [InlineData("depenk.yml", true)]
    [InlineData(".depenk/graph.json", false)]
    [InlineData(".depenk/depenk.html", false)]
    [InlineData("orders/src/Orders.Api/bin/Debug/Gen.cs", false)]
    [InlineData("orders/src/Orders.Api/obj/Gen.cs", false)]
    [InlineData("orders/.git/HEAD", false)]
    [InlineData("tools/node_modules/x/a.cs", false)]
    [InlineData("README.md", false)]
    public void OnlySourceAndProjectFilesTriggerRescans(string path, bool relevant) =>
        Assert.Equal(relevant, WorkspaceWatcher.IsRelevant(path.Replace('/', Path.DirectorySeparatorChar)));
}
```

`tests/Depenk.Tests/Server/DiagramServerTests.cs`:

```csharp
using System.Net;
using Depenk.Server;

namespace Depenk.Tests.Server;

public class DiagramServerTests
{
    private const string Bundle = "<html>BUNDLE</html>";
    private const string CancelController = """
        using Microsoft.AspNetCore.Mvc;
        namespace Acme.Orders.Api;
        [Route("api/orders")]
        public class CancelController : ControllerBase { [HttpPost("{id}/cancel")] public Task Cancel(Guid id) => Task.CompletedTask; }
        """;

    private static Task<DiagramServer> Start(GraphStore store, bool watch = false, TimeSpan? debounce = null, int port = 0) =>
        DiagramServer.StartAsync(store, new DiagramServerOptions(Port: port, Watch: watch, BundleHtml: Bundle, Debounce: debounce));

    private static async Task<bool> ReadUntilAsync(StreamReader reader, string line, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            while (await reader.ReadLineAsync(cts.Token) is { } l) if (l == line) return true;
        }
        catch (OperationCanceledException) { }
        return false;
    }

    [Fact]
    public async Task ServesTheBundle_OnLoopbackOnly()
    {
        using var ws = FixtureScanTests.CopyFixture();
        await using var server = await Start(new GraphStore(ws.Root));
        using var http = new HttpClient();

        Assert.Equal("127.0.0.1", server.Url.Host);
        var res = await http.GetAsync(server.Url);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("text/html", res.Content.Headers.ContentType?.MediaType);
        Assert.Equal("<html>BUNDLE</html>", await res.Content.ReadAsStringAsync());
        Assert.True(res.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task Graph_HasAnETag_AndAnswers304WhenUnchanged()
    {
        using var ws = FixtureScanTests.CopyFixture();
        await using var server = await Start(new GraphStore(ws.Root));
        using var http = new HttpClient();

        var first = await http.GetAsync(new Uri(server.Url, "api/graph"));
        Assert.Contains("\"repos\":[", await first.Content.ReadAsStringAsync());
        var etag = first.Headers.ETag;
        Assert.NotNull(etag);

        var again = new HttpRequestMessage(HttpMethod.Get, new Uri(server.Url, "api/graph"));
        again.Headers.IfNoneMatch.Add(etag!);
        Assert.Equal(HttpStatusCode.NotModified, (await http.SendAsync(again)).StatusCode);
    }

    [Fact]
    public async Task ForeignHostHeader_IsRejected()
    {
        using var ws = FixtureScanTests.CopyFixture();
        await using var server = await Start(new GraphStore(ws.Root));
        using var http = new HttpClient();

        var req = new HttpRequestMessage(HttpMethod.Get, new Uri(server.Url, "api/graph"));
        req.Headers.Host = $"evil.example:{server.Port}";
        var res = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Equal("", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Events_AnnounceRescans()
    {
        using var ws = FixtureScanTests.CopyFixture();
        await using var server = await Start(new GraphStore(ws.Root));
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var res = await http.GetAsync(new Uri(server.Url, "api/events"), HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal("text/event-stream", res.Content.Headers.ContentType?.MediaType);
        using var reader = new StreamReader(await res.Content.ReadAsStreamAsync());

        Assert.True(await ReadUntilAsync(reader, ": connected", TimeSpan.FromSeconds(10)));
        server.Rescan();
        Assert.True(await ReadUntilAsync(reader, "event: graph-changed", TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public async Task Watcher_RescansOnceForABurstOfEdits_AndServesTheNewGraph()
    {
        using var ws = FixtureScanTests.CopyFixture();
        var store = new GraphStore(ws.Root);
        await using var server = await Start(store, watch: true, debounce: TimeSpan.FromMilliseconds(300));
        var scansBefore = store.ScanCount;
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var res = await http.GetAsync(new Uri(server.Url, "api/events"), HttpCompletionOption.ResponseHeadersRead);
        using var reader = new StreamReader(await res.Content.ReadAsStreamAsync());
        Assert.True(await ReadUntilAsync(reader, ": connected", TimeSpan.FromSeconds(10)));

        ws.File("orders/src/Orders.Api/CancelController.cs", CancelController);
        for (var i = 0; i < 4; i++) ws.File($"orders/src/Orders.Api/Extra{i}.cs", $"namespace X; public class Extra{i} {{}}");

        Assert.True(await ReadUntilAsync(reader, "event: graph-changed", TimeSpan.FromSeconds(30)));
        Assert.Contains("/api/orders/{id}/cancel", await http.GetStringAsync(new Uri(server.Url, "api/graph")));
        await Task.Delay(1500);
        Assert.Equal(scansBefore + 1, store.ScanCount);
    }

    [Fact]
    public async Task PortInUse_Throws()
    {
        using var ws = FixtureScanTests.CopyFixture();
        var store = new GraphStore(ws.Root);
        await using var first = await Start(store);
        await Assert.ThrowsAnyAsync<IOException>(() => Start(store, port: first.Port));
    }
}
```

Add to `tests/Depenk.Tests/Server/CliDiagramTests.cs` (and add `using System.Diagnostics;` and `using Depenk.Server;`):

```csharp
    [Fact]
    public async Task Serve_PrintsALoopbackUrl_ThatServesTheUi()
    {
        using var ws = FixtureScanTests.CopyFixture();
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { Cli.Dll(), "serve", "--workspace", ws.Root, "--no-open" }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            string? line;
            do line = await p.StandardOutput.ReadLineAsync(cts.Token);
            while (line is not null && !line.StartsWith("depenk serve: ", StringComparison.Ordinal));
            Assert.NotNull(line);
            var url = line!["depenk serve: ".Length..];
            Assert.StartsWith("http://127.0.0.1:", url);
            using var http = new HttpClient();
            Assert.Contains(DiagramBundle.GraphPlaceholder, await http.GetStringAsync(url)); // serve mode: no inlined data
        }
        finally
        {
            p.Kill(entireProcessTree: true);
        }
    }
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet build tests/Depenk.Tests`
Expected: FAIL with `CS0246` for `WorkspaceWatcher`, `DiagramServer` and `DiagramServerOptions`.

- [ ] **Step 3: Implement**

`src/Depenk.Server/WorkspaceWatcher.cs`:

```csharp
namespace Depenk.Server;

/// <summary>Watches the workspace for source/project changes and calls back once per burst (debounced).</summary>
public sealed class WorkspaceWatcher : IDisposable
{
    private static readonly string[] Extensions = [".cs", ".csproj", ".props", ".targets"];
    private static readonly string[] IgnoredDirectories = [".depenk", "bin", "obj", ".git", "node_modules"];

    private readonly string _root;
    private readonly TimeSpan _debounce;
    private readonly Timer _timer;
    private readonly FileSystemWatcher _fsw;

    public WorkspaceWatcher(string workspace, Action onChange, TimeSpan? debounce = null)
    {
        _root = Path.GetFullPath(workspace);
        _debounce = debounce ?? TimeSpan.FromMilliseconds(750);
        _timer = new Timer(_ => onChange(), null, Timeout.Infinite, Timeout.Infinite);
        _fsw = new FileSystemWatcher(_root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            InternalBufferSize = 64 * 1024,
        };
        _fsw.Changed += (_, e) => Touch(e.FullPath);
        _fsw.Created += (_, e) => Touch(e.FullPath);
        _fsw.Deleted += (_, e) => Touch(e.FullPath);
        _fsw.Renamed += (_, e) => { Touch(e.OldFullPath); Touch(e.FullPath); };
        _fsw.Error += (_, _) => Schedule(); // buffer overflow: we lost events, so rescan to be safe
        _fsw.EnableRaisingEvents = true;
    }

    public static bool IsRelevant(string relativePath)
    {
        var parts = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (parts.Any(p => IgnoredDirectories.Contains(p, StringComparer.OrdinalIgnoreCase))) return false;
        var name = parts[^1];
        return name.Equals("depenk.yml", StringComparison.OrdinalIgnoreCase)
            || Extensions.Any(ext => name.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
    }

    private void Touch(string fullPath)
    {
        if (IsRelevant(Path.GetRelativePath(_root, fullPath))) Schedule();
    }

    private void Schedule() => _timer.Change(_debounce, Timeout.InfiniteTimeSpan);

    public void Dispose()
    {
        _fsw.Dispose();
        _timer.Dispose();
    }
}
```

`src/Depenk.Server/DiagramServer.cs`:

```csharp
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Depenk.Core;
using Depenk.Core.Model;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Depenk.Server;

public sealed record DiagramServerOptions(int Port = 0, bool Watch = true, string? BundleHtml = null, TimeSpan? Debounce = null, TimeSpan? Heartbeat = null);

/// <summary>
/// The live diagram: GET / (UI), GET /api/graph (ETag), GET /api/events (SSE "graph-changed").
/// Loopback only, Host-checked against DNS rebinding, read-only. Never writes to stdout (it can run inside `depenk mcp`).
/// </summary>
public sealed class DiagramServer : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Compact = new(GraphJson.Options) { WriteIndented = false };

    private readonly GraphStore _store;
    private readonly string _bundle;
    private readonly TimeSpan _heartbeat;
    private readonly object _gate = new();
    private readonly List<Channel<string>> _clients = [];
    private (DepGraph Graph, string Json, string ETag)? _cache;
    private WebApplication? _app;
    private WorkspaceWatcher? _watcher;
    private volatile int _port = -1;

    private DiagramServer(GraphStore store, string bundle, TimeSpan heartbeat)
    {
        _store = store;
        _bundle = bundle;
        _heartbeat = heartbeat;
    }

    public Uri Url { get; private set; } = null!;
    public int Port => _port;

    public static async Task<DiagramServer> StartAsync(GraphStore store, DiagramServerOptions? options = null, CancellationToken ct = default)
    {
        options ??= new DiagramServerOptions();
        var server = new DiagramServer(store, options.BundleHtml ?? DiagramBundle.Html, options.Heartbeat ?? TimeSpan.FromSeconds(25));

        // Empty builder: no appsettings from the cwd, no logging providers (nothing reaches stdout).
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.WebHost.UseKestrelCore().ConfigureKestrel(k => k.Listen(IPAddress.Loopback, options.Port));
        builder.Services.AddRoutingCore();
        var app = builder.Build();
        app.Use(server.GuardHostAsync);
        app.UseRouting();
        app.MapGet("/", server.IndexAsync);
        app.MapGet("/api/graph", server.GraphAsync);
        app.MapGet("/api/events", server.EventsAsync);
        await app.StartAsync(ct); // IOException when the port is taken

        var address = app.Services.GetRequiredService<IServer>().Features.GetRequiredFeature<IServerAddressesFeature>().Addresses.First();
        server._port = new Uri(address).Port;
        server.Url = new Uri($"http://127.0.0.1:{server._port}/");
        server._app = app;

        if (options.Watch) server._watcher = new WorkspaceWatcher(store.Workspace, server.Rescan, options.Debounce);
        var refresh = store.StartBackgroundRefresh();
        if (refresh is not null)
            _ = refresh.ContinueWith(t =>
            {
                if (t.IsFaulted) Console.Error.WriteLine($"depenk: background scan failed: {t.Exception?.GetBaseException().Message}");
                else server.NotifyGraphChanged();
            }, TaskScheduler.Default);
        return server;
    }

    public void Rescan()
    {
        try
        {
            _store.Rescan();
            NotifyGraphChanged();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"depenk: rescan failed, still serving the previous graph: {ex.GetBaseException().Message}");
        }
    }

    public void NotifyGraphChanged()
    {
        var (_, etag) = CurrentJson();
        var message = $"event: graph-changed\ndata: {{\"etag\":{JsonSerializer.Serialize(etag)}}}\n\n";
        lock (_gate) foreach (var c in _clients) c.Writer.TryWrite(message);
    }

    private Task GuardHostAsync(HttpContext ctx, RequestDelegate next)
    {
        var host = ctx.Request.Host;
        var ok = host.Port == _port && (host.Host == "127.0.0.1" || host.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase));
        if (ok) return next(ctx);
        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }

    private Task IndexAsync(HttpContext ctx)
    {
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.Headers.CacheControl = "no-store";
        return ctx.Response.WriteAsync(_bundle, ctx.RequestAborted);
    }

    private (string Json, string ETag) CurrentJson()
    {
        var graph = _store.Current().Graph;
        lock (_gate)
        {
            if (_cache is { } c && ReferenceEquals(c.Graph, graph)) return (c.Json, c.ETag);
            var json = JsonSerializer.Serialize(graph, Compact);
            var etag = $"\"{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)))[..16]}\"";
            _cache = (graph, json, etag);
            return (json, etag);
        }
    }

    private Task GraphAsync(HttpContext ctx)
    {
        var (json, etag) = CurrentJson();
        ctx.Response.Headers.ETag = etag;
        ctx.Response.Headers.CacheControl = "no-cache";
        if (ctx.Request.Headers.IfNoneMatch.Contains(etag))
        {
            ctx.Response.StatusCode = StatusCodes.Status304NotModified;
            return Task.CompletedTask;
        }
        ctx.Response.ContentType = "application/json; charset=utf-8";
        return ctx.Response.WriteAsync(json, ctx.RequestAborted);
    }

    private async Task EventsAsync(HttpContext ctx)
    {
        ctx.Response.ContentType = "text/event-stream";
        ctx.Response.Headers.CacheControl = "no-store";
        var channel = Channel.CreateUnbounded<string>();
        lock (_gate) _clients.Add(channel);
        var ct = ctx.RequestAborted;
        try
        {
            await ctx.Response.WriteAsync(": connected\n\n", ct);
            await ctx.Response.Body.FlushAsync(ct);
            while (!ct.IsCancellationRequested)
            {
                using var beat = CancellationTokenSource.CreateLinkedTokenSource(ct);
                beat.CancelAfter(_heartbeat);
                string message;
                try { message = await channel.Reader.ReadAsync(beat.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { message = ": ping\n\n"; }
                await ctx.Response.WriteAsync(message, ct);
                await ctx.Response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException) { /* the client went away */ }
        catch (ChannelClosedException) { /* the server is stopping */ }
        finally
        {
            lock (_gate) _clients.Remove(channel);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _watcher?.Dispose();
        lock (_gate) foreach (var c in _clients) c.Writer.TryComplete();
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
```

`src/depenk/Program.cs`. Add these options:

```csharp
var portOption = new Option<int>("--port", () => 0, "Port on 127.0.0.1 (0 = any free port)");
var noOpenOption = new Option<bool>("--no-open", "Don't open a browser");
```

and this command before `var root = …`:

```csharp
var serve = new Command("serve", "Serve the live diagram on http://127.0.0.1; it updates as the code changes")
    { workspaceOption, portOption, noOpenOption };
serve.SetHandler(async ctx =>
{
    var ws = ResolveWorkspace(ctx.ParseResult.GetValueForOption(workspaceOption));
    if (!WorkspaceExists(ws)) { ctx.ExitCode = 3; return; }
    Console.Error.WriteLine($"Workspace: {ws}");
    var port = ctx.ParseResult.GetValueForOption(portOption);
    var store = new GraphStore(ws);
    try { store.Current(); }
    catch (ConfigException ex) { Console.Error.WriteLine(ex.Message); ctx.ExitCode = 2; return; }

    DiagramServer server;
    try { server = await DiagramServer.StartAsync(store, new DiagramServerOptions(Port: port)); }
    catch (IOException ex) { Console.Error.WriteLine($"depenk: port {port} is already in use ({ex.Message})"); ctx.ExitCode = 1; return; }

    await using (server)
    {
        Console.WriteLine($"depenk serve: {server.Url}");
        if (!ctx.ParseResult.GetValueForOption(noOpenOption))
        {
            try { Process.Start(new ProcessStartInfo(server.Url.ToString()) { UseShellExecute = true }); }
            catch (Exception ex) { Console.Error.WriteLine($"depenk: could not open a browser ({ex.Message}); open the URL above."); }
        }
        try { await Task.Delay(Timeout.Infinite, ctx.GetCancellationToken()); }
        catch (OperationCanceledException) { /* Ctrl+C */ }
    }
    ctx.ExitCode = 0;
});
```

Add `serve` to the root command: `{ scan, export, serve, mcp, query }`.

- [ ] **Step 4: Run tests**

Run: `dotnet build Depenk.sln` (expect 0 warnings), then `dotnet test tests/Depenk.Tests --filter "FullyQualifiedName~Depenk.Tests.Server"`
Expected: PASS.

- [ ] **Step 5: Manual check**

Run `dotnet run --project src/depenk -- serve --workspace <a copy of the fixture with .git folders>`. In the browser:
- edit a controller and watch the map update within about 2 s, without losing the selection
- stop the server and confirm the "Lost connection — retrying" banner appears
- restart the server and confirm the banner clears

Take a screenshot of the live page and show it to the user.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat: depenk serve: loopback live diagram with ETag graph, SSE updates, debounced watcher and Host guard

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 14: MCP `export_diagram` and `open_diagram`; skill, README and version 0.3.0

**Files:**
- Create: `src/Depenk.Mcp/DiagramHost.cs`
- Modify: `src/Depenk.Mcp/DepenkTools.cs`, `src/Depenk.Mcp/DepenkMcpServer.cs`, `src/Depenk.Mcp/ToolJson.cs`, `skills/depenk/SKILL.md`, `README.md`, `Directory.Build.props`, `.claude-plugin/plugin.json`
- Modify tests: `tests/Depenk.Tests/Mcp/McpServerTests.cs`, `tests/Depenk.Tests/Mcp/McpStdioTests.cs`
- Test: `tests/Depenk.Tests/Mcp/DiagramToolsTests.cs`

**Interfaces:**
- Consumes: `DiagramExporter.Export`, `DeepLink.For` (Task 12); `DiagramServer`, `DiagramServerOptions` (Task 13); `CurrentSnapshot`, `IsToolFailure`, `Fail` (DepenkTools, Plan 2 fixes).
- Produces:
  - `DiagramHost(GraphStore) : IAsyncDisposable, IDisposable`, with `Task<Uri> EnsureStartedAsync(CancellationToken)`. It's a singleton registered by `AddDepenkMcpServer`.
  - The tool `export_diagram(focus?, path?)` returns `data: { path, bytes }`. It is the only tool besides `rescan` with `ReadOnly = false`.
  - The tool `open_diagram(focus?)` returns `data: { url }`.
  - `ToolJson.ErrorFor` maps `IOException` / `UnauthorizedAccessException` to `invalid_argument`, with the hint "Pass a different path, or check permissions."

- [ ] **Step 1: Write the failing tests**

`tests/Depenk.Tests/Mcp/DiagramToolsTests.cs`:

```csharp
using System.Net;
using System.Text.Json;
using Depenk.Tests.TestUtil;

namespace Depenk.Tests.Mcp;

public class DiagramToolsTests
{
    [Fact]
    public async Task ExportDiagram_WritesRelativeToTheWorkspace_WithTheFocus()
    {
        using var ws = FixtureScanTests.CopyFixture();
        await using var h = await McpHarness.StartAsync(ws.Root);

        var data = await h.DataAsync("export_diagram", new() { ["focus"] = "orders", ["path"] = "out/map.html" });

        var path = data.GetProperty("path").GetString()!;
        Assert.Equal(Path.Combine(Path.GetFullPath(ws.Root), "out", "map.html"), path);
        Assert.True(data.GetProperty("bytes").GetInt64() > 0);
        Assert.Contains("content=\"repo:orders\"", File.ReadAllText(path));
    }

    [Fact]
    public async Task ExportDiagram_UnknownFocus_IsNotFoundWithSuggestions()
    {
        using var ws = FixtureScanTests.CopyFixture();
        await using var h = await McpHarness.StartAsync(ws.Root);

        var (isError, text) = await h.CallAsync("export_diagram", new() { ["focus"] = "ordrs" });

        Assert.True(isError);
        using var doc = JsonDocument.Parse(text[text.IndexOf('{')..]);
        Assert.Equal("not_found", doc.RootElement.GetProperty("code").GetString());
        Assert.Contains("repo:orders", doc.RootElement.GetProperty("suggestions").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task OpenDiagram_StartsOneServerPerSession_AndDeepLinksTheFocus()
    {
        using var ws = FixtureScanTests.CopyFixture();
        await using var h = await McpHarness.StartAsync(ws.Root);

        var a = (await h.DataAsync("open_diagram", new() { ["focus"] = "GET /api/orders/{id}" })).GetProperty("url").GetString()!;
        var b = (await h.DataAsync("open_diagram")).GetProperty("url").GetString()!;

        Assert.Matches(@"^http://127\.0\.0\.1:\d+/#/ep:orders:GET:/api/orders/%7Bid%7D$", a);
        Assert.Equal(new Uri(a).Port, new Uri(b).Port);
        using var http = new HttpClient();
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync(b)).StatusCode);
    }
}
```

In `tests/Depenk.Tests/Mcp/McpServerTests.cs`:
- replace `ExpectedTools` with:

```csharp
    private static readonly string[] ExpectedTools =
    [
        "export_diagram", "find_endpoints", "find_model_usages", "get_diagnostics", "get_endpoint", "get_model", "get_repo",
        "get_source", "how_to_call", "impact_of_change", "list_repos", "open_diagram", "rescan", "trace",
    ];
```

- rename the test `ExposesTwelveTools_WithReadOnlyAnnotations_AndOverviewResource` to `ExposesAllTools_WithReadOnlyAnnotations_AndOverviewResource`
- in its body, use `Assert.StartsWith("0.3.0", …)`, and replace the two annotation asserts with:

```csharp
        string[] writers = ["export_diagram", "rescan"];
        Assert.All(tools.Where(t => !writers.Contains(t.Name)), t => Assert.True(t.ProtocolTool.Annotations?.ReadOnlyHint));
        Assert.All(tools.Where(t => writers.Contains(t.Name)), t => Assert.False(t.ProtocolTool.Annotations?.ReadOnlyHint));
```

In `tests/Depenk.Tests/Mcp/McpStdioTests.cs`:
- change both `Assert.Equal(12, …)` to `Assert.Equal(14, …)`
- add `using System.Text.Json;`
- add:

```csharp
    [Fact]
    public async Task OpenDiagram_KeepsStdoutClean_AndServesTheUi()
    {
        using var ws = FixtureScanTests.CopyFixture();
        await using var client = await Connect(["mcp", "--workspace", ws.Root]);

        var r = await client.CallToolAsync("open_diagram", new Dictionary<string, object?>());
        Assert.NotEqual(true, r.IsError);
        var url = JsonDocument.Parse(r.Content.OfType<TextContentBlock>().Single().Text).RootElement.GetProperty("data").GetProperty("url").GetString()!;
        using var http = new HttpClient();
        Assert.Contains("depenk-graph", await http.GetStringAsync(url));

        // a stray stdout write from Kestrel would have corrupted the protocol stream by now
        var after = await client.CallToolAsync("list_repos", new Dictionary<string, object?>());
        Assert.NotEqual(true, after.IsError);
    }
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Depenk.Tests --filter "FullyQualifiedName~Depenk.Tests.Mcp"`
Expected: FAIL. The new tools are unknown, there are 12 tools instead of 14, and the version is 0.2.0.

- [ ] **Step 3: Implement**

`src/Depenk.Mcp/DiagramHost.cs`:

```csharp
using Depenk.Server;

namespace Depenk.Mcp;

/// <summary>One live diagram server per MCP session: started by the first open_diagram, stopped with the host.</summary>
public sealed class DiagramHost(GraphStore store) : IAsyncDisposable, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DiagramServer? _server;

    public async Task<Uri> EnsureStartedAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            _server ??= await DiagramServer.StartAsync(store, new DiagramServerOptions(), ct);
            return _server.Url;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_server is not null) await _server.DisposeAsync();
        _server = null;
    }

    // the generic host disposes the service provider synchronously
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
```

`src/Depenk.Mcp/DepenkMcpServer.cs`:
- in `AddDepenkMcpServer`, after `services.AddSingleton(store);`, add `services.AddSingleton<DiagramHost>();`
- append this sentence to `Instructions`: `" To show the user a map, call open_diagram (live link) or export_diagram (offline HTML file)."`

`src/Depenk.Mcp/ToolJson.cs`. In `ErrorFor`, add this arm before `_ when duringScan`:

```csharp
        IOException or UnauthorizedAccessException => Error(QueryException.InvalidArgument, $"Could not write the file: {e.Message}", "Pass a different path, or check permissions."),
```

`src/Depenk.Mcp/DepenkTools.cs`:
- change the primary constructor to `public sealed class DepenkTools(GraphStore store, DiagramHost diagrams)`
- add `using Depenk.Server;` if it isn't already present
- add these tools before `Rescan`:

```csharp
    [McpServerTool(Name = "export_diagram", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Write the interactive dependency diagram as one self-contained HTML file that opens offline, and return its path. Optionally opens on a node.")]
    public string ExportDiagram(
        [Description("Node to open on: any id, repo name, package id, \"VERB /route\" or model name.")] string? focus = null,
        [Description("Output file; relative paths resolve against the workspace (default .depenk/depenk.html).")] string? path = null)
    {
        var snapshot = CurrentSnapshot(store);
        try
        {
            var full = path is null ? null : Path.IsPathRooted(path) ? path : Path.Combine(store.Workspace, path);
            var written = DiagramExporter.Export(store, full, focus);
            var bytes = new FileInfo(written).Length;
            return ToolJson.Envelope($"Wrote {written} ({bytes / 1024} KB): open it in a browser", snapshot.Stale, new ExportResult(written, bytes));
        }
        catch (Exception e) when (IsToolFailure(e))
        {
            throw Fail(e, duringScan: false);
        }
    }

    [McpServerTool(Name = "open_diagram", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Start the live diagram on http://127.0.0.1 (once per session) and return its link, optionally focused on a node. Give the link to the user; the map updates as code changes.")]
    public async Task<string> OpenDiagram(
        [Description("Node to focus: any id, repo name, package id, \"VERB /route\" or model name.")] string? focus = null,
        CancellationToken cancellationToken = default)
    {
        var snapshot = CurrentSnapshot(store);
        try
        {
            var id = focus is null ? null : snapshot.Index.ResolveAny(focus);
            var url = DeepLink.For(await diagrams.EnsureStartedAsync(cancellationToken), id);
            return ToolJson.Envelope($"Diagram: {url}", snapshot.Stale, new OpenResult(url));
        }
        catch (Exception e) when (IsToolFailure(e))
        {
            throw Fail(e, duringScan: false);
        }
    }
```

- add these records next to `RescanCounts`:

```csharp
    private sealed record ExportResult(string Path, long Bytes);
    private sealed record OpenResult(string Url);
```

`skills/depenk/SKILL.md`. Insert before `## If the depenk tools are missing`:

```markdown
## Showing the user a map

- "Show me", "draw the dependencies", "where does this sit?" → `open_diagram` (pass `focus` to open on a repo,
  endpoint or model) and give the user the link. It updates live as code changes.
- A file to share or attach → `export_diagram` writes one offline HTML file and returns its path.

```

and in the last section, change the final sentence to:

```markdown
then restart Claude Code. Without MCP, the same tools work from the shell: `depenk query <tool> --json '{...}'`, and
`depenk serve` / `depenk export` open the diagram.
```

`Directory.Build.props`: change `<Version>0.2.0</Version>` to `<Version>0.3.0</Version>`. In `.claude-plugin/plugin.json`, change `"version": "0.2.0"` to `"version": "0.3.0"`.

`README.md`:
1. Status line: `> **Status: early (v0.3).** Scanning engine, MCP server and interactive diagram are done; change history is on the [roadmap](#roadmap).`
2. Add this section after "Use it from Claude Code (MCP)":

````markdown
## See it: the interactive diagram

```bash
depenk serve                     # live map on http://127.0.0.1, updates as you edit code
depenk export                    # one offline HTML file: .depenk/depenk.html (share it, attach it)
depenk export --focus orders --out orders.html
```

- **Three depths:** repos, projects (grouped by repo) and call chains (call site → client method → endpoint).
- **Inspector:** contracts as model trees, with `↗ repo` jumps for types from other repos; callers; who publishes and consumes a package.
- **Impact view:** "what breaks if I change this?" for a package, endpoint, model or a single field (`Ctrl K`, then `impact OrderDto.Lines`).
- **Navigation and look:** light and dark themes, deep links (`#/repo:orders`, `#/impact/OrderDto.Lines`), and PNG/SVG export of the current view.

`serve` binds to `127.0.0.1` only. Exports contain paths and line numbers, never source code.
````

3. In the MCP tool table, add these two rows after `rescan`, and change "exposes 12 tools" to "exposes 14 tools":

```markdown
| `export_diagram` | write the offline HTML diagram |
| `open_diagram` | a live diagram link for the user |
```

4. In Roadmap, tick `- [x] **Interactive diagram**` and change its text to: `light/dark map in the ServiceMap style: repo → project → call-chain drill-down, model trees, impact view, Ctrl+K search; exported as one HTML file or served live`.
5. In Development, add: `UI: \`cd web && npm ci && npm test\` (Vitest, including the shared query conformance cases), \`npm run e2e\` for Playwright screenshots. \`dotnet build\` builds the UI automatically (Node 22 required).`

- [ ] **Step 4: Run the full suite**

Run: `dotnet build Depenk.sln` (expect 0 warnings), then `dotnet test tests/Depenk.Tests`
Expected: everything passes, including `PackagingTests` (the skill mentions all 14 tools, and the plugin version matches 0.3.0).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(mcp): export_diagram and open_diagram; skill, README and version 0.3.0

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 15: Playwright screenshots (both themes), performance budget, CI

**Files:**
- Create: `web/playwright.config.ts`, `web/e2e/global-setup.ts`, `web/e2e/screens.spec.ts`, `web/e2e/perf.spec.ts`, `web/test/util/synthetic.ts`, `web/test/synthetic.test.ts`, `.github/workflows/ci.yml`
- Modify: `.gitignore` (Playwright output)
- Generated and committed: `web/e2e/screens.spec.ts-snapshots/*.png` (baselines)

**Interfaces:**
- Consumes: the built `web/dist/index.html` (Task 3+); `buildExportHtml` (Task 11); `fixtureJson` (Task 3); the `data-ready="true"` attribute and `performance.mark('depenk-ready')` (Task 8).
- Produces: `makeSyntheticGraph(repos = 50, controllers = 10, endpointsPerController = 10): DepGraph`. It mirrors `SyntheticWorkspace`: each repo has an Api and a Client project, 100 endpoints and 100 client methods, and a consumer calling the clients of the next 3 repos.

- [ ] **Step 1: Synthetic graph (unit-tested), Playwright config and global setup**

`web/test/util/synthetic.ts`:

```ts
import type { DepGraph, Edge } from '../../src/graph/types';

/** Same shape as tests/Depenk.Tests/TestUtil/SyntheticWorkspace.cs, built directly as a graph. */
export function makeSyntheticGraph(repos = 50, controllers = 10, endpointsPerController = 10): DepGraph {
  const g: DepGraph = {
    schemaVersion: 1, workspace: 'synthetic', generatedAt: '2026-10-02T00:00:00Z',
    repos: [], projects: [], packages: [], endpoints: [], clientMethods: [], callSites: [], models: [], edges: [], diagnostics: [],
  };
  const name = (r: number) => `svc${String(r).padStart(2, '0')}`;
  const loc = (path: string, line = 1) => ({ path, line });
  for (let r = 0; r < repos; r++) {
    const n = name(r), api = `proj:${n}/${n}.Api`, client = `proj:${n}/${n}.Client`, pkg = `pkg:${n}.Client`;
    g.repos.push({ id: `repo:${n}`, name: n, path: n, dirty: false });
    g.projects.push({ id: api, repo: n, name: `${n}.Api`, path: `${n}/src/${n}.Api/${n}.Api.csproj`, kind: 'api', isPackable: false });
    g.projects.push({ id: client, repo: n, name: `${n}.Client`, path: `${n}/src/${n}.Client/${n}.Client.csproj`, kind: 'client', packageId: `${n}.Client`, version: '1.0.0', isPackable: true });
    g.packages.push({ id: pkg, packageId: `${n}.Client`, producerProjectIds: [client] });
    g.edges.push({ kind: 'produces', from: client, to: pkg, confidence: 'certain', version: '1.0.0' });
    for (let c = 0; c < controllers; c++) {
      const dto = `model:${n}.Client:${n}.Client.R${c}Dto`;
      g.models.push({ id: dto, repo: n, projectId: client, fullName: `${n}.Client.R${c}Dto`, kind: 'record',
        fields: [{ name: 'Id', typeName: 'Guid', nullable: false, collection: false }, { name: 'Name', typeName: 'string', nullable: false, collection: false }] });
      for (let e = 0; e < endpointsPerController; e++) {
        const ep = `ep:${n}:GET:/api/r${c}/e${e}/{id}`, cm = `cm:${n}.Client:R${c}Client.E${e}Async`;
        g.endpoints.push({ id: ep, repo: n, projectId: api, verb: 'GET', route: `/api/r${c}/e${e}/{id}`, normalizedRoute: `api/r${c}/e${e}/{}`,
          handler: `R${c}Controller.E${e}`, parameters: [{ name: 'id', source: 'route', typeName: 'Guid', required: true }],
          responses: [{ statusCode: 200, typeName: `Task<ActionResult<R${c}Dto>>` }], location: loc(`${n}/src/${n}.Api/Controllers/R${c}Controller.cs`, e + 5) });
        g.clientMethods.push({ id: cm, repo: n, projectId: client, typeName: `R${c}Client`, methodName: `E${e}Async`, signature: `Task<R${c}Dto?> E${e}Async(Guid id)`,
          verb: 'GET', route: `api/r${c}/e${e}/{id}`, normalizedRoute: `api/r${c}/e${e}/{}`, strategy: 'httpClient', confidence: 'high', location: loc(`${n}/src/${n}.Client/R${c}Client.cs`, e + 4) });
        g.edges.push({ kind: 'targets', from: cm, to: ep, confidence: 'high' }, { kind: 'returns', from: ep, to: dto, confidence: 'high', statusCode: 200 });
      }
    }
    const deps = [1, 2, 3].map(k => name((r + k) % repos));
    deps.forEach((d, i) => {
      const cs = `cs:${n}/${n}.Api:Consumer.Run:${i + 6}`;
      g.callSites.push({ id: cs, repo: n, projectId: api, containingMember: 'Consumer.Run', confidence: 'medium', location: loc(`${n}/src/${n}.Api/Consumer.cs`, i + 6) });
      const edges: Edge[] = [
        { kind: 'references', from: api, to: `pkg:${d}.Client`, confidence: 'certain', version: '1.0.0' },
        { kind: 'invokes', from: cs, to: `cm:${d}.Client:R0Client.E0Async`, confidence: 'medium' },
        { kind: 'dependsOn', from: `repo:${n}`, to: `repo:${d}`, confidence: 'certain', viaPackages: [`${d}.Client`], callCount: 1 },
      ];
      g.edges.push(...edges);
    });
  }
  return g;
}
```

`web/test/synthetic.test.ts`:

```ts
import { expect, test } from 'vitest';
import { GraphIndex } from '../src/graph/graphIndex';
import { buildView } from '../src/graph/views';
import { makeSyntheticGraph } from './util/synthetic';

test('synthetic graph has the SyntheticWorkspace shape and stays drawable', () => {
  const g = makeSyntheticGraph();
  expect([g.repos.length, g.endpoints.length, g.edges.filter(e => e.kind === 'dependsOn').length]).toEqual([50, 5000, 150]);
  const ix = new GraphIndex(g);
  expect(buildView(ix, 'repo', null).nodes).toHaveLength(50);
  const endpointDepth = buildView(ix, 'endpoint', 'repo:svc00'); // 100 endpoints + 100 client methods + 3 call sites
  expect(endpointDepth.nodes.length).toBeLessThanOrEqual(300);
  expect(buildView(ix, 'endpoint', 'pkg:svc00.Client').depth).toBe('endpoint');
});
```

`web/playwright.config.ts`:

```ts
import { defineConfig, devices } from '@playwright/test';

export default defineConfig({
  testDir: 'e2e',
  globalSetup: './e2e/global-setup.ts',
  timeout: 30_000,
  expect: { toHaveScreenshot: { maxDiffPixelRatio: 0.01, animations: 'disabled' } },
  use: { ...devices['Desktop Chrome'], viewport: { width: 1440, height: 900 } },
  projects: [{ name: 'chromium' }],
});
```

`web/e2e/global-setup.ts`:

```ts
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { buildExportHtml } from '../src/app/exportHtml';
import { fixtureJson } from '../test/util/fixture';
import { makeSyntheticGraph } from '../test/util/synthetic';

export const outDir = fileURLToPath(new URL('./.out/', import.meta.url));

/** Turns the built bundle into two exports: the fixture (screenshots) and the 50-repo synthetic graph (perf). */
export default function globalSetup() {
  const bundle = readFileSync(fileURLToPath(new URL('../dist/index.html', import.meta.url)), 'utf8');
  mkdirSync(outDir, { recursive: true });
  writeFileSync(join(outDir, 'fixture.html'), buildExportHtml(bundle, fixtureJson().replace(/^﻿/, ''), null));
  writeFileSync(join(outDir, 'perf.html'), buildExportHtml(bundle, JSON.stringify(makeSyntheticGraph()), null));
}
```

Append to `.gitignore`:

```
web/e2e/.out/
web/test-results/
web/playwright-report/
```

Run: `cd web && npx vitest run test/synthetic.test.ts`
Expected: PASS.

- [ ] **Step 2: Write the screenshot and perf specs (failing: no baselines yet)**

`web/e2e/screens.spec.ts`:

```ts
import { expect, test, type Page } from '@playwright/test';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';
import { outDir } from './global-setup';

const url = (hash = '') => pathToFileURL(join(outDir, 'fixture.html')).href + hash;
const ready = (page: Page) => page.locator('[data-ready="true"]').waitFor();

for (const theme of ['light', 'dark'] as const) {
  test.describe(theme, () => {
    test.use({ colorScheme: theme });

    test('repo depth', async ({ page }) => {
      await page.goto(url());
      await ready(page);
      await expect(page).toHaveScreenshot(`repo-${theme}.png`);
    });

    test('project depth', async ({ page }) => {
      await page.goto(url('#/repo:orders'));
      await ready(page);
      await page.getByRole('button', { name: 'Project', exact: true }).click();
      await ready(page);
      await expect(page).toHaveScreenshot(`project-${theme}.png`);
    });

    test('endpoint depth with contract', async ({ page }) => {
      await page.goto(url('#/ep:orders:GET:/api/orders/%7Bid%7D'));
      await ready(page);
      await expect(page).toHaveScreenshot(`endpoint-${theme}.png`);
    });

    test('impact view', async ({ page }) => {
      await page.goto(url('#/impact/OrderDto.Lines'));
      await ready(page);
      await expect(page.getByRole('heading', { name: 'Changing OrderDto.Lines affects 2 other repos' })).toBeVisible();
      await expect(page).toHaveScreenshot(`impact-${theme}.png`);
    });

    test('collapsed sidebar', async ({ page }) => {
      await page.goto(url());
      await ready(page);
      await page.getByRole('button', { name: 'Collapse sidebar' }).click();
      await expect(page).toHaveScreenshot(`collapsed-${theme}.png`);
    });
  });
}

test('phone width', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto(url());
  await ready(page);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
  await expect(page).toHaveScreenshot('phone.png', { fullPage: true });
});

test('the export works offline: no network requests at all', async ({ page }) => {
  const requests: string[] = [];
  page.on('request', r => { if (!r.url().startsWith('file:') && !r.url().startsWith('data:') && !r.url().startsWith('blob:')) requests.push(r.url()); });
  await page.goto(url());
  await ready(page);
  expect(requests).toEqual([]);
});
```

`web/e2e/perf.spec.ts`:

```ts
import { expect, test } from '@playwright/test';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';
import { outDir } from './global-setup';

test('50 repos / 5,000 endpoints: repo view ready in under 1 s, no long main-thread task over 200 ms', async ({ page }) => {
  await page.addInitScript(() => {
    const w = window as unknown as { __long: number[] };
    w.__long = [];
    new PerformanceObserver(list => { for (const e of list.getEntries()) w.__long.push(e.duration); }).observe({ type: 'longtask', buffered: true });
  });
  await page.goto(pathToFileURL(join(outDir, 'perf.html')).href);
  await page.locator('[data-ready="true"]').waitFor();
  const readyAt = await page.evaluate(() => performance.getEntriesByName('depenk-ready')[0]?.startTime ?? Infinity);
  const longest = await page.evaluate(() => Math.max(0, ...(window as unknown as { __long: number[] }).__long));
  console.log(`ready at ${Math.round(readyAt)} ms, longest task ${Math.round(longest)} ms`);
  expect(readyAt).toBeLessThan(1000);
  expect(longest).toBeLessThan(200);
});
```

Run: `cd web && npm run build && npx playwright install chromium && npx playwright test`
Expected: the screenshot tests FAIL with "A snapshot doesn't exist … writing actual". The offline and perf tests PASS. If perf fails, profile it (Performance panel) and fix the cause before going on, e.g. by memoising `GraphIndex` construction or deferring non-visible inspector work. Don't loosen the budget.

- [ ] **Step 3: Create and review the baselines**

Run: `cd web && npx playwright test --update-snapshots`, then `npx playwright test`
Expected: PASS. Open every PNG in `web/e2e/screens.spec.ts-snapshots/`, check them against the mockup (`docs/superpowers/specs/mockups/servicemap-direction.html`) in both themes, and **show them to the user** as the after-screenshots for the plan.

- [ ] **Step 4: CI**

`.github/workflows/ci.yml`:

```yaml
name: ci
on:
  push:
    branches: [master]
  pull_request:

jobs:
  web:
    runs-on: windows-latest   # screenshot baselines are recorded on Windows
    defaults:
      run:
        working-directory: web
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-node@v4
        with:
          node-version: 22
          cache: npm
          cache-dependency-path: web/package-lock.json
      - run: npm ci
      - run: npm test
      - run: npm run build
      - run: npx playwright install chromium
      - run: npx playwright test
      - if: failure()
        uses: actions/upload-artifact@v4
        with:
          name: playwright-report
          path: web/test-results/

  dotnet:
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-node@v4
        with:
          node-version: 22
          cache: npm
          cache-dependency-path: web/package-lock.json
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: 9.0.x
      - run: dotnet build Depenk.sln   # builds web/ through the BuildWeb target
      - run: dotnet test Depenk.sln --no-build
```

- [ ] **Step 5: Full verification**

Run, from the repo root:
- `cd web && npm test && npm run build && npx playwright test && cd ..`
- `dotnet build Depenk.sln` (0 warnings)
- `dotnet test tests/Depenk.Tests`

Expected: everything passes.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "test(web): Playwright screenshots in both themes, offline check, 50-repo perf budget; CI workflow

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

## Spec Coverage (Plan 3)

| Spec section | Task(s) |
|---|---|
| §1 scope: UI, export, serve, two MCP tools, TS conformance runner | 3–15 |
| §2 architecture: `web/`, `Depenk.Server`, GraphStore move, one bundle with two modes, queries in the browser | 1, 3, 4, 11, 12 |
| §3 visual system: tokens, light/dark, fonts bundled, repo palette, canvas, marks, edges | 5, 7, 8 |
| §4.1 chrome: top bar, collapsible sidebar, inspector tabs | 7, 9 |
| §4.2 depths and drill-in | 5, 6, 8 |
| §4.3 interactions: hover, click, double-click, Backspace, Esc, Ctrl+K, deep links, drag, keyboard, export view, scale, reduced motion | 5, 7, 8, 10, 11 |
| §4.4 impact mode | 10 |
| §5.1 export (+ `export_diagram`) | 12, 14 |
| §5.2 serve: routes, ETag, SSE, Host guard, watcher, live refresh | 11, 13 |
| §5.3 `open_diagram` | 14 |
| §5.4 graph loading: schema check, dangling edges, reconnect banner | 3, 11 |
| §6 error handling table | 3, 7, 10, 12, 13, 14 |
| §7 testing: Vitest, conformance, components, Playwright (both themes), perf, .NET export/server/MCP | 2–15 |
| §8 build: package.json, BuildWeb/EmbedWeb, CI, version 0.3.0, README, skill | 3, 12, 14, 15 |

## Execution Notes

- **Task order:** Tasks 3–11 are front-end only and can be reviewed in the browser as they land. Tasks 12–14 are .NET. Task 15 needs everything else.
- **Screenshots:** every UI task (7–11) starts with a **before** screenshot and ends with an **after** screenshot shown to the user (the user's global instruction). Use the inline Puppeteer script from Task 7, Step 0, kept in the scratchpad, not the repo. Until Task 11 lands, make an export-like page by replacing the graph placeholder in `web/dist/index.html` with the fixture JSON, wrapped in the same `<script>` tag.
- **Fast suites:** `cd web && npx vitest run` and `dotnet test tests/Depenk.Tests --filter "Category!=Perf"`.
- **Library APIs:** if React Flow 12.12, elkjs 0.12, Vite 8 or Vitest 5 differ from the code here (option names, type names), keep the behaviour and the tests' intent, and record the change in the task report.
- **Stdout:** never add `console.log` to `web/src` (CI noise), and never write to stdout from `Depenk.Server` (`open_diagram` runs inside `depenk mcp`).
