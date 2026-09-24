# Changelog — TestPrune

## Unreleased

## 12.0.0 - 2026-09-24

- Read the package version and run pragmas without branches to keep Database.fs above its branch floor
- Refuse an index newer than this schema instead of running older DDL on it


## 11.0.0 - 2026-09-23

- fix!: `index` stores one analysis result per source file instead of one merged
  result per project, so a signature file's edges belong to the signature. Before, a
  project with `.fsi` files credited the signature's edges to its implementation.
  BREAKING CHANGE: `Orchestration.AnalysisOutcome.Analyzed` carries the per-file
  `AnalysisResult list`.

## 10.0.0 - 2026-09-23

- fix!: the first index after upgrading automatically recreates `.test-prune.db`
  (`SchemaVersion` 13 -> 14). A symbol now keeps one source occurrence per
  declaring file, so a signature (`.fsi`) and its implementation no longer overwrite
  each other's indexed state.

## 9.0.0 - 2026-09-17

- fix: `index`, `run`, `status` and `dead-code` flush the audit sink before
  exiting, so the last queued audit events reach `.test-prune.db` instead of being
  lost when the process exits. A flush that times out prints a warning; the
  command's exit code is unchanged.

## 8.2.0 - 2026-09-15

- Docs: de-identify comments that named a private tracker or downstream repo
- Changelog: name the Core fixes released since 8.1.5
- Finish: preserve real declaration ownership in the in-memory store
- Finish: verify multiline signatures and compiler declaration kinds
- Finish: index signature declarations for consumer impact selection
- Merge: land the coverage impact selection domain
- Finish: add coverage impact selection domain


## 8.1.5 - 2026-08-29

- perf: reduce cold-scan latency and retained allocation pressure when TestPrune analyzes
  large FCS syntax trees containing shared object subgraphs.

## 8.1.4 - 2026-08-29

- fix: avoid false incomplete-analysis fallback on large files containing thousands of
  ordinary shallow generic symbol uses, while retaining strict recursive FCS graph
  bounds and fail-closed behavior for genuinely pathological graphs.

## 8.1.3 - 2026-08-29

- fix: apply the defensive FCS traversal budget cumulatively per analyzed file, so
  thousands of individually bounded symbol uses cannot combine into runaway analyzer
  work. Exhaustion remains an index failure with full-suite fallback.

## 8.1.2 - 2026-08-29

- fix: fail closed when a live FCS type graph exceeds the bounded traversal work
  budget. Compiler hosts now receive an index failure and subsequent full-suite
  fallback instead of runaway analysis on recreated branching wrappers.

## 8.1.1 - 2026-08-29

- fix: make indexing atomic per project when any source file cannot be analyzed. A
  failed project keeps its last complete graph while successful sibling projects may
  advance, and the command returns non-zero. A durable incomplete-index marker forces
  subsequent selection to run all tests—even for an upstream-only diff—until a fully
  successful retry clears it. The marker is written before build/index work starts, so
  crashes remain fail-closed; recovery bypasses project and file caches so an unchanged
  dependent that failed last time must really analyze before the marker clears. No
  caller can under-select by ignoring the exit code. Concurrent attempts are
  mutually excluded across cache migration and graph writes and generation-owned, so a
  concurrent attempt fails closed instead of clearing or overwriting the active one;
  selection rechecks its completed generation and
  widens to all tests if an index overlapped its snapshot. Malformed project files also
  fail the index instead of disappearing from it.
- fix!: the first index after upgrading automatically recreates `.test-prune.db`
  (`SchemaVersion` 12 -> 13) for the durable completion protocol. A fresh v13 database
  is conservatively incomplete until its first successful index.

## 8.1.0 - 2026-08-28

- Finish: reuse existing FCS results in Core analysis


## 8.0.0 - 2026-08-28

- docs(test): prepare the blocked FsHotWatch CLI pin upgrade without changing the
  declared toolchain. The contract test keeps the released manifest pin explicit,
  and the runbook requires the macOS watcher fallback to ship before TestPrune bumps
  the pin and runs its full gate.

- feat!: runtime-only behaviour coupling can now select the whole test project that
  previously executed a changed file, even when no AST dependency reaches that
  project. Selection reports the changed file and project as a
  runtime-coverage source.

- feat!: the first index after upgrading rebuilds `.test-prune.db`
  (`SchemaVersion` 11 -> 12) to add project-attributed runtime coverage.

- fix: `run` and `status` now carry every changed path into impact selection instead
  of discarding non-F# paths at the diff-parser boundary.
  `[<DependsOnFile>]` and `[<DependsOnGlob>]` therefore work end to end for snapshots,
  migrations, fixtures, and other declared inputs. Renames check both the old and new
  path, and Git C-quoted names are decoded before matching.

