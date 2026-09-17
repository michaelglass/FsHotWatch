# Function-scoped Falco route edges (fix browser-test over-selection)

## Problem (reproduced, quantified in a large private downstream repository)

TestPrune.Falco over-selects integration/browser tests. Measured against the
53 integration `.fs` files:

- A change to `Web/Handlers/User.fs` (serves **24** routes) selects **14/53**
  files — even for a one-function change, because *all* of the file's routes'
  tests are pulled.
- A change to `Landing.fs` (route `/`) selects **53/53** (defensible — `/` is
  load-bearing for every flow; left as-is).

### Root cause — `FalcoRouteExtension.AnalyzeEdges` emits a cross-product

```
changedHandlerSymbols = every symbol in the changed handler file
affectedTestMethods   = every test method in a route-matched test class (file-level match)
edges = [ for h in changedHandlerSymbols do for t in affectedTestMethods -> t -> h ]
```

Core selection (`Database.QueryAffectedTests(changedSymbolNames)`) then walks
these edges: any changed function in the file is a `ToSymbol` on edges to *all*
the file's route-matched tests → all of them selected. Granularity is the whole
file, not the changed function's route.

## Fix — connect each route's tests only to that route's handler function

`RouteEndpoints.fs` (in that downstream repository) already maps each route to its handler
function (`Route.Landing LandingRoute.Root -> Some Landing.index`). Carry that
function name into the route store so edges become **function-scoped**:
a route's tests link to the *specific* function serving that route.

### Why this is safe (no under-selection)

The route edge only bridges the HTTP boundary (a browser test hits a URL, not a
symbol). Ordinary call deps — a handler calling a helper that changed — are
already captured by TestPrune.Core's symbol dependency graph, which marks the
handler function affected transitively. So scoping the route edge to the direct
handler function drops **no** genuinely-affected test. Backed by:
1. A regression test proving a one-function change to a multi-route file selects
   only that function's route's tests.
2. A regression test proving a helper change (same file) still selects the
   dependent route's tests **via the symbol graph** (belt-and-suspenders).
3. `HandlerFunction = None` falls back to today's file-level cross-product —
   so any un-seeded route keeps current (safe, broad) behavior.

## Phase 1 — TestPrune engine (this brief; self-contained, no release)

1. **`AstAnalyzer.fs` `RouteHandlerEntry`** — add `HandlerFunction: string option`
   (short `Module.function`, e.g. `Landing.index`; `None` = legacy/unresolved).
2. **`Database.fs` schema** — add `handler_function TEXT` to `route_handlers`
   (nullable). Follow the existing schema-drift recreate path (see the ~line-238
   "recreate" comment) so an old DB with the 3-col table is rebuilt, not left
   stale. Update `RebuildRouteHandlers` INSERT + add a reader that returns full
   `RouteHandlerEntry` rows for a source file.
3. **`Ports.fs` `RouteStore`** — add
   `GetRouteHandlersForSourceFile: string -> RouteHandlerEntry list`
   (keep `GetUrlPatternsForSourceFile`/`GetAllHandlerSourceFiles`).
4. **`FalcoRouteAnalysis.fs` `AnalyzeEdges`** — replace the cross-product:
   for each changed handler file, for each of its routes, resolve the route's
   matching test classes (per-route URL regex, reusing `findTestClassesInFiles`
   with a single regex), then emit edges from those tests' methods to the
   route's handler-function symbol. Match the handler symbol in the symbol store
   by **suffix** (`s.FullName.EndsWith("." + handlerFunction)` — mirrors the
   existing test-class suffix match at lines 108-109), because the seed carries
   the short `Module.function` and the store holds the fully-qualified name.
   When `HandlerFunction = None`, keep the current file-level cross-product for
   that route (fallback). Handle `config`-applied handlers — the function is the
   bare symbol (`WellKnown.robots`), not the partial application.
5. **`FalcoRouteExtensionTests.fs`** — extend `db.RebuildRouteHandlers([...])` in
   the harness to set `HandlerFunction`, and add the two regression tests above
   plus a fallback test (`HandlerFunction = None` ⇒ current behavior). Keep all
   existing tests green (they should, with `HandlerFunction = None`).

**Gate:** `mise run ci` green (50/50 coverage, tests, FCS clean, fantomas clean).
New public API (`RouteHandlerEntry.HandlerFunction`, `GetRouteHandlersForSourceFile`)
→ FsSemanticTagger will pick a minor bump; do **not** hand-edit versions.

**Do NOT:** run `mise run release`, push, or touch the downstream repository.
Leave it committed on a jj change for review.

## Phase 2 — downstream seed (separate, after Phase 1 releases)

- Populate `HandlerFunction` in `RouteMapping.generateMappings`. Preferred:
  AST-derive route→function from `RouteEndpoints.fs` (single source of truth,
  lets us delete the parallel `routeToHandler` file-map). Pragmatic interim:
  extend `routeToHandler` to also return the `Module.function` short name.
- Bump the TestPrune pin, re-seed (`seed-routes`), verify over-selection drops
  (User.fs one-function change → only that route's tests) via `dotnet fshw check`.

## Rollback / abandon criteria

- If `mise run ci` can't go green on the edge-scoping within the engine, or a
  regression test shows a genuinely-affected test dropped that the symbol graph
  does *not* recover → abandon the edge-scoping, keep only the additive schema
  (`HandlerFunction` unused) so nothing regresses, and escalate the design.
