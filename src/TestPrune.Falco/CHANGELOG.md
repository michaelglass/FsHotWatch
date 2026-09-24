# Changelog — TestPrune.Falco

## Unreleased

## 3.1.6 - 2026-09-24

- docs: `RouteStore` can be built over `TestPrune.Ports.pluginStoreAt dbPath` when the
  process only seeds or reads routes and never opens the core index, so route seeding no
  longer depends on the seeding process and the indexing process agreeing on core's
  schema version. No code change: `RouteStore` already took any `PluginStore`.

## 3.1.5 - 2026-09-23

- docs: reword private downstream-repo references and a scrubbed coverage reason


## 3.1.4 - 2026-09-15

- Recognize accessible and escaped test declarations and file modules when
  attributing route changes to tests and dependency edges.

## 3.1.3 - 2026-08-19

- a >] inside an attribute string must not truncate the block


## 3.1.2 - 2026-08-18

- **a route with no path text of its own no longer matches the F#
  comment token.** `urlPatternToRegex` admitted a leading `/` so a doubled separator
  still read as a path start. For the root route `/` — whose whole pattern is that one
  separator — the two combined to match `//`, which is how F# opens a comment, and `^`
  let a file-opening `// license header` do the same. The route therefore matched every
  commented line in the repo.

  Measured on the consumer this was built for: route `/` matched 4,886 comment openers
  (`// `, `/// `, and their bare-line forms) against 43 real URL literals, so a
  one-line landing-page edit selected **60 of 61** integration test classes — every
  other handler file selected between 0 and 20. It is now 13, and the exhaustive
  per-file diff over all 32 handler files adds nothing anywhere and changes no other
  file's selection at all.

  Nothing that evidences the route is lost: every quoted literal (`"/"`, `"/?lang=en"`,
  `'/'`) still matches, because a quote opens it. The guard is scoped to patterns with
  no literal text of their own (`/`, and param-only routes like `/{lang}`) rather than
  applied to every route — across the consumer's other 175 route patterns the `/`
  alternative changed no file's outcome, but losing a match is the dangerous direction,
  so a route with text of its own keeps the broader boundary.

## 3.1.1 - 2026-08-17

- docs: **route→test attribution is what makes `[<TestPrune.CompositionRoot>]` safe,
  and this release states the limit.** TestPrune.Core can now be told to stop
  propagating relevance through an application's composition root, which is only sound
  while some *other* attribution still reaches the tests covering a changed handler.
  This extension is that other attribution — it links each route to the tests that name
  its URL, independently of the composition edge.

  The gap to know about: a test that reaches a route by **clicking** rather than by
  naming the URL (`page.ClickAsync "#stop-impersonating"`) is not attributed here, so
  the composition-root barrier would drop it. Measured on the consumer this was built
  for, 29 of 32 handler files were fully attributed and the three misses were exactly
  those click-driven browser tests. **Do not mark a composition root in a repo whose
  browser tests navigate by UI interaction** until this extension attributes
  click-driven navigation. Core's per-project fail-safe bounds the damage but does not
  close it.

  No API or behaviour change in this package — the source diff since 3.1.0 is comment
  rewrites only. Released to move the `TestPrune.Core` dependency floor onto the
  version that has composition-root support.

## 3.1.0 - 2026-08-05

- resolve Falco route non-literal navigation, dependency-free


## 3.0.2 - 2026-07-26

- fix: **A class must show evidence that it holds tests to be selected.**
  3.0.1 made module selection depend on a test attribute but
  left every `type X(...)` unconditionally selectable, so fixtures and helpers
  were still returned as affected "test classes" — in a large private downstream repository,
  `IntegrationTestFixture`, `TestServer` and `BrowserErrorTracker`. Selecting one
  runs nothing on the filter path, and on the edge path fabricates test→handler
  edges out of fixture members that `QueryAffectedTests`' transitive reverse-walk
  then amplifies into every test touching the fixture. Classes and modules now
  share one rule: a span is selectable when it carries a test attribute, or —
  classes only — an `inherit` clause, because xUnit also runs test methods a base
  class declares. Two safe-direction guards come with it: a `FactAttribute`
  subclass (`[<SkippableFact>]`, `[<WindowsTheory>]`) now counts as a marker, and
  an `inherit`ing class with no marker of its own stays selectable. Merely
  implementing `IClassFixture`/`ICollectionFixture`, or being a
  `[<CollectionDefinition>]` marker, is not evidence. The conservative fallback is
  unchanged: a URL matched outside every selectable span still selects all of the
  file's test declarations, so a test reaching the route only through a fixture
  constant is never dropped. A file whose only declarations are fixtures now
  contributes nothing — there is no test in it to run.

## 3.0.1 - 2026-07-18

- fix: **Route→test selection is per-declaration, not per-file.** A
  matched test file no longer selects every class and module it contains: only test
  classes and test-bearing modules whose own span matches the route URL are selected,
  with a conservative fallback — any match outside every selectable span (file header,
  a shared URL-constant/helper module) selects all of the file's test declarations, so
  a test that exercises the route only indirectly is never dropped. Non-test helper
  modules (fixtures, URL holders) are never returned as affected. Test attributes are
  recognized only inside `[<...>]` blocks — combined lists like
  `[<Trait("a","b"); Fact>]` included — so attribute-like names in ordinary code
  (`[ users; TestCase(1) ]`) can no longer make a helper module selectable and
  suppress the fallback. Known residual (documented in the code): a literal `>]`
  inside an attribute string argument closes the block early and can hide a
  module-style test's only marker.

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