- feat!: **message changes can now re-select tests that assert the old prose without
  referencing its producer (first slice).** The index records decoded,
  non-interpolated string-literal bridges for prose-shaped messages. This intentionally
  widens impact selection where the symbol graph previously had no path.

- feat!: **the first `test-prune index` after upgrading rebuilds `.test-prune.db`
  (`SchemaVersion` 10 -> 11).** The rebuild is automatic and ensures cached files gain
  the new literal-coupling edges.

## 7.0.1 - 2026-08-18

- feat!: **Your `.test-prune.db` is rebuilt on first run again (SchemaVersion 9→10).** Nothing to do — the first `test-prune index` after upgrading is a
  full re-index rather than an incremental one. The bump is what removes rows the 8→9
  constraint was supposed to have kept out: they are attached to no source file, so
  nothing short of a rebuild collects them.
- fix: **Query-builder keywords are no longer indexed as if they were symbols.**
  In `select { for u in users do where (...) }`, F# reports `where` to
  the compiler service as a bare name, so TestPrune indexed one node called `where` and
  hung every query in the repo off it — including queries built with entirely different
  libraries. On a real repo, one node named `select` stood for four unrelated functions
  from three libraries. Each keyword is now recorded under the builder member it actually
  calls, so `test-prune dead-code` and the graph read the way the code does. Test
  selection is unchanged; this is accuracy of attribution, not a change in what runs.

## 7.0.0 - 2026-08-17

- feat: **`run` and `status` honour `[<TestPrune.CompositionRoot>]`.** Mark the symbol
  that wires your application together — a routing table, a DI registration block —
  and `test-prune run` stops treating "the app names this handler" as a reason to run
  every test whose fixture boots the app. Editing one handler in a Falco app measured
  537 selected integration tests before, 4 after. Changing the marked symbol itself
  still selects everything downstream, because rewired composition is what
  host-booting tests verify.

  Nothing to install and nothing to configure: the attribute is matched by type name,
  so declare it in your own code (`type CompositionRootAttribute() = inherit
  System.Attribute()`). A repo that annotates nothing selects exactly what it selected
  before — verified symbol-by-symbol.

  **Read the safety note before annotating.** This is the only setting that makes
  `test-prune` run FEWER tests than the graph implies, so it is the only one that can
  hide a failure. TestPrune.Core's changelog has the measured numbers, the per-project
  fail-safe, and the case where you should not use it at all.
- chore(deps): the bundled native SQLite (`SQLitePCLRaw.lib.e_sqlite3`) moves
  3.50.3 → 3.53.3, clearing GHSA-2m69-gcr7-jv3q. No CLI behaviour change; your
  `.test-prune.db` is unaffected and is not re-indexed.

## 6.1.2 - 2026-08-11

- feat!: **Your `.test-prune.db` is rebuilt on first run (SchemaVersion 8→9).** The index now rejects unqualified symbol names at the database
  level, which SQLite can only add by rebuilding the table. The old cache file is
  deleted and recreated automatically — nothing you need to do, but the first
  `test-prune index` after upgrading is a full re-index rather than an incremental one.
- fix: **`test-prune run` selects far fewer, more accurate tests.**
  Parameters and local `let` bindings were being indexed as if they were global symbols
  under their bare name, so every unresolved reference to that identifier anywhere in
  the repo collapsed onto one node. In a ~620-test-class repo that pulled ~3,000 tests
  into every run regardless of what changed. Those nodes are gone.
- fix: **Editing an active pattern, operator or interface member now selects its tests.**
  These forms were previously dropped from the dependency graph
  with no diagnostic — active patterns entirely, operators and interface members as
  hash-less placeholders no edit could ever invalidate. A change to one of them selected
  nothing, so a green run that skipped the relevant test was indistinguishable from one
  that ran it. This was **under-selection**: expect these tests to start appearing.

## 6.1.1 - 2026-07-20

- fix: **Bounded post-exit output drain.** After a spawned process exits,
  reading its redirected stdout/stderr can still block forever if a grandchild (an MSBuild
  worker, VBCSCompiler, or a testhost) inherited the write handle and outlives the direct
  child. That drain is now bounded. Crucially, on the `jj diff` path a drain wedge now
  surfaces as an `Error` (jj appears wedged) rather than an empty diff: previously a wedged
  drain returned a truncated read that flowed as "no changed files" and ran **zero tests
  green** — silent under-selection. The `dotnet build` and test-run paths keep their exit-code
  verdict on a drain-timeout (partial output plus a diagnostic), never turning a passing run
  into a failure.

## 6.1.0 - 2026-07-18

