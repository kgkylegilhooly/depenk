# depenk 3: Interactive Diagram · Design Spec

- **Date:** 2026-10-02
- **Status:** Draft, awaiting review
- **Supersedes:** §8 (Frontend) of [`2026-09-30-depenk-design.md`](2026-09-30-depenk-design.md), plus the parts of §3, §6 and §10 that cover `export`, `serve`, `export_diagram` and `open_diagram`.
- **Mockups:** [`mockups/servicemap-direction.html`](mockups/servicemap-direction.html). It holds four switchable views: A, B, C and D. **Chosen:** layout B (Command Center), with C (endpoint drill-down) and D (impact view) as views inside it.

## 1. Purpose and scope

Plans 1 and 2 built the graph engine and the MCP server. Plan 3 makes the graph **visible**: an interactive map of how the services depend on each other. You can open it offline as one HTML file, or live from `depenk serve`. An agent can also open it through MCP.

### Changes from the original spec

| Topic | Original (§8) | Now |
|---|---|---|
| Visual direction | "Observatory": dark navy, glowing gradient edges, flow particles | **ServiceMap style**: dotted grid, IBM Plex type, teal accent, a colored stripe per repo, flat edges |
| Themes | Dark only (light was out of scope) | **Light and dark**. Follows `prefers-color-scheme`, with a manual toggle |
| Layout | Command Center | Command Center, with a **collapsible** left sidebar |
| Impact | MCP only | Also a diagram view: **impact mode** (§4.4) |

### In scope
- **UI:** the `web/` React app, built into one self-contained `index.html`.
- **CLI:** `depenk export` and `depenk serve`.
- **MCP tools:** `export_diagram` and `open_diagram`. The tool count goes from 12 to 14.
- **New project `Depenk.Server`:** a loopback-only HTTP host with SSE.
- **TypeScript query port:** the TypeScript version of the query layer, with a runner for the shared conformance cases (`tests/query-cases/*.json`).

### Out of scope (Plan 4: History)
- Snapshot compare overlay, `compare_snapshots`, `check_contract_changes`.

### Success criteria
- `depenk export` writes one HTML file that opens offline, with no CDN requests, and is fully interactive. Impact and trace work without a server.
- `depenk serve` shows the live graph and updates within about 2 s after a source file changes.
- The UI matches the chosen mockup in both themes.
- The synthetic 50-repo, 5,000-endpoint workspace renders its repo view in under 1 s. Layout never blocks the main thread.
- The C# and TypeScript query layers pass the same conformance cases.

## 2. Architecture

```
web/                         React 19 + TypeScript + Vite → dist/index.html (single file, fonts inlined)
  src/graph/                 graph.json types, GraphIndex, queries (neighbors, trace, impact, search)
  src/layout/                elkjs in a Web Worker; layout-input builders per depth
  src/map/                   React Flow canvas; node, edge and container components
  src/panels/                Sidebar, Inspector (per-kind tabs), Overview, CommandPalette
  src/app/                   routing (hash deep links), selection/impact state, theme, persistence
  test/                      Vitest unit + component tests, conformance runner, Playwright specs

src/Depenk.Server/           Kestrel host: GET / · GET /api/graph · GET /api/events (SSE)
                             Embeds web/dist/index.html as a resource; DiagramExporter; GraphStore (moved from Depenk.Mcp)
src/depenk/                  + `export` and `serve` commands
src/Depenk.Mcp/              + export_diagram, open_diagram (Depenk.Server runs in-process)
```

**Dependency direction:** `depenk` → `Depenk.Mcp` → `Depenk.Server` → `Depenk.Analysis` / `Depenk.Query`. Both the server and the MCP tools need `GraphStore`, and `open_diagram` needs the server. To avoid a reference cycle, **`GraphStore` and `GraphSnapshot` move from `Depenk.Mcp` into `Depenk.Server`** (namespace `Depenk.Server`). `Depenk.Mcp` then references `Depenk.Server`. The move changes no behavior; existing tests just update their `using` lines.

**One bundle, two modes:**
- **Export:** the graph is inlined as `<script type="application/json" id="depenk-graph">…</script>`.
- **Serve:** the script tag is absent, and the app fetches `/api/graph` and subscribes to `/api/events`.

The app picks the mode by checking for that element.

**Queries run in the browser.** The TypeScript port of `Depenk.Query` covers lookup, search, neighbors, trace and impact. It lets an exported file answer "what breaks if…" with no server. The shared conformance cases keep the two implementations in agreement.

## 3. Visual system

Taken from the user's ServiceMap sample (see the mockup file for the exact tokens).

