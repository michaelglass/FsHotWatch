# Changelog — TestPrune.Falco

## Unreleased

## 3.0.0 - 2026-07-15

- fix: **`findTestFiles` no longer hangs forever.** It scanned the integration-test
  directory with `SearchOption.AllDirectories`, which follows directory symlinks —
  and `tests/*/bin` holds Playwright's Nix-provisioned browser symlinks, so the walk
  escaped into /nix/store and reached its self-loop symlinks (`ncurses -> .`), which
  double the path count per level. Effectively non-terminating. Because this runs
  inside `FindAffectedTestClasses`, it hung impact analysis itself: `fshw check`
  logged `QueryAffectedTests: 1964 affected tests` and then went silent for hours
  without ever launching a test. Now walks via `TestPrune.SafeWalk`, which never
  traverses a symlinked directory and prunes `bin`/`obj` during traversal rather than
  filtering them out afterwards.
- feat!: TestPrune.Falco owns the route table. `RouteHandlerEntry` and a new
  `RouteStore` type (its own `route_handlers` table, created on demand inside
  TestPrune's cache database through core's `Ports.PluginStore` seam) live here now,
  not in TestPrune.Core — the core engine no longer carries any HTTP/route/URL
  concept. BREAKING CHANGE: seed with `RouteStore(toPluginStore db).Rebuild entries`
  instead of `db.RebuildRouteHandlers entries`, and pass that `RouteStore` to
  `FalcoRouteExtension` instead of `Ports.toRouteStore db`. `AffectedTest` (returned
  by `FindAffectedTestClasses`) also moved here from `TestPrune.Extensions`.
- fix: an unresolvable `HandlerFunction` no longer drops a route's edges. A seed
  naming a handler that has since been renamed or moved used to scope to zero symbols
  and emit nothing, so that route's tests silently stopped being selected —
  under-selection. It now falls back to the file-level match, like `None` does. This
  is core's shared `EdgeEmission.edgesTo`, which `AnalyzeEdges` now builds every edge
  with; the function-scoped behaviour (and its regression tests) is unchanged.
- feat: `RouteStore.Rebuild` is atomic — a rejected entry rolls the whole re-seed back
  rather than leaving the route table half-written.
- chore(deps): refresh to TestPrune.Core 6.0.0 (SchemaVersion 8). Core's `route_handlers`
  table and route API are gone; Falco now owns that table through `Ports.PluginStore`.

## 2.0.4 - 2026-07-11

- fix: function-scoped route edges. `AnalyzeEdges` now links each route's tests
  to that route's *handler function* (via `RouteHandlerEntry.HandlerFunction`)
  instead of the whole changed file's symbols × all-its-routes' tests
  cross-product, so a one-function change to a multi-route handler no longer
  over-selects every route's browser tests. Falls back to the prior file-level
  behaviour when `HandlerFunction` is `None`; no under-selection, since ordinary
  call deps are still caught by TestPrune.Core's transitive symbol graph.
- chore(deps): refresh to TestPrune.Core 5.0.0.

## 2.0.3 - 2026-06-25

- feat: `FalcoRouteExtension` — route-based integration-test selection. Maps a changed Falco handler file to the integration tests that exercise its routes by scanning test sources for URL patterns (including `{param}` placeholders), pulling those tests into TestPrune's impact set.
- chore(deps): refresh to TestPrune.Core 4.3.0.

## 2.0.2 - 2026-06-12
- refactor: adapt to revised ITestPruneExtension edge-injection interface

## [1.0.2] - 2026-04-11
- chore: update NuGet dependencies to latest versions
- chore: bump local tool versions (coverageratchet, fssemantictagger, syncdocs, fsprojlint) to latest alpha

## 1.0.1
- fix: replace bespoke CI with shared NuGet tools and reusable workflows
- fix: workflow cleanup from code review
- chore: add NuGet Trusted Publishing comment, set check-docs: false for AnalyzerShim
- chore: remove leftover scripts/ directory replaced by shared tools
- feat: use auto-discovering example-projects in CI workflow
- note: version bumped to 1.0.1 to avoid accidental publication of reserved 1.0.0

## 0.1.0-beta.1
- feat: add indexing benchmarks and enable TransparentCompiler

## 0.1.0-alpha.9
- (no Falco-specific changes; version bumped in lock-step with Core)

## 0.1.0-alpha.8
- test: add FalcoRouteExtension unit tests for multi-class and multi-handler scenarios
- refactor: FalcoRouteExtension uses RouteStore port type; extensions take RouteStore instead of Database

## 0.1.0-alpha.7
- (no Falco-specific changes; version bumped in lock-step with Core)

## 0.1.0-alpha.6
- refactor: eliminate mutable accumulators in FalcoRouteAnalysis; hoist regex patterns to module level
- refactor: FalcoRouteAnalysis address code smells — narrow broad catches, fix connection leak
- feat: add SymbolStore/SymbolSink port types used by extension interface

## 0.1.0-alpha.5
- (no Falco-specific changes; version bumped in lock-step with Core)

## 0.1.0-alpha.4
- (no Falco-specific changes; version bumped in lock-step with Core)
