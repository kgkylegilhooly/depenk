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
