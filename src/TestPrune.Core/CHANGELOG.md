# Changelog — TestPrune.Core

## Unreleased

- fix!: **`SchemaVersion` 13 -> 14.** A symbol is one row with one source occurrence
  per declaring file (`symbol_occurrences`: location and content hash). Dependency
  edges, test methods and attributes record the file whose analysis contributed them,
  so re-indexing one file replaces only that file's facts. A symbol survives while any
  file still declares it, and coverage points belong to an occurrence. Existing
  indexes are recreated automatically, and `Database.WasRecreated` reports it.
  BREAKING CHANGE: `GetSymbolsInFile` and `GetAllSymbols` return one entry per
  occurrence. `GetDependenciesFromFile` and `GetTestMethodsInFile` return the facts
  that file's analysis contributed. `FindSymbolContainingLine` returns an occurrence id.
- fix: queries that test membership against a caller-supplied list of names no longer
  fail once the list is large. They spent one SQLite host parameter per name, and SQLite
  caps those at `SQLITE_MAX_VARIABLE_NUMBER` (32766 in the pinned `e_sqlite3` build), so
  the ceiling was set by repository size rather than by anything a caller could see: a
  full unfiltered pass over a 64,913-symbol index died with
  `SQLite Error 1: 'too many SQL variables'`, taking the whole run with it, while the
  impact-filtered path never came close. The set now travels as ONE bound JSON array, so
  no list length changes the parameter count. Chunking was rejected: it raises the ceiling
  without removing it, and `QueryAffectedTests` defines its composition-root barriers by
  exclusion from the seed set, so a per-chunk walk would treat seeds outside its chunk as
  barriers and silently select fewer tests.

## 9.0.0 - 2026-09-17

- fix: `AuditSink.Flush()` no longer waits forever. The wait is bounded by
  `flushTimeoutMs` (120s) and returns a `FlushOutcome`: `Flushed`, or
  `FlushTimedOut` with the bound, reported on stderr. Before this, a sink whose
  agent never replied (stopped, or wedged in a persist that never returns) hung the
  caller with no diagnostic. BREAKING CHANGE: `Flush()` returns `FlushOutcome` instead of
  `unit`, so callers must handle the timeout rather than read it as a completed flush.
- fix: a persist function that throws no longer stops an audit sink. The failure is
  reported on stderr, that event is dropped, and later events are still persisted.
  Before this, one failing event silently discarded every event after it.

## 8.2.0 - 2026-09-15

- fix: index declarations from signature files (`.fsi`) as their own source
  occurrences, hashed independently of the implementation bodies they describe,
  so a consumer whose dependency edge runs through a signature is selected when
  that signature changes. Signature ranges cover multiline types and constraints,
  which FCS identifier ranges omit; compiler declaration kinds are verified rather
  than assumed.
- fix: the in-memory store follows SQLite's upsert rule for repeated symbols: a
  reference placeholder can no longer replace a real declaration's kind or owning
  file, and an extern is used only when no real declaration exists.

## 8.1.5 - 2026-08-29

- perf: collect shared literals through FCS's typed syntax fold instead of recursively
  reflecting over the compiler's full object graph. Large files no longer allocate
  gigabytes while walking value-rich live parse trees; opt-in analysis diagnostics expose
  per-stage timing, allocation, GC, and traversal-cache counters for host investigations.

## 8.1.4 - 2026-08-29

- fix: reuse completed generic-edge traversal for recreated wrappers of the same
  structurally identified monomorphic declaration, while generic instantiations and
  degraded traversals remain isolated. The calibrated 32,768-candidate file-wide cap
  covers captured FsHot files that charged 7,707 and 27,109 candidates (20.9% headroom
  over the measured maximum); root/child fanout, total work, and depth retain hard bounds.

## 8.1.3 - 2026-08-29

- fix: share the 4,096-node FCS traversal budget across the entire source-file
  analysis, including every symbol-use generic graph and test declaring-entity chain.
  Large files can no longer multiply the per-traversal allowance by thousands; the
  cumulative limit still fails closed while ordinary multi-symbol generic attribution
  remains intact.

## 8.1.2 - 2026-08-29

- fix: cap defensive FCS graph traversal at 4,096 expanded nodes and reduce the
  emergency depth ceiling to 32. Recreated branching type wrappers can no longer
  expand combinatorially; budget exhaustion is an analysis error, so callers widen
  conservatively instead of accepting a partial dependency graph. Finite repeated
  generic wrappers remain fully attributed.

## 8.1.1 - 2026-08-29

- fix: bound FCS type-argument and declaring-entity graph traversal by reference
  identity, logical ancestry, and depth. Compiler hosts can now reuse live parse and
  check results containing recursive symbol graphs without runaway CPU or memory,
  while finite nested generic arguments retain their dependency edges. Reaching the
  generous depth limit returns an analysis error instead of silently emitting an
  incomplete dependency graph; the CLI treats that refusal as an atomic index failure
  and preserves the last complete project graph.
- fix!: **`SchemaVersion` 12 -> 13.** The automatic cache recreation introduces
  durable, attempt-owned index metadata. A fresh or interrupted cache remains
  fail-closed until one complete index owns and records completion.

## 8.1.0 - 2026-08-28

