# ADR 0001 — TestPrune.Falco resolves Falco.UnionRoutes symbolic navigation via source (AST) composition, not runtime reflection

Status: Accepted (2026-08-04)

## Context

TestPrune.Falco selects integration tests for a changed HTTP handler by matching the changed
route's URL against the raw text of each test file. That is purely textual, so a test that
navigates via a **typed route value** rather than a URL literal —

```fsharp
let! resp = client.GetAsync(Route.link (Route.Admin(NoPreCondition, AdminPages.Settings)))
```

— carries no `/admin/settings` substring in its own span and was **silently dropped** (an
under-selection soundness hole: a real change to that route would not select its covering test).

Closing it needs a case→URL map: given the route DU, resolve `AdminPages.Settings` to
`/admin/settings` and feed it to the same matcher. The question this ADR settles is *how* to obtain
that map.

`Route.link`/`Route.info` navigation is real in the intelligence consumer (e.g. `SystemHealth.fs`),
so this is a live gap, not a hypothetical one.

## Options

1. **AST source-derivation (dependency-free).** Parse the route DU's `[<Route(Path=…)>]`
   attributes and nesting from source (FCS untyped tree) and re-derive the URL by the same rules
   Falco.UnionRoutes uses (explicit path, empty-segment convention names, kebab fallback, nested
   concatenation, field-inferred params). Match **constraint-insensitively** (`{id:guid}` ~ `{id}`)
   because the source cannot infer a constraint from a wrapped id type. No dependency on
   Falco.UnionRoutes; no built assembly required.

2. **Runtime reflection.** Add Falco.UnionRoutes as a dependency and call its canonical
   `Route.enumerate`/`Route.info` over the host's route type, loaded from the host's **built route
   assembly** at analysis time. Canonical by construction, but adds Falco.UnionRoutes — and
   transitively Falco + ASP.NET Core — as a hard dependency of every TestPrune.Falco consumer
   (most of whom write plain string routes), and requires loading the host's compiled assembly
   (AssemblyLoadContext + `.deps.json` resolution + load-failure risk).

## Evidence

Both options were built and measured head-to-head, in a synthetic corpus and against the real
`Intelligence.Domain.Routes.Route` (241 distinct canonical URL patterns):

| Resolver | Real-route exact | Over-selection | Runtime dep | Built assembly |
| --- | --- | --- | --- | --- |
| AST, exact matching | 149 / 241 (62%) | none | none | no |
| **AST, constraint-insensitive** | **241 / 241** | **0 collisions** | **none** | **no** |
| Reflection (`Route.info`) | 241 / 241 | 0 | Falco.UnionRoutes (+Falco+ASP.NET) | yes |

- Pure AST's only misses were **constraint-only** (0 structural): it composes `/admin/briefs/{id}`
  where the route table carries `/admin/briefs/{id:guid}`. All 92 misses were of this kind.
- Constraint-insensitive matching is a strict **superset** of exact matching (it only adds
  matches, never drops — so it cannot under-select) and closes all 92 with **zero over-selection
  collisions**: no two real routes normalize to the same URL while differing only by constraint.
  Falco.UnionRoutes' own routing forbids two distinct routes colliding on a pattern, so that zero
  is **structural, not luck**.
- Reflection's only concrete deltas over constraint-insensitive AST were therefore (a) constraint
  precision — moot given the collision guard — and (b) zero-maintenance composition as the routing
  library evolves.

## Decision

Adopt **Option 1: AST source-derivation with constraint-insensitive matching**, in a
dependency-free core (`UnionRouteLinks`), plus a **drift-alarm contract test**
(`SyntheticRouteContractTests`) that asserts the AST composition equals Falco.UnionRoutes' canonical
`Route.enumerate` over the synthetic corpus (modulo constraints), reflecting the synthetic type
**in-process** — no host-assembly loading.

This keeps TestPrune.Falco **string-route-pure**: any consumer adopts it without pulling a routing
library, and the string-route path (`StringRouteConstants`, named-URL-constant resolution) and the
core matcher stay UnionRoutes-agnostic. Falco.UnionRoutes is a **test-only** dependency of
TestPrune.Tests (the drift alarm); it does **not** appear in the shipped TestPrune.Falco package.

## Consequences

- No Falco.UnionRoutes runtime dependency, no ASP.NET Core transitive weight, no requirement for the
  host's built route assembly at analysis time.
- The one cost: the AST holds a **copy** of Falco.UnionRoutes' URL-composition rules. If the library
  ever changes those rules (kebab, empty-segment names, nesting, param inference), the drift-alarm
  contract test turns red and `UnionRouteLinks` gets a deliberate, matching update — it cannot drift
  silently. Constraint changes are deliberately tolerated (handled by constraint-insensitive
  matching), so only *structural* rule changes trip the alarm.
- `Route.enumerate` was added to Falco.UnionRoutes as the contract-test oracle (and is independently
  useful); it is the only reason the test project references the library.
- **The drift alarm is optional, and a standalone clone still builds green.** `Route.enumerate` is
  unreleased (no published Falco.UnionRoutes package carries it — 0.3.3 is the latest and does not),
  so the oracle can only be reached through a sibling checkout of that repo. That must not be a
  build requirement for anyone who clones TestPrune alone. `TestPrune.Tests.fsproj` therefore gates
  the `ProjectReference` *and* the two source files that name the library
  (`SyntheticRoutes.fs`, `SyntheticRouteContractTests.fs`) on `FalcoUnionRoutesAvailable`, which
  defaults to `Exists(../../../Falco.UnionRoutes/...)`. Without the sibling the suite compiles and
  passes with the alarm excluded; the build prints a high-importance notice and a `Skip`-marked
  stand-in (`SyntheticRouteContractSkipped.fs`) reports the omission in the test results, so a green
  run never overstates what it checked. Force the absent path on a machine that has the sibling with
  `FalcoUnionRoutesAvailable=false mise run ci`. The trade-off is deliberate: the alarm guards
  maintainers (and CI machines that check both repos out) against silent drift, while contributors
  are never blocked by a dependency they cannot obtain from NuGet. Once a Falco.UnionRoutes release
  ships `Route.enumerate`, this gate can be replaced by a plain `PackageReference`.

## Rejected / deferred

**Runtime reflection resolver** — measured equivalent (241/241) but broad cost (couples every
consumer to a minority routing library + needs the host's built assembly) with no concrete gain
today. It was fully built during the evaluation (host-assembly `AssemblyLoadContext` loader,
`fromType`/`fromAssembly`, the head-to-head harness) and then abandoned.

- Abandoned code: TestPrune change `wvuwvmzz`, commit **`8e61afed181a`** (`ReflectionRouteLinks.fs`
  + the reflection comparison harness). Revive with `jj new 8e61afed181a` (in the op log) if needed.
- Revival triggers: (1) wanting route→handler auto-derivation by reflecting the app's endpoint
  wiring — genuinely reflection-only, the AST cannot see runtime wiring; or (2) the
  composition-maintenance chore (keeping `UnionRouteLinks` in step with Falco.UnionRoutes) costing
  more than the dependency would.
