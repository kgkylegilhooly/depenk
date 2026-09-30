# depenk — Cross-Repo C# Dependency Explorer · Design Spec

- **Date:** 2026-09-30
- **Status:** Draft, awaiting review
- **Mockups:** `docs/superpowers/specs/mockups/` (chosen: Observatory style · Command Center layout · Endpoint view A)

## 1. Purpose

depenk is an open-source tool that finds how **services in separate C# repositories depend on each other**. It shows those links as a polished, interactive diagram and makes the same information available to AI agents through MCP.

It is built around a common .NET pattern: **service A calls service B by referencing B's API client NuGet package.** That client wraps HTTP calls to B's controllers. So a `PackageReference` to a client package *is* a service-to-service call. depenk reads that link and follows it down: consumer call site → client method → HTTP endpoint → controller action → request and response models → fields.

### Goals
1. **Impact analysis for agents (MCP):** answer "what breaks if I change this package, endpoint, model or field?" while an agent is working.
2. **Architecture documentation:** a navigable map of the whole system, shareable as one HTML file or explored live.
3. **Generic and open:** nothing tied to one company's conventions. Unusual patterns are handled with configuration, not code changes.

### Success criteria
- Pointed at a folder of local clones, `depenk scan` builds a correct repo-to-repo dependency graph **without building or restoring any repo**.
- For supported client styles, client methods are linked to controller endpoints, and endpoints show their request and response model trees.
- An agent can answer impact questions through MCP without reading other repos' source directly.
- The exported HTML opens offline, looks like the approved Observatory mockups, and stays responsive on a 50-repo, 5,000-endpoint workspace.

### Out of scope for v1
- Messaging or event-bus links, shared databases, gRPC.
- Finding HTTP calls that bypass a client package (raw URL matching across repos).
- Version-aware contracts (scanning old git tags to see a model as it was in an older package version).
- Tracking which fields each consumer reads; following requests across services through handler internals (`trace_request_flow`, `find_path`).
- `plan_package_upgrade`; full DI snippet generation in `how_to_call`.
- Light theme; fetching repos from GitHub or Azure DevOps.

## 2. Inputs and configuration

- **Workspace:** a folder of local git clones (e.g. `C:\code\repos`), or an explicit list of repo paths in config. No cloning, no git host API calls.
- **`depenk.yml`** (optional, at the workspace root, validated against a published JSON Schema):

```yaml
repos:                      # optional; default = every git repo directly under the workspace
  include: ["*"]
  exclude: ["legacy-*"]
projects:
  kindOverrides:            # force classification when the heuristics are wrong
    "Orders.Contracts": Client
  ignore: ["*.Benchmarks"]
packages:
  producers:                # settle ambiguous or unusual PackageId → repo mappings
    "Acme.Orders.Client": orders
httpWrappers:               # teach the scanner about in-house HTTP wrappers
  - type: "*.IApiHttpClient"            # glob on the declared type name
    methods: { "Get*": GET, "Post*": POST, "Put*": PUT, "Delete*": DELETE, "Patch*": PATCH }
    routeArgument: 0
routes:
  prefixes:                 # base paths a client prepends, if not visible in code
    "Orders.Client": "/api"
```

- **Cache:** `<workspace>/.depenk/graph.json` (the current graph) and `<workspace>/.depenk/snapshots/<utc-timestamp>-<shortsha>.json` (history). Per-file content hashes are stored for incremental rescans.

## 3. Architecture

A .NET global tool written in C# that uses **syntax-only Roslyn** (it parses code, it never compiles). Repos never need to restore or build.