- feat: add `AstAnalyzer.analyzeSourceFromResults` for compiler hosts that already
  have successful FCS parse and check results. It produces the same analysis as
  `analyzeSource` without parsing or type-checking the file a second time.

## 8.0.0 - 2026-08-28

- fix(release): publish independent packages by semantic dependency level and
  require every exact artifact to restore from nuget.org before tagging its
  dependents. A bounded process timeout, retry limit, isolated package cache,
  and guaranteed cleanup make an unavailable dependency fail closed.

- feat!: preserve project-attributed runtime coverage and union it with static
  impact selection. A complete project run replaces that project's
  file map; an impact-filtered run may add positive evidence but cannot erase the
  last complete baseline. Missing and stale baselines are reported as distinct typed
  states so a runner can widen the affected project instead of silently narrowing.

- feat!: **`SchemaVersion` 11 -> 12.** The runtime coverage map and its complete-run
  watermarks retain the test-project identity that the existing merged
  `coverage_points` high-water mark intentionally discards.

- feat: add `DiffParser.parseChangedPaths`, the lossless counterpart to
  `parseChangedFiles`. It returns every path in a Git/Jujutsu diff,
  including both sides of a rename and non-F# files, decodes Git C-quoted path names,
  and removes duplicates without changing their first-seen order. The existing
  `parseChangedFiles` API retains its code-only, rename-destination contract and now
  shares the same quoted-path decoder.

- feat!: **bridge prose-coupled tests to the production symbols whose messages they
  assert (first slice).** Symbol references cannot represent a test that
  receives a log line, response body, rendered label, or CLI verdict and compares it to
  the same literal without naming its producer. TestPrune now gives qualifying prose
  literals a hashed synthetic node, with `test -> literal -> producer` edges of the new
  public `DependencyKind.SharedLiteral` kind. The ordinary reverse walk therefore
  selects the test when the producer changes, without joining tests to other tests or
  producers to other producers.

  Extraction comes from decoded FCS `SynConst.String` nodes, not a source-text scanner:
  escape-equivalent spellings join the same node, comments never enter the input, and
  interpolated strings are deliberately excluded. Only prose-shaped values (at least 24
  characters and four words) participate, avoiding identifier-shaped hubs such as fixture
  email addresses and token names. Literal text is hashed and never stored in the index.

  Incremental consumers that rebuild before querying capture the old side of a message
  edit with `Database.GetPriorSharedLiteralSeeds`, then include those bounded seeds in
  their ordinary pending-verification lifecycle. This preserves the test's edge to the
  old prose without leaving a stale producer edge in the current graph.

- feat!: **`SchemaVersion` 10 -> 11.** Existing files could otherwise remain cache hits
  and never acquire their literal edges. The automatic rebuild makes the graph uniform.

- fix: preserve the `_extern` source sentinel while normalizing symbol paths. Synthetic
  literal nodes depend on that ownership boundary so re-indexing one producer cannot
  orphan another file's shared node.

- feat: **add project-attributed, freshness-aware runtime coverage selection primitives
  (phase 1).** `CoverageImpact` validates per-test-project Cobertura
  snapshots, unions every runtime-covered changed file with AST project attribution,
  and fails open with typed diagnostic reasons when a snapshot is missing, expired,
  from the wrong trailing baseline, future-dated, malformed, incomplete, or contains
  an invalid/outside-repository path. Snapshot construction is opaque and canonicalizes
  paths symmetrically without changing the existing raw coverage high-water store.

  This release note covers the Core domain API only. Snapshot persistence/discovery,
  live selector integration, gate output, Integration coverage collection, and the
  cross-repository release/pin chain remain follow-up work; TestPrune does not consume
  this signal yet.

## 7.0.1 - 2026-08-18

- fix!: **a computation-expression custom operation is indexed under the member it
  resolves to, not under its keyword — and `Module` can no longer be used to smuggle an
  unqualified name past the schema (rework).**

  The `symbols_full_name_is_qualified` CHECK shipped in 7.0.0 could not fail. The extern
  placeholder pass picked a kind from the shape of the string — no dot, therefore a module
  — so every unqualified name was relabelled into the one kind the CHECK exempts. The rows
  it was written to reject went on being written, wearing `Module`.

  What was being relabelled turned out to be a naming bug, not a module. At a USE site FCS
  reports a custom operation's `FullName` as the operation KEYWORD (`where`, `select`,
  `entity`), while `LogicalName` holds the member (`Where`) and `DeclaringEntity` holds the
  builder. Since `full_name` is UNIQUE, every keyword collapsed to one row, and every
  builder using that keyword — in any library — merged onto it. Measured on a real index of
  a ~9,300-test consumer repo: **one row named `select` was `SelectBuilder.Select`,
  `SqlHydra.Query.SelectBuilders.select`, `Falco.Markup.Elem.select` and `Feliz.Html.select`
  at once**, and one named `where` was three different builders' `Where` (451 direct
  dependents, 1,828 test methods reachable). Nine such rows, 1,465 edges.

  Two changes, at the two places the invariant can be held:

  - `AstAnalyzer` qualifies through `DeclaringEntity`, so the edge lands on
    `SqlHydra.Query.SelectBuilders.SelectBuilder\`2.Where` — the same name the definition
    side already records. A member or module value whose name cannot be qualified even
    that way is dropped rather than indexed under a name that is not its own.
  - The extern placeholder pass takes `Module` only on EVIDENCE that FCS classified that
    name as a module. Anything else unqualified stays `ExternRef` and is rejected by the
    constraint, which names the symbol and its file.

  Re-indexing that repo: 54 unqualified rows → 22, all of them real top-level
  single-segment modules (namespace-less Fable modules, each named after its own file).
  No in-repo symbol and no in-repo dependency edge changed, so test selection is
  byte-identical — measured across eight files, 0 delta on every one. The keyword rows
  were sinks; what they cost was correct attribution, not selection.

