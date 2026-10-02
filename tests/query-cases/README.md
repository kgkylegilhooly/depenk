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