- fix: **Bounded waits for spawned `dotnet build` and `jj diff`.**
  The index-time solution build is bounded (10 minutes) and `jj diff` runs through
  `runBoundedDiff`; a wedged child process is killed (entire tree) with a diagnostic
  instead of hanging the CLI silently.

## 6.0.0 - 2026-07-15

- fix: `Orchestration.indexProject` built its per-file accumulator as an anonymous record
  that was a field-for-field shadow copy of the existing named `AnalysisResult` — so
  TestPrune's own impact analysis could not see the orchestrator's coupling to that type, and
  editing `AnalysisResult` would not select the tests that reach it through this path. Now
  constructs `AnalysisResult` directly. Found by running TestPrune's `TP001` analyzer against
  TestPrune; no behavior change.

## 6.0.0 - 2026-07-13

- fix: project discovery no longer follows directory symlinks. `findProjectFiles`
  used `SearchOption.AllDirectories`, which traverses symlinked dirs — in a
  devenv/nix repo that reaches /nix/store's self-loop symlinks and never
  terminates. It now walks via `TestPrune.SafeWalk`.
- refactor: the `isOutputPath` post-filter is gone. `SafeWalk` prunes `bin`/`obj`
  during traversal, so a caller-side "/bin/"-substring filter could never fire —
  the walker owns build-output pruning, and callers must not re-filter.
- chore(deps): TestPrune.Core 6.0.0.

## 5.0.0 - 2026-07-11

- chore(deps): TestPrune.Core 5.0.0 — function-scoped route attribution
  (`RouteHandlerEntry.HandlerFunction`, `route_handlers.handler_function`).

## 4.3.0 - 2026-06-16

- feat: `ProjectLoader.parsePackageReferences` extracts a project's
  `<PackageReference>` versions — both inline (`Version="..."`) and CPM-resolved
  from an ancestor `Directory.Packages.props` — feeding TestPrune.Core's new
  dependency-fingerprint project-fanout so a package bump re-runs the dependent
  test projects' tests.

## 4.2.3 - 2026-06-16

- fix: editing a test's own body now re-selects that test, so `test-prune run`
  re-executes a test you just changed instead of skipping it as unaffected (via
  TestPrune.Core's `QueryAffectedTests` seed-inclusion fix).

## 4.2.2 - 2026-06-12

- fix: `test-prune` impact analysis no longer mis-attributes or drops dependency
  edges when two bindings share a short name across sibling nested modules in one
  file, which could silently skip affected tests (via TestPrune.Core).
- fix: comment-only edits next to triple-quoted strings containing embedded `"` no
  longer produce phantom "changed" signals in impact analysis (via TestPrune.Core's
  `stripComments` fix).
- fix: failures reading a test process's redirected output now surface the original
  IO exception instead of an `AggregateException` wrapper (via TestPrune.Core).

## 4.2.1 - 2026-06-07

- chore: release alongside TestPrune.Core (public `Database.SchemaVersion` for external
  read-only compatibility probes). No CLI-facing changes.

## 4.2.0 - 2026-06-05

- chore: bundle TestPrune.Core 4.2.0 (`Database.WasRecreated`, which lets downstream
  consumers invalidate stale sibling caches after a schema-bump DB rebuild). No change
  to CLI behavior.

## 4.1.0 - 2026-06-04

- chore: version bump alongside TestPrune.Core 4.1.0 (which adds edit-aware coverage
  storage). No `test-prune` CLI-facing changes — the coverage API is library-level.

## 4.0.3 - 2026-06-02

- fix: `test-prune` impact analysis no longer crashes on un-nameable F# symbols (e.g. anonymous-record projections) in analyzed sources — the AST walk skips the un-nameable symbol and continues (via TestPrune.Core).

## 4.0.2 - 2026-05-27

- chore: update external NuGet dependencies — Microsoft.Data.Sqlite 10.0.5→10.0.8,
  Microsoft.SourceLink.GitHub 10.0.201→10.0.300, Microsoft.Testing.Extensions.CodeCoverage
  18.6.2→18.7.0. Pinned FSharp.Core to 10.1.204 (was floating `10.1.*`, which drifted to
  10.1.300 and broke restore: FSharp.Compiler.Service 43.12.204 hard-pins FSharp.Core to `[10.1.204]`).

## 4.0.1 - 2026-05-04

- fix: detectChanges now filters extern symbols from both sides internally, eliminating phantom diffs on warm FCS restart
- fix: namespace entities are no longer misclassified as Type symbols in tryClassifyEntity, eliminating +1 phantom symbol rows

## 4.0.0 - 2026-04-25
- feat: indexer captures entity-level attributes and containment edges for
  TestPrune.Core's aggregate-type invalidation. No CLI surface changes;
  databases auto-migrate from v4 to v5 on open.
- chore: initial changelog; bump upstream tool versions