```
depenk (CLI: scan | export | serve | mcp | query)
 ├─ Depenk.Core       graph model, JSON schema (schemaVersion: 1), ids, diagnostics
 ├─ Depenk.Scanning   repo discovery, csproj / Directory.Build.props / Directory.Packages.props parsing, git info
 ├─ Depenk.Analysis   Roslyn: endpoint finders, route strategies, model extractor, call-site finder, linker
 ├─ Depenk.Query      read-only traversal + impact + diff over the graph
 ├─ Depenk.Mcp        MCP server (official ModelContextProtocol C# SDK, stdio)
 ├─ Depenk.Server     Kestrel host for `serve` and `open_diagram` (HTTP API + SSE)
 └─ web/              React + TS UI → single-file bundle embedded as a resource
```

Each unit has one job and talks to the others only through the graph model or `Depenk.Query`. The analyzers sit behind interfaces (`IEndpointFinder`, `IRouteStrategy`, `ICallSiteFinder`), so a future semantic `--deep` mode (MSBuildWorkspace) or new client styles can be added without restructuring.

### 3.1 Scan pipeline
1. **Discover** repos, then projects (`*.csproj`). Record the repo's git HEAD and dirty state.
2. **Parse projects:** SDK, `PackageId`/`AssemblyName`, `IsPackable`, `PackageReference`s. Versions are resolved through CPM and `Directory.Build.props`, with simple `$(Prop)` substitution; anything unresolvable is recorded as `unresolved($(Prop))` plus a diagnostic.
3. **Classify projects:** `Api` (Web SDK, or contains controllers or minimal APIs), `Client` (packable and contains HTTP client patterns, or named `*.Client`/`*.Contracts`), `Library`, `Test` (references a test SDK), `Other`. Config overrides always win.
4. **Link packages to producers:** match `PackageId` to the producing project, in any repo. A package with no producer in the workspace is `external` and hidden by default.
5. **Find endpoints (per `Api` project):**
   - controllers: `[ApiController]` / `ControllerBase`, `[Route]` on class and method with `[controller]`/`[action]` tokens replaced, `[Http*]` attributes
   - minimal APIs: `Map{Get,Post,Put,Delete,Patch}`, including `MapGroup` prefixes
6. **Find client methods (per `Client` project):** strategies, run in priority order:
   - **Refit:** `[Get("/…")]`-style attributes on interface methods. Confidence: high.
   - **Generated (NSwag/Kiota):** route literals plus HTTP method in generated code. Confidence: high.
   - **Configured wrappers:** calls matching `httpWrappers` from config. Confidence: high.
   - **Generic heuristic:** calls inside a public client method that have a route-like string or interpolated-string argument. The verb comes from the called method's name (`GetAsync`, `PostAsJsonAsync`…) or from `HttpMethod.X`. Confidence: medium.

   Interface methods are mapped to their implementing class by name within the project.
7. **Link client methods to endpoints (same repo only):** normalize routes on both sides (placeholders `{id}`, `{id:int}` and `{orderId}` from interpolation all become `{}`, case-insensitive, slashes trimmed, configured prefixes applied), then match on verb plus normalized route.
   - Exactly one match → link with the strategy's confidence.
   - Several matches → keep all, marked low confidence, with a diagnostic.
   - No match → `unresolved` diagnostic.
8. **Extract models:** start from each endpoint's parameters (binding source taken from `[FromBody]`/`[FromQuery]`/`[FromRoute]`/`[FromHeader]` or inferred from route placeholders) and its return types:
   - unwrap `Task<>`, `ValueTask<>`, `ActionResult<>`, `IActionResult` with `[ProducesResponseType]`, `Results<…>`/`TypedResults`, `IEnumerable<>`, `List<>`, arrays and `Nullable<>`
   - resolve type names to class, record, struct or enum declarations anywhere in the workspace, preferring the same project, then its referenced projects and packages, then global name matching
   - follow fields and properties recursively, up to a depth limit
   - types that can't be resolved become `opaque` models
9. **Find call sites (per consumer project):** invocations whose receiver is a local, field, property or constructor parameter declared with a known client type (interface or class). Confidence: medium.
10. **Derive** `Repo → Repo` `dependsOn` edges with counts. **Compute diagnostics.** Write `graph.json` and a snapshot.

