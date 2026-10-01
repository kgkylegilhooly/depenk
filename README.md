# depenk

**See how your C# services actually depend on each other — across repositories.**

depenk scans a folder of local C# repo clones and builds a cross-repo dependency graph: which service calls which, through which API client package, hitting which controller endpoint, carrying which request and response models. It reads `.csproj` files and source as plain syntax (Roslyn), so **nothing is ever restored or built** — a broken SDK pin in one repo never blocks the scan.

> **Status: early (v0.1).** The scanning engine is done and tested. The MCP server, interactive diagram and change-history features are on the [roadmap](#roadmap).

## The idea

A very common .NET setup: service **B** publishes an API client NuGet package (`Orders.Client`) that wraps HTTP calls to its own controllers. Service **A** calls B by referencing that package.

```
 billing repo                               orders repo
 InvoiceBuilder.BuildAsync()                ├─ Orders.Client  ──published as NuGet──┐
   └─ IOrdersClient.GetOrderAsync(id) ──────┤    IOrdersClient.GetOrderAsync        │
                                            │      └─ GET api/orders/{id}           │
 Billing.Api.csproj                         └─ Orders.Api                           │
   <PackageReference Include="Orders.Client"/>   OrdersController.Get(id) → OrderDto│
        ▲───────────────────────────────────────────────────────────────────────────┘
```

So a `PackageReference` to a client package *is* a service-to-service call. depenk follows it all the way down:

**call site → client method → HTTP endpoint → controller action → request/response models → fields**

## What it finds

| Layer | Example | How |
|---|---|---|
| Repo → repo | billing depends on orders | `PackageReference` matched to the project that produces that `PackageId` |
| Client method → endpoint | `IOrdersClient.GetOrderAsync` → `GET /api/orders/{id}` | client-package strategies (below), matched to controllers / minimal APIs **in the same repo** |
| Caller → client method | `InvoiceBuilder.BuildAsync` calls `GetOrderAsync` | calls on variables, fields and constructor parameters typed as the client |
| Endpoint → models | returns `OrderDto { Customer: CustomerDto, Lines: List<OrderLineDto> }` | parameters / return types unwrapped and resolved across projects and repos |

Every link carries a **confidence** (`certain` · `high` · `medium` · `low`). Anything ambiguous is kept with all its candidates, marked `low`, and reported — never silently guessed.

### Reading client packages

Strategies run in order; the first match wins:

1. **Refit** — `[Get("/api/orders/{id}")]` attributes. *(high)*
2. **Generated clients** — NSwag (`urlBuilder_` / `// Operation Path:` comments) and Kiota (`{+baseurl}` request builders). *(high)*
3. **Configured wrappers** — your in-house HTTP wrapper, described once in `depenk.yml`. *(high)*
4. **Generic HttpClient heuristic** — calls like `http.GetFromJsonAsync<T>($"api/orders/{id}")` or `new HttpRequestMessage(HttpMethod.Put, url)`. *(medium)*

Endpoints are found from `[ApiController]`/`ControllerBase` controllers (including `[Route]` inherited from base controllers) and minimal APIs (`MapGet`/`MapPost`/… with `MapGroup` prefixes).

### Diagnostics

The graph includes findings you can act on:

- `versionDrift` — consumers behind the producer's package version
- `cycle` — circular repo dependencies
- `unusedEndpoint`, `unusedClientMethod`, `unusedModel` — dead surface area
- `ambiguousRoute`, `ambiguousProducer`, `ambiguousModel`, `ambiguousCallSite` — places depenk couldn't decide, with every candidate listed
- `unresolvedClientMethod`, `unresolvedVersion`, `parseError`, `duplicateProjectName`, `duplicateRepoName` — gaps worth a look

## Getting started

Requires the [.NET 9 SDK](https://dotnet.microsoft.com/download). depenk isn't on NuGet yet, so build it from source:

```bash
git clone https://github.com/kgkylegilhooly/depenk
cd depenk
dotnet pack src/depenk -o ./nupkg
dotnet tool install --global depenk --add-source ./nupkg
```

Then point it at the folder that holds your clones:

```bash
depenk scan --workspace ~/code/repos
```

```
Scanned 6 repos, 18 projects: 82 endpoints, 8 client methods, 5 call sites, 42 models in 1.1s -> .depenk/graph.json (2 warnings)
```

Every direct child folder that is a git repo is scanned. Running `scan` again is instant when nothing changed (`Graph is up to date`); use `--force` to rescan anyway.

| Option | Default | |
|---|---|---|
| `--workspace <dir>` | current directory | folder containing the repo clones |
| `--force` | off | rescan even if nothing changed |

Exit codes: `0` success · `1` unexpected error · `2` invalid `depenk.yml` · `3` workspace not found.

Without installing: `dotnet run --project src/depenk -- scan --workspace <dir>`.

## Configuration (`depenk.yml`)

Optional — place it in the workspace root. Everything has sensible defaults.

```yaml
repos:
  paths: []                 # explicit repo folders; when set, child-folder discovery is skipped
  include: ["*"]
  exclude: ["legacy-*"]

projects:
  kindOverrides:            # Api | Client | Library | Test | Other
    Orders.Contracts: Client
  ignore: ["*.Benchmarks"]

packages:
  producers:                # settle which repo produces a package when it's ambiguous
    Acme.Orders.Client: orders

httpWrappers:               # teach depenk your in-house HTTP wrapper
  - type: "*.IApiHttpClient"          # glob on the declared type name
    methods: { "Get*": GET, "Post*": POST, "Put*": PUT, "Delete*": DELETE }
    routeArgument: 0                  # which argument holds the route

routes:
  prefixes:                 # base path a client prepends that isn't visible in code
    Orders.Client: /api
```

## Output

`.depenk/graph.json` (versioned with `schemaVersion: 1`) contains:

- **Nodes** — `repos`, `projects`, `packages`, `endpoints`, `clientMethods`, `callSites`, `models`, each with a stable readable id (`repo:orders`, `ep:orders:GET:/api/orders/{id}`, `cm:Orders.Client:IOrdersClient.GetOrderAsync`, `model:Orders.Client:Acme.Orders.Client.OrderDto`) and a workspace-relative source location
- **Edges** — `references`, `produces`, `targets`, `invokes`, `accepts`, `returns`, `fieldOf`, `dependsOn`, each with a confidence
- **Diagnostics** — `{ kind, severity, nodeIds, message }`

Paths are always workspace-relative with `/` separators, and output is deterministic, so the file diffs cleanly.

## How it works

```
depenk (CLI)
 ├─ Depenk.Core       graph model, ids, JSON
 ├─ Depenk.Scanning   repo discovery, depenk.yml, csproj / Directory.Build.props / central package versions
 └─ Depenk.Analysis   Roslyn syntax analysis: endpoints, client strategies, linking, models, call sites, diagnostics
```

Design principles: **a partial result beats a failed scan** (unreadable files and folders become `parseError` diagnostics), **ambiguity is always visible**, and rescans are incremental (unchanged files are never re-parsed). A synthetic 50-repo, 5,000-endpoint workspace scans in under a second.

Known limitations of syntax-only analysis: MSBuild `Condition`s and `Import`s aren't evaluated, controller actions inherited from base classes aren't picked up, and HTTP calls that bypass a client package aren't detected.

## Roadmap

- [x] **Graph engine** — `depenk scan`, everything above
- [ ] **MCP server + Claude Code skill** — let AI agents ask "what breaks if I change this endpoint/model/field?" (`impact_of_change`, `get_endpoint`, `get_model`, `find_model_usages`, `how_to_call`, …) via a local stdio server — no Docker
- [ ] **Interactive diagram** — dark "Observatory" UI: repo → project → endpoint drill-down, model trees, filters, Ctrl+K search; exported as a single HTML file or served live
- [ ] **History** — snapshots, architecture diffs between scans, and `check_contract_changes` to catch breaking API changes before you commit

The full design is in [`docs/superpowers/specs`](docs/superpowers/specs/2026-09-30-depenk-design.md), with UI mockups in [`mockups/`](docs/superpowers/specs/mockups).

## Development

```bash
dotnet build Depenk.sln                                  # warnings are errors
dotnet test tests/Depenk.Tests --filter "Category!=Perf" # fast suite
dotnet test tests/Depenk.Tests                           # includes the 50-repo perf smoke test
```

The end-to-end tests scan a fixture workspace of five fake repos in `tests/fixtures/workspace` and compare the result to a golden snapshot.

## License

[MIT](LICENSE)