- feat!: **`SchemaVersion` 9 → 10.** No schema text change — a forced rebuild. An extern
  row's `source_file` is never in a re-indexed set, so orphan cleanup cannot collect one:
  without the bump the junk rows above would outlive the fix in place.

## 7.0.0 - 2026-08-17

- feat: **`[<TestPrune.CompositionRoot>]` — stop an application composition root
  propagating relevance through itself.** An app's routing table or
  DI registration block references every handler in the codebase in order to *wire
  them up*, and an integration fixture that boots the app depends on it. The
  reverse-walk therefore reaches every fixture-using test from every handler: in a large
  private downstream repository, editing one line of `AdminJournal.translate` selected **537
  integration tests across 57 classes** — the entire suite, browser tests included,
  about four minutes per gate. Four unrelated handlers returned the identical number,
  so this was the normal case, not an edge.

  Every edge on that path is *true* (`productHandler` really does reference
  `translate`; the fixture really does boot the app). What is false is the conclusion.
  Nothing in the graph distinguishes "wires X up" from "calls X", so the application
  author marks the composition root and the walk declines to carry relevance through
  it.

  The semantics are deliberately **asymmetric**, and both halves matter:

  - Relevance does **not** propagate *through* a marked symbol. Reached from
    something it aggregates, it is still reported affected, but the walk stops there.
  - Relevance **does** propagate *from a change to* it. Marked symbol in the change
    set ⇒ ordinary seed, full walk. "The app is wired differently now" is precisely
    what host-booting tests verify.

  That asymmetry is what lets one marker serve two opposed requirements: a handler
  edit must not reach the whole suite, while a startup/config edit must. Verified on
  the real 29 906-symbol graph — `AntiforgeryConfig.configure`, `TestServer.Start`
  and `productHandler` itself all select the same 537 tests before and after, while
  `AdminJournal.translate` drops **537 → 4**, exactly its own `JournalTranslateTests`.
  Across all 178 route handlers: 41 narrowed, 137 unchanged, **0 widened, 0 dropped
  to zero**, 95 587 → 74 706 selected integration tests (−21.8%).

  **Opt-in and name-matched.** An un-annotated repository takes one extra `EXISTS`
  probe and then the historical code path unchanged — asserted symbol-by-symbol, and
  the 200-case randomised soundness harness now doubles as proof of it. (That probe
  is honest but not yet fast: `symbol_attributes` is indexed on `symbol_id` only, so
  it is a table scan. Indexing `attribute_name` needs no schema-version bump and is
  tracked separately.) The attribute is matched by NAME exactly as `DependsOnFile`
  is, so a codebase that would rather not reference `TestPrune.Attributes` from
  production code can declare its own three-line `CompositionRootAttribute` — which
  is the supported route, since that package is not published to NuGet.

  **Fail-safe: a barrier may narrow a test project's selection, never empty it.**
  The narrowing is only sound while some *other* attribution still reaches the
  covering tests — TestPrune.Falco's route→test edges, in the case this was built
  for. That attribution is not total: Falco attributes 29 of that repository's 32
  handler files, and the three it misses are covered by browser tests that navigate
  by **clicking** (`page.ClickAsync "#stop-impersonating"`) rather than naming the
  URL. Barriering alone answers "no integration tests affected" for those — a green
  gate that verified nothing. A global "is the answer empty?" guard is not enough
  either: `CompanyProfile.saveProducts` keeps 4 unit tests, which would mask all 537
  integration tests vanishing. So the rule is per project, and it is why
  `saveProducts` measures 537 → 537 rather than 537 → 0.

  **Residual, stated rather than implied.** If a route has *some* attributed test and
  another that only clicks, the second is still dropped: the project is non-empty, so
  nothing fires. Closing that needs Falco to attribute click-driven navigation.
  **Until it does, do not mark a composition root in a repo whose browser tests
  navigate by UI interaction** — the mechanism is sound, the attribution feeding it
  is not yet complete. Note also that the fail-safe's granularity is *your* test-project
  layout: the numbers above hold because unit and integration tests live in separate
  projects, and a suite with a single test project gets the weaker "never select
  nothing" guarantee instead.

  The rule and the fail-safe live in `Domain.CompositionRoot` and are called by both
  the SQLite and in-memory stores, so the selector that ships and the selector the
  soundness harness grades cannot drift apart.