**Incremental rescan:** only files whose content hash changed are re-parsed, and linking (steps 7–10) is recomputed over the whole graph, which is cheap.

## 4. Graph model (`Depenk.Core`)

Every node has a stable, readable `id`, a `repo`, and a source `location` (`path`, `line`) where one applies.

| Node | ID example | Key fields |
|---|---|---|
| `Repo` | `repo:orders` | name, path, headSha, dirty |
| `Project` | `proj:orders/Orders.Api` | kind, sdk, packageId? |
| `Package` | `pkg:Orders.Client` | producerProjectId? (null = external) |
| `Endpoint` | `ep:orders:GET:/api/orders/{id}` | verb, route (display), normalizedRoute, handler, parameters[{name, source, typeRef, required, default?}] |
| `ClientMethod` | `cm:Orders.Client:IOrdersClient.GetOrderAsync` | verb?, route?, strategy, confidence, signature |
| `CallSite` | `cs:billing/Billing.Api:InvoiceBuilder.Build:118` | containingMember, confidence |
| `Model` | `model:Orders.Client:Acme.Orders.OrderDto` | kind (class, record, struct, enum, opaque), fields[{name, typeRef, nullable, collection}], enumValues? |

| Edge | From → To | Attributes |
|---|---|---|
| `references` | Project → Package | version (or `unresolved(...)`) |
| `produces` | Project → Package | version |
| `targets` | ClientMethod → Endpoint | strategy, confidence |
| `invokes` | CallSite → ClientMethod | confidence |
| `accepts` | Endpoint → Model | source (body, query, route, header) |
| `returns` | Endpoint → Model | statusCode |
| `fieldOf` | Model → Model | fieldName |
| `dependsOn` | Repo → Repo | derived; viaPackages[], callCount |

**Confidence levels:** `certain` (package links), `high`, `medium`, `low` (ambiguous).

**Diagnostics** (`{kind, severity, nodeIds[], message}`): `versionDrift`, `unresolvedClientMethod`, `ambiguousRoute`, `ambiguousProducer`, `unresolvedVersion`, `cycle` (repo-level), `unusedEndpoint`, `unusedModel`, `unusedClientMethod`, `parseError`.

## 5. Query layer (`Depenk.Query`)

Read-only operations over a loaded graph. They power MCP, the `serve` HTTP API and `depenk query`:
- lookup and search, including fuzzy "did you mean"
- neighbors
- `trace(node, up|down|both, depth)` over package, target, invoke and model edges
- `impact(target)`: target is a package, endpoint, model or `model.field`; returns affected repos, projects, call sites and endpoints, with the lowest confidence found along each path
- `diff(snapshotA, snapshotB)`: diff by node ID covering nodes, edges, fields and routes
- `contractChanges(repo, base)`: rescan that repo's working tree, then compare its endpoints and models with the cached graph and classify each change as breaking or non-breaking:
  - **breaking:** a removed or renamed field or endpoint, a changed type, a changed verb or route, a newly required parameter, a removed enum value
  - **non-breaking:** additions

**Parity with the UI:** the static HTML UI implements the traversal subset (neighbors, trace, search, filter) in TypeScript. The shared **query conformance suite** (`tests/query-cases/*.json`: graph, query, expected result) runs against both the C# and TypeScript implementations.

## 6. MCP server (`Depenk.Mcp`)

- **Transport:** stdio. The MCP client starts `depenk mcp --workspace <path>` for each session. No Docker, ports or daemon.
- **Startup:** loads the cached graph immediately, then checks git HEAD and file hashes in the background and rescans incrementally. While a rescan is running, answers include `stale: true`. If there's no cache yet, the first call triggers a full scan.
- **Output:** compact JSON plus a one-line `summary`. Lists are capped (default 50) and return `truncated: true` along with narrowing hints.
- **Errors:** structured `{code, message, hint, suggestions[]}`; never raw exceptions.

