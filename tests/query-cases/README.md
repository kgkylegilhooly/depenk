# Query conformance cases

Language-neutral test cases for depenk's graph queries. The C# query layer (`Depenk.Query`) runs them in
`tests/Depenk.Tests/QueryConformanceTests.cs`; the browser UI's TypeScript queries (Plan 3) must pass the same files.

Each file: `{ name, graph, op, args, expected }`, `graph` relative to the case file. Both graph and case files may
start with a UTF-8 BOM — implementations must strip it before parsing JSON.

## Semantics every implementation must share

### Normalized adjacency
Every edge is a dependency hop *dependent → dependency* with its original direction, except `produces` 
(project → package), which is flipped to package → project.

### neighbors
- **Args:** `id` (node ID), `direction` ("down" or "up"; no other values permitted)
- **Returns:** distinct far-end node IDs reachable in the given direction, sorted ordinally
- **Semantics:** includes far ends even if they are not known nodes in the graph; no filtering for unknown nodes

### trace
- **Args:** `id` (root node; must resolve to an exact node in the graph), `direction` ("down", "up", or "both"), 
  `depth` (integer, clamped to 1..10)
- **Returns:** `"{id}@{depth}"` for each node visited, in visit order (breadth-first)
- **Semantics:**
  - Root is resolved as an exact node ID and is **excluded from output**
  - BFS traversal visits each node at most once per direction (with `both`, a node can appear once per direction)
  - At each node, hops are traversed in ordinal order of far-end ID
  - Duplicates via several hops to the same far end are emitted once (first visit wins)
  - Hops whose far end is not a known node are **skipped** (unlike neighbors)

### search
- **Args:** `query` (string), `limit` (positive integer)
- **Returns:** up to `limit` node IDs, ordered by rank
- **Candidate set:** every node of every kind (if duplicate IDs exist across kinds, first occurrence wins)
- **Ranking:**
  1. Query is a case-insensitive substring of the ID or label (substring match)
  2. Smaller Levenshtein distance of lowercased query to lowercased ID or label (unit-cost over UTF-16 code units; 
     no transposition; take the smaller of the two distances)
  3. Ordinal comparison of the node ID (tie-breaker)
- **Casing:** both query and candidate IDs/labels are lowercased using invariant culture before substring/distance tests
- **Labels by node kind:** repo/project name, PackageId, `"VERB /route"` (e.g., `"GET /api/orders"`), 
  `"Type.Method"` (call site's containing member), full type name (model)