- **Tokens:** CSS custom properties on `:root`, redefined under `:root[data-theme="dark"]`, and under `prefers-color-scheme: dark` when no theme has been chosen.
  - surface tones: `--bg`, `--surface`, `--surface-2`, `--ink`, `--ink-2`, `--ink-3`, `--line`, `--grid`
  - accent: `--accent` (teal) and `--accent-soft`
  - status colors: `--ok`, `--warn`, `--bad`, each with a `-soft` variant
  - verb colors: `--m-get`, `--m-post`, `--m-put`, `--m-delete`, `--m-patch`, `--m-other`
- **Repo colors:** a curated palette of 8 hues, each with light and dark variants. A repo gets one from a stable hash of its name: FNV-1a modulo the palette size, with linear probing when two repos collide in the same workspace. The color appears as the stripe on repo and project nodes and as dots in lists.
- **Type:**
  - IBM Plex Sans Condensed 600/700 for headings and node names
  - IBM Plex Sans 400/500/600 for body text
  - IBM Plex Mono 400/500 for routes, ids and counts
  - The fonts are bundled as woff2 subsets (OFL), with no CDN.
- **Canvas:** a dotted grid (22 px pitch, 1.1 px dots in `--grid`). There are no shadows on map marks.
- **Marks:**
  - nodes: rounded 10 px, 1.2 px border
  - selection: 2.2 px accent border
  - impact source: an accent-soft fill
  - affected: a `--bad` border
  - faded: opacity .28
- **Edges:**
  - a quadratic curve with an arrowhead
  - width = `min(1.4 + 0.6·calls, 5)`
  - colors: cycle edges `--warn`, impact edges `--bad`, ordinary edges `--ink-3`
  - dashed when confidence is low or medium, or when a package is referenced but never called
  - labels are pills
  - in a two-way pair, the two edges curve to opposite sides and each label sits at t = .3 from its own source

## 4. Views and interactions

### 4.1 Chrome
- **Top bar:**
  - the "depenk" name, plus the scan time
  - a breadcrumb: System / repo / project / endpoint
  - the Ctrl+K search field
  - Export ▾ (PNG, SVG, HTML; the HTML option appears only in serve mode)
  - the theme toggle
  - in impact mode, the pinned target chip
- **Left sidebar (collapsible to a 48 px icon strip; the state is persisted):**
  - **Depth:** Repo · Project · Endpoint
  - **Browse tabs:** Repos · Endpoints · Models · Packages, each a filterable list. Clicking an item selects and focuses it.
  - **Show:** API, Client packages, Libraries, Tests (off by default), Third-party packages (off by default)
  - **Highlight:** Up · Down · Both
  - **Min confidence:** Any · Medium · High
  - **Diagnostics:** grouped by kind with counts. Clicking a group lists its items, and clicking an item focuses it.
- **Right inspector (400 px):**
  - With no selection, it shows the **overview**: a headline ("N links across M repos"), a summary sentence, *Needs attention* (diagnostics, sorted by severity), the repo list and a legend.
  - With a selection, it shows tabs for that kind:
    - Repo: Overview · Publishes · Called by · Endpoints
    - Project: Overview · References · Endpoints
    - Endpoint: Contract · Callers · Location
    - Model: Fields · Used by
    - Package: Consumers · Versions
  - Each inspector has a "What breaks if this changes?" button, which enters impact mode.

### 4.2 Map depths
- **Repo depth** (default):
  - nodes: repos (name, project count, in/out counts, diagnostic badge)
  - edges: `dependsOn`, labeled `<Package> · <callCount>`
- **Project depth:**
  - repos are dashed containers (elkjs compound nodes) holding their projects, each tagged API / CLIENT · NUGET <version> / TEST / LIBRARY
  - edges run from the consuming project to the producing client project, labeled with the referenced version and call count
  - version drift gets an amber edge plus a `drift` badge
- **Endpoint depth:**
  - scoped to the selected endpoint, package or client method
  - three columns: call sites → client methods → endpoints
  - edges are labeled with confidence
  - the inspector shows the contract as an expandable model tree (`get_model` depth 2, expandable further)
  - a field whose type belongs to another repo shows a `↗ <repo>` pill that focuses that model

Drilling in: double-clicking a repo opens Project depth focused on it. Double-clicking a project or endpoint opens Endpoint depth.

### 4.3 Interactions
- **Hover:** highlights neighbors in the current highlight direction and dims everything else.
- **Selection and navigation:**
  - click selects
  - double-click drills in
  - Backspace or the breadcrumb goes back up
  - Esc clears the selection, or leaves impact mode
- **Ctrl+K palette:**
  - fuzzy search over repos, projects, packages, endpoints (`VERB /route` or handler), client methods and models, grouped by kind
  - Enter focuses the result
  - the `impact <target>` prefix enters impact mode
- **Deep links:**
  - `#/<nodeId>` focuses a node, and `#/impact/<target>` enters impact mode
  - an unknown id shows "not found" with up to 5 suggestions