| Group | Tool / resource | Purpose |
|---|---|---|
| Browse | `list_repos` | Repos and repo-to-repo links with counts |
| | `get_repo(repo)` | Projects, packages published and consumed, dependents, endpoint count |
| | `find_endpoints(query, repo?, verb?)` | Search by route or handler |
| | `get_endpoint(id \| "VERB /route")` | Contract (parameters, request and response models), handler location, client methods, callers |
| | `get_model(name, depth=2)` | Field tree, marking types from other repos |
| | `find_model_usages(model)` | Endpoints and repos the model passes through |
| | `get_source(nodeId, context=10)` | Code snippet for any node that has a source location |
| | resource `depenk://overview` | Markdown summary: repos, connections, hotspots, diagnostics count |
| Analyze | `trace(node, direction, depth)` | Paths upstream or downstream |
| | `impact_of_change(target)` | Affected repos, projects and call sites for a package, endpoint, model or field |
| | `check_contract_changes(repo, base="HEAD")` | Breaking and non-breaking contract changes in the working tree, and the consumers hit |
| | `get_diagnostics(kind?)` | Drift, unresolved or ambiguous links, cycles, unused items |
| | `how_to_call(endpoint)` | (light) Package ID and latest version, client interface and method signature, request and response models |
| History | `compare_snapshots(a?, b?)` | Architecture diff; defaults to the last two snapshots |
| Operate | `rescan(repos?)` | Incremental refresh |
| | `export_diagram(focus?, path?)` | Write single-file HTML and return its path |
| | `open_diagram(focus?)` | Start the in-process UI on localhost and return a deep-link URL (lives as long as the MCP session) |

## 7. Distribution

- **.NET global tool:** `dotnet tool install -g depenk`, then `claude mcp add depenk -- depenk mcp --workspace C:\code\repos`.
- **`dnx` (.NET 10):** `dnx depenk --yes -- mcp --workspace …`. Also packaged with NuGet's MCP server package type (`.mcp/server.json`) so it can be discovered on nuget.org.
- **Claude Code plugin:** bundles the MCP config plus a **skill** (`skills/depenk/SKILL.md`) that tells agents when to use it:
  - before editing a controller or DTO → `check_contract_changes` / `impact_of_change`
  - when integrating with another service → `how_to_call`
  - at the start of a session → `depenk://overview`
- **CLI for agents without MCP:** `depenk query <tool> --json '{…}'`, which reuses the MCP tool handlers.
- **Optional container image** for CI, e.g. nightly `scan` + `export` to publish the diagram. Not needed for local use.
- **License:** MIT.

## 8. Frontend (`web/`)

**Approved direction:** *Observatory* (dark navy canvas, glowing gradient edges, animated flow particles, monospace type, one accent color per repo) in the *Command Center* layout, with *Endpoint view A* (call chain on the canvas, contract tree in the inspector).

### 8.1 Stack
- React + TypeScript + Vite.
- `@xyflow/react` (React Flow) for the canvas: nodes are HTML components, and it provides minimap, zoom and pan.
- `elkjs` for layout: layered, left to right, with compound nodes for project depth.
- Design tokens as CSS custom properties. JetBrains Mono and Inter are **bundled**, not loaded from a CDN, so exports work offline.
- `vite-plugin-singlefile` produces one HTML file. `export` puts the graph into `<script type="application/json" id="depenk-graph">`. The built bundle is embedded in the .NET tool.
- Repo colors are assigned deterministically (a stable hash into a curated palette).

### 8.2 Layout: Command Center
- **Top bar:** logo, breadcrumb (System / repo / project / endpoint), Ctrl+K search.
- **Left sidebar:**
  - depth switch: Repo · Project · Endpoint
  - tabs for Repos / Endpoints / Models / Packages
  - filters: node kinds, tests, third-party, minimum confidence
  - highlight direction: Up · Down · Both
  - diagnostics list