- chore(deps): **`SQLitePCLRaw.lib.e_sqlite3` 3.50.3 → 3.53.3.** The pin exists because
  the SQLitePCLRaw bundle pulls native `lib.e_sqlite3` 2.1.11, flagged High by
  GHSA-2m69-gcr7-jv3q; the native binary re-versions onto SQLite's own line
  independently of the 2.1.x managed core/provider/bundle and carries no managed
  dependencies, so pinning it forward stays clean.

  This is the right home for the pin, and now the only one. Because
  `CentralPackageTransitivePinningEnabled` is on, the `PackageVersion` entry becomes a
  real dependency of the published package — verified against the packed nuspec, which
  declares `SQLitePCLRaw.lib.e_sqlite3 3.53.3`. Consumers therefore inherit the floor
  and need no pin of their own; FsHotWatch carried two duplicates of this constraint
  until 2026-08-11 and has now dropped both.

## 6.1.2 - 2026-08-11

- feat!: **SchemaVersion 8→9 — an unqualified `full_name` is now a hard DB error.**
  The `symbols` table gains
  `CHECK (kind = 'Module' OR full_name LIKE '%.%')`. An unqualified name is never a
  real symbol, but `full_name` is UNIQUE with `ON CONFLICT DO UPDATE`, so silently
  merging every same-named thing in the repo onto one row was the *designed*
  behaviour — the mechanism behind the over-selection fixed below. SQLite has no
  `ALTER TABLE ADD CONSTRAINT`, so the constraint can only arrive by rebuilding the
  table: **an existing `.test-prune.db` is deleted and recreated on first open.**
  Nothing durable is lost — the index is regenerated on the next scan — but that run
  is a full re-index, and any consumer pinned to an older `TestPrune.Core` (or an
  `fshotwatch.cli` that stamps this number) must be upgraded in lockstep. A row that
  violates the constraint now fails with the offending symbol's name, kind and source
  file instead of SQLite's bare constraint name. `kind = 'Module'` is scoped, not a
  loophole: a top-level single-segment module (`module Alpha`) has no qualifier to
  have.
- fix: **Parameters and local `let` bindings are no longer indexed as global symbols.**
  FCS reports a parameter's or local's `FullName` as the bare
  identifier (`name`, `kind`, `source`), and `symbols.full_name` is UNIQUE — so each
  one became a single repo-wide node that every unresolved reference to that
  identifier, in any file, unified onto. In a ~620-test-class consumer repo seven such
  rows selected ~3,000 tests per run; one row named `name` alone had 413 dependents and
  pulled in 2,837 tests, swamping the genuinely changed symbols. It was also
  self-sustaining: a junk seed that never verifies stays in `pending-verification.json`
  and re-seeds every subsequent run. Classification now gates on
  `IsModuleValueOrMember` — true for every module-level binding and type member, false
  for every parameter and local — with a `FullName` dot-qualification check behind it as
  a standing invariant. Expect materially smaller and more accurate selections.
- fix: **Active patterns, operators and interface members are now tracked.**
  An audit of 30 F# binding forms found 11 that never produced
  a usable graph node, in two shapes. Active patterns vanished outright: a *use* is
  reported as an `FSharpActivePatternCase`, which nothing classified, so a module that
  pattern-matched on `(|Even|Odd|)` had no dependency on it at all and editing the
  pattern selected none of the tests exercising it. Operators (`let (+.) a b`),
  backticked names containing dots, and interface members (`interface I with member
  _.Do x = ...`, and `abstract member Do: int -> int` on an interface declared alone)
  survived only as `_extern` placeholders with an empty content hash — indexed enough
  to look right, but no edit could ever change the hash, so no test was ever selected.
  Both shapes were **silent under-selection**: a green run that skipped the relevant
  test looked exactly like one that ran it. Editing any of these forms now selects its
  dependent tests, and an active-pattern use is recorded as `PatternMatches` rather
  than falling through to the `References` catch-all.

## 6.1.1 - 2026-07-20

- fix: **`runProcessWith` bounds the post-exit output drain.** 6.1.0
  bounded the `WaitForExit`, but the success-path drain of stdout/stderr was still an
  unbounded read — and `ReadToEndAsync` only completes when every process inheriting the
  write handle closes it, so a grandchild (an MSBuild worker, VBCSCompiler, a testhost)
  that outlives the direct child wedges the drain forever, silently. The drain is now
  bounded by `drainOutputWithin` (30s wedge-detector, `internal`): on expiry it emits a
  stderr diagnostic and returns the partial capture with a `Completed = false` signal
  rather than blocking. `runProcessWith` keeps its exit-code verdict (a drain-timeout
  never turns a passing run into a failure); a caller for whom the drained text is
  authoritative data can branch on `Completed`. Original read exceptions still surface
  unwrapped.

## 6.1.0 - 2026-07-18

- fix: **`runProcessWith` bounds the test-run wait.** A wedged test
  runner can no longer block the CLI forever: `WaitForExit` is bounded (default
  30 minutes, `TESTPRUNE_TEST_RUN_TIMEOUT_MS` to override), and on expiry the process
  tree is killed with a diagnostic and a POSIX-`timeout(1)`-style exit code 124. On a
  healthy run this behaves exactly like the previous unbounded wait.

## 6.0.0 - 2026-07-15

- feat!: **SchemaVersion 7→8** (`route_handlers` left the core schema). This is the
  number `TestPrune.Core` and `fshotwatch.cli` must agree on: it stamps the cache
  database, and on a mismatch core DELETES and recreates the file. A legacy DB is
  therefore recreated on first open — which is free, because plugin tables are
  re-created on demand by their owner and Falco's routes are re-seeded every run.
  Any consumer pinned to an older `TestPrune.Core` must be upgraded in lockstep.