- **Drag:** nodes can be dragged. Positions are saved in localStorage, keyed by workspace and depth, and "Reset layout" clears them. Saved positions override elkjs only for nodes that still exist.
- **Keyboard:**
  - Tab moves through panels
  - arrow keys move between neighboring nodes on the map
  - Enter selects
  - every action has a keyboard path
- **Export view:** PNG and SVG of the current viewport, built from the React Flow viewport.
- **Scale:** if a depth would show more than about 300 nodes, the app shows the next coarser depth instead, with a notice: "Showing repos — 1,240 projects is too many; select a repo to drill in".
- **Reduced motion:** `prefers-reduced-motion` turns off transitions and the fade-in of changed nodes.

### 4.4 Impact mode
- **Entry:** the inspector button, Ctrl+K `impact …`, or a deep link.
- **Calculation:** the TypeScript `impactOfChange(target)`, which matches the MCP tool's semantics.
- **Map:**
  - the target's repo is drawn as the *source*
  - affected repos, projects and endpoints get the `--bad` border, with a count badge of affected call sites
  - edges on affected paths turn `--bad`
  - everything else fades
- **Top bar:** the pinned target chip and red counters (repos, call sites).
- **Inspector:**
  - a headline ("Changing X affects N other repos")
  - KPIs (repos, endpoints, call sites)
  - the field path, when the target is a field
  - affected call sites, each with its chain (client method → endpoint) and a confidence pill
  - a "Not affected" note listing repos that are nearby but not affected
- **Lists:** affected lists are capped at 200 per kind, with a "+N more" row. The map always shows every affected node.

## 5. Data flow

### 5.1 Export: `depenk export [--out <file>] [--focus <id>]`
1. Resolve the workspace exactly as `scan` does (`--workspace`, `$DEPENK_WORKSPACE`, auto-detect).
2. Load the graph through `GraphStore.Current()`, which scans first if the graph is missing. If the graph is stale, rescan synchronously first, the same as `depenk query` does.
3. Serialize the graph with `GraphJson`, then escape it for embedding in HTML:
   - `<` → `<`
   - `>` → `>`
   - `&` → `&`
   - U+2028 and U+2029 → their `\u` escapes
4. Insert it at the `<!--depenk-graph-->` marker in the embedded bundle.
5. With `--focus`, set `location.hash` before first render through a `<meta name="depenk-focus">` tag.
6. Write with `AtomicFile` to `--out`, or by default to `<workspace>/.depenk/depenk.html`, and print the absolute path.

**Exit codes:** the same as `scan`. `0` ok, `1` unexpected or write failure, `2` invalid config, `3` workspace not found.

**`export_diagram(focus?, path?)`** runs the same flow and returns the standard envelope, with `data: {path, bytes}`. A relative `path` resolves against the workspace.

**Source code:** exports never contain source code. They contain paths and line numbers only.

### 5.2 Serve: `depenk serve [--port <n>] [--no-open]`
- **Host:**
  - Kestrel bound to `127.0.0.1` only
  - `--port 0` (the default) picks a free port
  - prints `depenk serve: http://127.0.0.1:<port>/` and opens the default browser unless `--no-open` is passed