- **Canvas:** the graph.
- **Right inspector:** tabs depend on what's selected:
  - Repo: Overview · Publishes · Called by · Endpoints
  - Endpoint: Contract · Callers · Source
  - Model: Fields · Used by
  - Package: Consumers · Versions

### 8.3 Views
- **Repo depth:** repos as nodes; edges are `dependsOn`, labeled with the client packages involved.
- **Project depth:** repos as containers holding their projects; edges go from the consuming project to the client package it uses.
- **Endpoint depth:** limited to the selection. Call sites → client method → endpoint, laid out in three columns. The contract appears in the inspector as an expandable model tree; types from other repos are tagged (`↗ customers`) and clicking one jumps there.

### 8.4 Interactions
- **Ctrl+K palette:** fuzzy search over all node types; Enter focuses the result.
- **Hover:** highlights neighbors and dims the rest.
- **Clicking and focusing:** click selects, double-click drills in, Backspace or the breadcrumb goes back up.
- **Confidence styling:** low and medium confidence edges are dashed; diagnostics show as badges (amber = drift, red = broken or unresolved).
- **Snapshot compare overlay:** added items glow green, removed items appear as red ghosts.
- **Deep links:** `#/<nodeId>` URLs. `open_diagram` and shared links use them.
- **Export view:** PNG or SVG of the current view.
- **Accessibility:** `prefers-reduced-motion` turns off the flow animation; full keyboard navigation; WCAG AA contrast.
- **Scale:** above about 300 visible nodes the view automatically groups nodes by repo or project.
- **Serve mode:** the same bundle loads `/api/graph` and listens on `/api/events` (SSE); after a rescan, changes animate in.

## 9. Error handling

- A partial result beats a failed scan. Per-file or per-project failures become `parseError` diagnostics, and the scan continues. Non-git repos work, but snapshots and `check_contract_changes` are skipped for them.
- Ambiguity is always shown and never silently settled. Config can pin the answer.
- Config errors report file, line and a message, based on JSON Schema validation.
- **Security:**
  - depenk only reads repos.
  - `serve` and `open_diagram` bind to `127.0.0.1`.
  - Exports contain paths and line numbers but **no source code** unless `--include-source` is passed.
  - MCP `get_source` reads only from files inside the configured workspace.

## 10. Testing

- **Fixture workspace** (`tests/fixtures/workspace/`), with fake repos covering:
  - client styles: Refit, NSwag-style generated, hand-written `HttpClient`, configured wrapper
  - controllers and minimal APIs (including `MapGroup`)
  - central package management and `Directory.Build.props` properties
  - models shared across repos, a repo cycle, version drift, an ambiguous route
- **Golden test:** scan the fixture and compare `graph.json` with a snapshot (Verify).
- **Unit tests** (xUnit) for each finder, strategy and model extractor, using inline C# sources; table-driven route-normalizer tests.
- **Query conformance suite** run by both the C# query layer and the TypeScript UI.
- **MCP integration tests:** host the server in-process and call every tool through the SDK client.
- **UI:** Vitest component tests; Playwright screenshot tests of each view against the fixture graph.
- **Performance smoke test:** a synthetic generator creates 50 repos with 5,000 endpoints. Budgets: full scan < 60 s, incremental rescan of one changed file < 2 s, initial render of the repo view < 1 s.

## 11. Suggested build order

1. Core model, scanning, package-level graph, then `scan` and a minimal `export`. This is useful on its own.
2. Endpoint finders, route strategies, linking, model extraction, call sites.
3. Query layer and MCP server with the full v1 tool set; skill and plugin packaging.
4. The polished frontend (all three depths, inspector, palette, overlays) and `serve`/`open_diagram`.
5. Snapshots, `compare_snapshots`, `check_contract_changes`.