- feat!: drop the route concept from the public API. HTTP routes are not a core
  concept — core has no business knowing what a URL is — so `RouteHandlerEntry`
  (`AstAnalyzer`), the `route_handlers` table and its five `Database` methods
  (`RebuildRouteHandlers`, `GetAllRouteHandlers`, `GetRouteHandlersForSourceFile`,
  `GetUrlPatternsForSourceFile`, `GetAllHandlerSourceFiles`), and `RouteStore` /
  `toRouteStore` (`Ports`) are GONE. They now live in TestPrune.Falco, which owns
  its own table. BREAKING CHANGE: seed routes with
  `TestPrune.Falco.RouteStore(toPluginStore db)` and construct `FalcoRouteExtension`
  with it.
- feat!: `Ports.PluginStore` + `Ports.toPluginStore` — the generic seam that replaces
  them. An extension whose facts are seeded from outside the AST gets a connection to
  core's cache database (`Database.OpenConnection`) and owns its tables end to end.
  Core owns the FILE: a `SchemaVersion` mismatch deletes and recreates it, dropping
  plugin tables with it, so a plugin must issue `CREATE TABLE IF NOT EXISTS` before
  every use and store only what it can re-derive. Taking a live `Database` is what
  makes the seam safe — the version check has already run before a plugin sees the
  connection.
- feat!: `EdgeEmission` — the shared, tested edge-emission helper every extension
  should build its edges with. `edgesTo` emits an edge from each dependent to the
  specific symbol it depends on across the boundary: scoped to the symbol the fact
  names (`NamedSymbol`), degraded to the whole candidate set when it names none
  (`UnnamedSymbol`) or names one that no longer resolves — never a cross-product,
  never empty. Both shipped bugs came from a plugin hand-rolling this step
  (TestPrune.Falco 2.0.3 over-selected; TestPrune.SqlHydra under-selected). Scoping
  to the direct symbol is safe because `QueryAffectedTests` is a recursive transitive
  reverse-walk, which the docs now say out loud.
- feat!: `Extensions.AffectedTest` moved to TestPrune.Falco — it only ever described
  that extension's route-matched test classes; nothing in core consumed it.
- feat!: `TestPrune.Coverage.ingestCobertura` and `fileCoverageSummary` now return
  named records (`CoverageIngestSummary` = `{ Ingested; Skipped }`, `FileCoverageSummary` =
  `{ Covered; Total }`) instead of anonymous records. BREAKING CHANGE: callers that bound
  the result and read its fields need no change; callers that constructed or annotated
  the anonymous type do. Found by turning TestPrune's own `TP001` analyzer on TestPrune:
  an anonymous record has no stable cross-build name, so impact
  analysis could not see a caller's coupling to these public return shapes — the exact
  blind spot the analyzer ships to warn consumers about.
- feat: new `TestPrune.SafeWalk` — THE one walker for every "files under this root"
  job. Never descends a reparse-point directory (termination is structural, not
  heuristic), prunes `bin`/`obj`/`.git`/`.jj`/`.devenv`/`.direnv`/`node_modules`
  during traversal, and is depth-capped as a belt against cycles that could evade the
  symlink guard. `SearchOption.AllDirectories` is banned in this codebase — route
  every repo-scale walk through `SafeWalk.enumerateFiles`.
- fix: **a file with a misplaced `///` doc comment was silently dropped from the
  symbol graph, so editing it selected NO tests.** `extractResults` refused a file
  whenever `FSharpParseFileResults.ParseHadErrors` was set. Under the
  TransparentCompiler — which is how FsHotWatch's daemon builds its checker
  (`FSharpChecker.Create(useTransparentCompiler = true)`) — FCS sets that flag for a
  file whose ONLY parse diagnostic is **informational**: FS3520 "XML comment is not
  placed on a valid language element" has severity `Info`, and the legacy compiler
  leaves `ParseHadErrors` unset for the very same file. Such a file compiles cleanly
  and its ParseTree is complete, yet it was refused wholesale — contributing no
  symbols, so a change to it had nothing to diff, selected no tests, and the gate
  reported green having run nothing relevant. Silent under-selection: the one failure
  mode a test-impact tool must not have (see `EdgeEmission`). The guard now gates on
  the diagnostics' **severity** (`Error` and nothing else), which is the honest
  question — "is this tree trustworthy?" — rather than on a flag whose meaning varies
  by compiler backend. A real syntax error is still refused. The old message was
  misleading too: it printed *every* diagnostic under the heading "Parse errors",
  which is how an `Info` came to be reported as an error in the first place.