- **Routes:**
  - `GET /` serves the bundle without inlined data, `Cache-Control: no-store`.
  - `GET /api/graph` serves the current graph JSON with a strong ETag (the graph's content hash) and honors `If-None-Match` → 304.
  - `GET /api/events` is SSE. It sends `event: graph-changed` with `data: {etag}` after each completed rescan, plus a heartbeat comment every 25 s.
- **Request guard:** requests whose `Host` isn't `127.0.0.1:<port>` or `localhost:<port>` get 403. This blocks DNS rebinding. There are no write endpoints.
- **Live refresh:**
  - a `FileSystemWatcher` on the workspace, filtered to `*.cs`, `*.csproj`, `*.props`, `*.targets` and `depenk.yml`, and ignoring `.depenk/`, `bin/`, `obj/` and `.git/`
  - changes are debounced for 750 ms, then trigger `GraphStore.Rescan()`, then `graph-changed`
  - if a rescan fails, it logs to stderr and the server keeps serving the last good graph
- **In the UI:** on `graph-changed`, the app refetches `/api/graph`, keeps its selection, viewport and impact target where they still exist, and fades in added nodes.
- **Shutdown:** Ctrl+C stops the server cleanly.

### 5.3 `open_diagram(focus?)`
- **First call:** starts `Depenk.Server` inside the MCP process, sharing the session's `GraphStore` and the port-0 behavior.
- **Later calls:** reuse the running server.
- **Returns:** `data: {url}`, where `url` = `http://127.0.0.1:<port>/#/<focus>`. It never opens a browser.
- **Lifetime:** the server stops when the MCP host shuts down.
- **Stdout:** it must never write to stdout. Kestrel logging goes to stderr, or is turned off.

### 5.4 Graph loading in the UI
- **Format check:** `schemaVersion` must equal 1. Any other value shows an "upgrade depenk" message and nothing is rendered.
- **Dangling references:** an edge whose endpoint node is missing is dropped and shown as a synthetic `danglingEdge` diagnostic in the sidebar.
- **Fetch failure in serve mode:** a banner says "Lost connection — retrying". The app keeps the last graph and retries with backoff, from 1 s up to 30 s.

## 6. Error handling summary

| Situation | Behavior |
|---|---|
| Config or scan failure (export, serve start, MCP tools) | Existing structured errors (`invalid_argument` / `scan_failed`) and exit codes |
| Port in use (explicit `--port`) | `Port 5050 is already in use` on stderr, exit 1 |
| Export write failure | The path in the message, exit 1; nothing half-written (`AtomicFile`) |
| Rescan failure while serving | stderr log; keep serving the last good graph; no event sent |
| Non-loopback `Host` header | 403 |
| Unknown deep link or palette target | "Not found" with suggestions |
| Unsupported `schemaVersion` | A message telling the user to upgrade; no render |

## 7. Testing

### 7.1 Frontend (`web/`, Vitest + Testing Library + Playwright)
- **Unit tests:**
  - `GraphIndex`
  - the queries (neighbors, trace, impact, search)
  - the layout-input builders for each depth
  - the repo color hash
  - deep-link parsing
  - the HTML-escape round-trip of the inlined graph
- **Conformance:** a Vitest runner executes every `tests/query-cases/*.json` case against the TypeScript queries. A zero-case guard fails the run if no cases load.
- **Component tests:**
  - inspector tabs for each kind
  - sidebar filters and collapse
  - the Ctrl+K palette, including the `impact` prefix
  - impact mode entering and leaving
  - the overview's needs-attention ordering
- **Playwright screenshots** against the fixture graph, in **light and dark**:
  - repo depth
  - project depth
  - endpoint depth
  - impact mode
  - collapsed sidebar
  - phone width (390 px)

  Baselines are committed. During development, before and after Puppeteer screenshots go to the user, per their global instructions.
- **Performance:** the synthetic 50-repo graph from `SyntheticWorkspace` is exported to JSON. The repo view must render in under 1 s, and no main-thread long task may exceed 200 ms during layout (checked with the Long Tasks API in Playwright).

### 7.2 .NET (xUnit)
- **Export:**
  - the graph round-trips out of the HTML
  - a model named `</script><script>alert(1)</script>` stays inert data
  - `--focus` sets the meta tag
  - the write is atomic
  - the default path and exit codes are correct
- **Server** (`WebApplicationFactory` / `TestServer`):
  - `/` serves the bundle
  - `/api/graph` returns its ETag and 304
  - SSE sends `graph-changed` after `Rescan()`
  - a foreign `Host` gets 403
  - the server binds only to loopback
- **MCP:**
  - `export_diagram` and `open_diagram` through `McpHarness`
  - `McpStdioTests` proves `open_diagram` writes nothing to stdout
  - `ToolNames` now lists 14 tools
  - `PackagingTests` (the skill mentions every tool) and the README tool table are updated

## 8. Build and repository

- `web/` has its own `package.json` with exact pinned versions:
  - react
  - @xyflow/react
  - elkjs
  - vite
  - vite-plugin-singlefile
  - vitest
  - @testing-library/react
  - @playwright/test

  Node 22 is required for development and CI only. Users installing the .NET tool never need Node.
- `Depenk.Server.csproj`:
  - embeds `web/dist/index.html` as `Depenk.Server.index.html`
  - an MSBuild target, `BuildWeb`, runs `npm ci && npm run build` when `web/dist/index.html` is missing or older than any file under `web/src` or `web/package.json`
  - setting `DepenkSkipWebBuild=true` skips that target, for example when CI builds the web leg separately
- **CI legs:**
  1. `npm ci && npm test` (Vitest plus conformance)
  2. `npm run build`, then `dotnet test`
  3. Playwright against the built bundle
- **Version:** `Directory.Build.props` and `.claude-plugin/plugin.json` both go to `0.3.0`, which `PackagingTests` already checks. The README gets a "Diagram" section, the roadmap box is ticked, and the skill learns about `open_diagram` ("show me").

## 9. Open questions resolved during brainstorming

| Question | Decision |
|---|---|
| Layout | B, Command Center, with a collapsible sidebar |
| Delivery | Export file **and** live serve; `open_diagram` uses serve in-process |
| Stack | React + React Flow + elkjs (worker) + Vite single-file |
| Look | The ServiceMap sample's tokens and type; light and dark |