- fix: **directory walks no longer follow symlinks, and no longer hang forever.**
  `discoverTestProjects` used `SearchOption.AllDirectories`, which FOLLOWS DIRECTORY
  SYMLINKS. In a devenv/nix repo the reachable tree contains self-loop symlinks
  (`ncurses-6.6-dev/include/{ncurses,ncursesw} -> .`), and each one DOUBLES the path
  count per level, so a walk that reaches one is effectively non-terminating. This
  silently wedged `fshw check` — observed at 8h36m with no output, no timeout, no
  error and no test ever launched. Scoping the walk to a narrower root (the old
  `discoverTestProjects` comment claimed "only scans tests/ to avoid .devenv/ symlink
  issues") is NOT protection: `tests/*/bin` holds Playwright's Nix-store browser
  symlinks, so the walk escapes into /nix/store from inside `tests/` anyway.

## 5.0.0 - 2026-07-11

- feat!: function-scoped route attribution. `RouteHandlerEntry` gains a
  `HandlerFunction: string option` field and `RouteStore` gains
  `GetRouteHandlersForSourceFile`, so a route can carry the handler function that
  serves it (`None` preserves prior behaviour). Adds a `handler_function` column
  to the `route_handlers` table (SchemaVersion 6→7 — a legacy DB is recreated).
  BREAKING: the new record field means every `RouteHandlerEntry` construction
  site must set `HandlerFunction`.

## 4.3.0 - 2026-06-16

- feat: dependency-fingerprint project-fanout — a dependency/PackageReference
  change selects all tests in transitively-dependent test projects (superset of
  the symbol graph). New `ProjectFanout` module: `ProjectInfo`,
  `computeDependencyFingerprint`, `diffFingerprints`, `affectedTestProjects`,
  `selectTestsForChangedProjects`. Closes the gap where a NuGet/PackageReference
  bump changes a project's binary behaviour without touching any F# symbol, so
  the symbol diff was empty and dependent tests were skipped. Source-symbol edits
  stay symbol-precise; only dependency/binary changes get the project-scoped
  fanout (never a run-all).

## 4.2.3 - 2026-06-16

- fix: editing a test's own body now re-selects that test for impact analysis.
  `Database.QueryAffectedTests` seeded its transitive closure only from symbols
  that *depend on* the changed symbol, so when the changed symbol *was* the test
  method (a node with no incoming edges), it returned no affected tests — the
  edited test was never re-run and a prior failure stayed pinned red. The closure
  now includes the changed symbols themselves, matching the in-memory reference
  store (`InMemoryStore.QueryAffectedTests`), which already did.

## 4.2.2 - 2026-06-12

- fix: the AST impact analyzer preserves dependency edges when two bindings share a
  short name across sibling nested modules (e.g. `let f` in `module A` and `module B`
  in one file). Previously the by-name range maps collapsed to last-write-wins, so a
  use inside one binding could resolve to the other binding's symbol — mis-attributing
  or dropping its dependency edges and silently failing to select affected tests (a
  soundness violation). Each name now maps to a list of ranges and is disambiguated by
  the range containing the use.
- fix: `stripComments` correctly handles triple-quoted strings. A triple-quoted string
  containing an odd number of embedded `"` (e.g. `"""3\" inches"""`) previously desynced
  the single-quote string tracker, letting a trailing `//` comment leak into the content
  hash and producing a phantom "changed" signal on comment-only edits. Triple-quoted
  content is now treated as literal until its closing `"""`.
- fix: failures reading a test process's redirected stdout/stderr surface the original
  IO exception instead of an `AggregateException` wrapper, so the real error type is
  preserved.

## 4.2.1 - 2026-06-07

- feat: `Database.SchemaVersion` is now public. External read-only consumers (e.g.
  FsHotWatch's `fshw dead-code`) probe a live DB's `PRAGMA user_version` against it
  before opening, since `Database.create`'s recreate-on-mismatch self-healing would
  wipe a daemon's symbol graph; a hardcoded copy of the constant silently inverts
  that protection whenever the schema bumps.

## 4.2.0 - 2026-06-05

- feat: `Database.WasRecreated` reports whether the on-disk DB was freshly created,
  or recreated because its schema version no longer matched, as opposed to a
  compatible reopen. This lets consumers detect a silent schema-bump rebuild — where
  the symbol graph is wiped to empty — and invalidate sibling caches keyed to the old
  graph. Without it, an external check-cache that short-circuits re-indexing keeps
  skipping files after a schema change, leaving the symbol graph permanently partial.

## 4.1.0 - 2026-06-04

- feat: TestPrune-native edit-aware coverage. Coverage from a Cobertura report is
  stored in the symbol DB keyed by `(symbol, line_offset)` instead of absolute line,
  so it survives source edits — a symbol that moves keeps its coverage (lines
  re-derive from the symbol's current `line_start`), and a symbol whose content
  changes has its coverage purged on the next `RebuildProjects`. New public API: the
  `TestPrune.Coverage` module (`parseCobertura`, `ingestCobertura`, `emitCobertura`,
  `fileCoverageSummary`) and `Database` members `RecordCoverage`, `RecordCoverageBatch`,
  `FindSymbolContainingLine`, `GetFileCoverage`, `GetCoveredFiles`. Each covered line
  is attributed to its nearest preceding declaration (TestPrune symbols are
  declaration-point markers), and a whole report ingests in a single transaction.

## 4.0.3 - 2026-06-02

- fix: the AST impact analyzer no longer aborts on un-nameable F# symbols. `FSharpEntity.FullName`/`TryFullName` can throw (`NullReferenceException` in compiled projects, `InvalidOperationException` in scripts) on symbols such as anonymous-record projections; these are now caught and the offending edge is skipped, so a single un-nameable symbol degrades impact selection slightly instead of crashing the whole analysis pass.

## 4.0.2 - 2026-05-27

- chore: update external NuGet dependencies — Microsoft.Data.Sqlite 10.0.5→10.0.8,
  Microsoft.SourceLink.GitHub 10.0.201→10.0.300. Pinned FSharp.Core to 10.1.204
  (was floating `10.1.*`, which drifted to 10.1.300 and broke restore: FSharp.Compiler.Service
  43.12.204 hard-pins FSharp.Core to `[10.1.204]`).

## 4.0.1 - 2026-05-04

- fix: detectChanges now filters extern symbols from both sides internally, eliminating phantom diffs on warm FCS restart
- fix: namespace entities are no longer misclassified as Type symbols in tryClassifyEntity, eliminating +1 phantom symbol rows

## 4.0.0 - 2026-04-25
- fix: schema forward-compat. `openCheckedConnection` now treats
  `user_version > SchemaVersion` as "leave it alone" (a newer process wrote
  this DB; older code must not clobber). The `Database` constructor's
  user_version stamp gate flipped from `<>` to `<` so the marker never
  regresses. Without this, an older client opening a daemon's newer DB would
  erase the version marker, then the daemon would hit "no such column" on its
  next flush.
- api: `Database.deleteCacheFiles` (formerly `private deleteDbFiles`) is now
  public. Plugins recovering from schema drift should call this — it deletes
  the main DB along with WAL/SHM sidecars in one shot, preventing the
  "0-byte main DB after partial cleanup" failure mode.
- feat: aggregate-type invalidation (schema v5). Editing any member of a type
  now invalidates consumers that touched any part of it. Module siblings are
  excluded. v4 databases auto-recreate on open.
- feat: direct test-method → fixture-type edges via primary-ctor params and
  `IClassFixture<T>`/`ICollectionFixture<T>` interfaces. Catches fixtures the
  test never references in-body.
- feat: xUnit `[<Collection("name")>]` bridges to `[<CollectionDefinition>]`
  via a synthetic symbol, resolving cross-file through the extern pipeline.
- feat: `[<DependsOnFile>]` / `[<DependsOnGlob>]` (new `TestPrune.Attributes`
  package) seed selection from non-F# file changes. New
  `SelectionReason.FileDependencyChanged` surfaced on `TestSelectedEvent`.
- feat: entity-level attributes are now captured in `symbol_attributes`
  (previously only member-level).
- api: `ImpactAnalysis.selectTests` now takes a `SymbolStore` instead of
  three loose callbacks. Use `Ports.toSymbolStore db` to migrate.
- api: `SymbolStore.GetParentLinksInFile`, `Database.GetParentLinksInFile`.
- api: `AnalysisResult.ParentLinks` field, `SymbolParentLink` record.
- api: `AstAnalyzer.SyntheticCollectionPrefix` literal.

## [3.0.2]
- fix: `openCheckedConnection` now recreates the DB when `user_version = 0`
  *and* the file already contains user tables. The previous `version <> 0 &&
  version <> SchemaVersion` guard treated `0` as a fresh-DB signal, which let
  any pre-versioning DB survive open with its legacy schema intact (CREATE
  TABLE IF NOT EXISTS is a no-op on existing tables). The constructor would
  then stamp the current `SchemaVersion`, and the very next INSERT crashed
  with `"no column named …"` — the plugin-host-level symptom was a permanent
  hang. Regression test `recreates database with user_version=0 and legacy
  tables` covers the fixture.
- revert: removed the `PRAGMA wal_checkpoint(PASSIVE)` added after
  `RebuildProjects` commits. It was introduced to mask a cross-connection
  visibility issue observed in integration tests, but the actual culprit
  was Microsoft.Data.Sqlite's connection pool caching stale reader state,
  which the checkpoint only partially papers over. Consumers that need
  deterministic visibility across in-process connections should call
  `SqliteConnection.ClearAllPools()` (or open a fresh
  `SqliteConnectionStringBuilder.Pooling = false` connection) before
  reading. Removes a per-commit round-trip and a misleading comment.

<!--
  The bullets below document changes that shipped in 3.0.0/3.0.1 but were
  never rolled out of [Unreleased] at the time. Left here for triage — they
  should be moved to the correct versioned section, not to 3.0.2.
-->
- fix: bump `SchemaVersion` 3 → 4. The 3.0.0 release introduced
  `dependencies.source`, `symbol_attributes`, and `symbols.is_extern` under
  the same v3 stamp that 2.0.0 used, so any DB written by 2.0.0 survived
  `openCheckedConnection` (version matched) and then crashed on the first
  INSERT with `"table dependencies has no column named source"`. Plugin
  hosts (FsHotWatch, etc.) deadlocked because the plugin never reached
  terminal status. Bumping forces auto-recreate of any stamped-v3 DB on
  open.
- fix: `RebuildProjects` now preserves incoming dependency edges when a file is re-indexed
  incrementally. The old code did `DELETE FROM symbols WHERE source_file IN (...)` which,
  combined with `ON DELETE CASCADE` on `dependencies.to_symbol_id`, destroyed every edge
  from other (non-re-indexed) files pointing into the re-indexed file's symbols — causing
  `QueryAffectedTests` to return 0 even when dependent tests clearly existed. Now uses
  UPSERT (`INSERT … ON CONFLICT(full_name) DO UPDATE SET …`) to preserve row ids for
  surviving symbols. Orphan cleanup is timestamp-driven: every symbol touched this pass
  gets `indexed_at = now`; a single `DELETE … WHERE source_file IN (…) AND indexed_at < @now`
  sweeps away symbols that genuinely disappeared from source. Extern inserts use a
  conditional UPSERT (`ON CONFLICT DO UPDATE SET indexed_at = excluded.indexed_at WHERE
  symbols.is_extern = 1`) so they bump their own timestamps without overwriting real
  symbols. Includes regression test `re-indexing library file preserves incoming edges
  from non-re-indexed tests`.
- refactor: add `DiffParser.isFsproj` helper; remove duplicated `.fsproj` extension checks
  across `DiffParser`, `ImpactAnalysis`, and `Orchestration`.
- feat: auto-recreate database when schema version is incompatible with current build
- feat: add SharedState dependency kind for cross-test coupling via shared resources
- feat: revise ITestPruneExtension to inject edges into dependency graph
- feat: add TestPrune.Sql package with ReadsFrom/WritesTo attributes and SQL coupling engine
- feat: add TestPrune.SqlHydra package with graph-based SqlHydra query pattern detection
- feat: generic symbol attribute extraction from FCS during analysis (schema v3)
- feat: AutoSqlExtension auto-discovers ReadsFrom/WritesTo from indexed attributes
- feat: show edge source provenance (core, sql, sql-hydra, falco) in status output
- refactor: extract DB placeholder helpers, batch attribute queries, single-pass extraction
- chore: add SourceLink, symbol packages, and NuGet packaging metadata to Sql and SqlHydra projects

## [2.0.0] - 2026-04-11
- feat: cross-project dependency extraction via extern symbols
- feat: add ExternRef SymbolKind for honest extern symbol classification
- feat: add TestExecutor DI record for injectable test execution in runRunWith
- fix: exit code bug where later test project results overwrote earlier failures
- fix: add warnings for unknown DB enum deserialization instead of silent fallback
- refactor: move warnedUnknownKinds to Database instance for proper test isolation
- refactor: simplify extern symbol handling (HashSet dedup, ExternSourceFile constant)
- test: improve coverage across Orchestration (86%→98%), Program (39%→50%), Database, AstAnalyzer
- test: fix test parallelization — Console-mutating tests use xUnit Collection to serialize
- fix: add semantic-tagger.json with CLI under core's shared tag
- fix: trigger docs deploy on release tags, not push to main
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
- feat: add bench tasks to CLAUDE.md

## 0.1.0-alpha.9
- fix: use CLR nested type separator (+) for test classes inside modules
- refactor: extract printTestResult helper, route stderr to eprintfn
- refactor: separate stdout and stderr in TestResult

## 0.1.0-alpha.8
- fix: include type definition ranges in findEnclosing for interface edges
- test: add dead code false positive regression tests
- feat: track type member functions in impact graph + add analysis diagnostics
- test: add regression tests for this self-identifier and cross-project type member chain

## 0.1.0-alpha.7
- feat: surface build stdout on failure for better diagnostics
- feat: print build stderr on failure for better diagnostics
- feat: add process duration logging to runProcess in TestRunner; use async reads to prevent deadlock
- fix: add 10-minute timeout and duration logging to dotnetBuildRunner; use async reads to prevent deadlock
- fix: stop stopwatch before stream drain in runProcess for accurate timing
- fix: serialize GetProjectOptionsFromScript with SemaphoreSlim to prevent FCS corruption
- fix: pass CancellationToken to SemaphoreSlim.WaitAsync; strengthen concurrency test
- fix: resolve relative paths to absolute before passing to FCS in getScriptOptions
- fix: guard null baseDir and empty path in resolveToAbsolute; add edge case tests
- refactor: simplify — remove new on SemaphoreSlim, WHY comments, avoid alloc in resolveReferenceOptions

## 0.1.0-alpha.6
- feat: comment-insensitive and layout-normalized content hashing
- feat: SQLite audit trail — persist analysis events with run ID
- feat: add InMemoryStore and migrate ImpactAnalysisTests to pure in-memory tests
- feat: TestSelection uses SelectionReason DU instead of raw strings
- feat: add SymbolStore/SymbolSink port types and adapter in Ports.fs
- feat: wire AuditSink into orchestration — events flow from pure core through sink
- feat: add --parallelism flag for configurable concurrent analysis
- feat: add AuditSink with MailboxProcessor-based event persistence
- feat: SymbolDiff.detectChanges, DeadCode.findDeadCode, ImpactAnalysis.selectTests now emit AnalysisEvents
- feat: add Domain.fs with typed errors, selection reasons, and analysis events
- refactor: functional core — eliminate shared mutable state, use immutable ProjectResult + fold
- refactor: orchestration uses port types (SymbolStore/SymbolSink) instead of Database directly
- test: add real-source E2E integration tests for SymbolDiff, impact analysis, and dead code

## 0.1.0-alpha.5
- feat: detect cross-file dependencies by analyzing open statements
- test: validate cross-file dependency detection; improve coverage

## 0.1.0-alpha.4
- feat: cross-file dependency detection via open statement analysis (initial)
