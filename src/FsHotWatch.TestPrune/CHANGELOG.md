# Changelog — FsHotWatch.TestPrune

## Unreleased

- fix: a failing `beforeRun` preflight in the manual `run-tests` command now
  surfaces as a **non-green** verdict. The command ran `executeTests` inside a
  try/with that, on a `beforeRun` throw, returned a command-level JSON error and
  posted **nothing** back — leaving the plugin at its prior (possibly green)
  status, so a concurrent `fshw check` read the daemon aggregate
  (`anyPluginFailed`) as clean and exited **0** even though the preflight-guarded
  suite NEVER RAN. It now posts the SAME `Aborted` lifecycle the impact path
  (`runTestsWithImpact`) builds, driving the plugin to `Failed` with the hook's
  output surfaced, so `check` reads non-green. The two Aborted-lifecycle
  constructions are unified in one helper so they can't drift. (AUTOMATION-68)

## 0.9.0-alpha.1 - 2026-07-03

- fix: a **seeded** `test-impact.db` (copied into a fresh workspace per ADR-010)
  no longer silently under-selects. The fshw-owned freshness sidecar
  (`file-freshness.json`) doesn't travel with the copied DB, so every seeded
  file had no sidecar record and the `detectChanges` call site treated
  "no record" the same as "poisoned" — it BYPASSED the diff and a real edit
  against a seeded row set detected zero changed symbols → zero affected tests →
  a vacuous green gate. `FileFreshness` now classifies stored rows three ways
  (`Clean` / `Dirty` / `Unknown`): an ABSENT record (`Unknown`) over a non-empty
  stored row set is a seeded DB and IS diffed — restoring ADR-010's "a seeded DB
  over-indexes but never serves a stale verdict" guarantee — while an explicit
  `fcsClean=false` record (`Dirty`, possibly-partial cold-scan rows) stays
  bypassed to avoid the phantom "all symbols changed" delta. A genuinely empty
  DB (real cold scan) still falls through to no-diff so it doesn't select the
  whole suite. (AUTOMATION-67)

## 0.8.0-alpha.1 - 2026-07-03

- fix: a transient DB error while resolving covering projects (`QueryAffectedTests` per-symbol lookups) now yields the honest re-runnable `Aborted` lifecycle instead of escaping as a raw framework fault that stranded the run before any test process launched (AUTOMATION-65).

## 0.7.0-alpha.30 - 2026-06-30

- fix: the test gate now defers on artifact **freshness**, not just presence. A
  test project whose compiled assembly EXISTS but predates the newest source is
  no longer run with `dotnet run --no-build` — which would execute stale bits and
  report a pass/fail that doesn't match the sources (the false-green this
  prevents). It is deferred as "waiting on build", exactly the signal a missing
  apphost already produced, so a stale binary can never yield a passing verdict.
  The previous apphost check fired only on a FAILED launch, so a stale artifact
  that exited 0 sailed through as a pass. Runs pre-launch on the canonical
  `<assemblyName>.dll` mtime; mirrors `BuildPlugin.verifyArtifactsFresh` (ADR-008).

## 0.7.0-alpha.29 - 2026-06-24

- chore(deps): pin `SQLitePCLRaw.lib.e_sqlite3` 3.50.3 (clears NU1903 / GHSA-2m69-gcr7-jv3q, High).

## 0.7.0-alpha.28 - 2026-06-19

- fix: the test gate no longer reports a false failure when a test host exits
  non-zero during a dirty shutdown (e.g. the Microsoft.Testing.Platform exit-7
  flake) after writing a clean report. The pass/fail verdict is now derived from
  the CTRF report's summary counts and is authoritative over the process exit
  code (which is only a tie-break when no report exists): a non-zero exit with a
  clean, complete report is GREEN. Previously such a run was reported as "Tests
  failed in <project>" with zero named tests while a re-run came back green.
- feat: a new `TestsErrored` verdict for a run that aborted before producing any
  parseable report (non-zero exit, no results). Distinct from a test failure
  (nothing was shown to fail) and from a pass (nothing was verified) — surfaced
  as an honest "errored — re-run" diagnostic, never green, and never cached.
  `--only-failed` re-runs it.
- feat: a per-project `reportVerificationFormat` setting (`.fshw.json`: `auto` |
  `ctrf` | `off`) scoping CTRF report injection. `auto` (default) injects
  `--report-ctrf` only for a runner detected as xUnit.v3 (an unsupported
  `--report-*` flag is fatal), else falls back to the dotnet heuristic; `off`
  keeps the exit code authoritative for custom runners.
- fix: per-test flakiness tracking now records runs — the CTRF parser read a
  top-level `tests` array, but real Microsoft.Testing.Platform / xUnit.v3 reports
  nest it under `results.tests`, so it had been silently recording nothing.

## 0.7.0-alpha.27 - 2026-06-17

- docs: fix a stale `fshw test` reference (now `fshw check`), document missing config/`create` fields, and add an early-alpha status note in the README.

## 0.7.0-alpha.26 - 2026-06-16

- feat: daemon re-runs dependent tests on a dependency-fingerprint change (new
  `PluginCtx` project-graph accessor), closing the zero-affected skip for
  dependency-only changes. On each `BuildCompleted`, every test project is
  fingerprinted from the content hashes of its referenced projects' compiled
  DLLs plus its own direct `PackageReference` versions (`DependencyFanout`); a
  project whose fingerprint moved since the last build is force-run in full,
  unioned with the symbol-precise selection. A NuGet/PackageReference bump that
  changes binary behaviour without touching an F# symbol (e.g. CommandTree
  0.6.3 → 0.7.0) now re-runs the dependent tests instead of being skipped.
  Bundles TestPrune.Core's `ProjectFanout`.
- fix: a failing test in a daemon run now has its NAME surfaced in the console
  output, not just the on-disk `.fshw/test-runs` log (which CI discards). The
  failure-summary matcher checked for a `failed ` prefix without trimming, so any
  capture path that indented the line (varies by MTP version) was missed —
  producing the contradictory `0 test(s) failed:` alongside `failed: 1` with no
  test name. Now matched on the trimmed prefix (covering `failed (canceled) …`
  timeout-cancellations, the documented under-load flake class), with a backstop
  that dumps the output tail when a run fails but no per-test line parses — so a
  failure is never swallowed into "0 test(s) failed".

## 0.7.0-alpha.25 - 2026-06-16

- fix: a directly-edited test now re-selects itself. Bundles TestPrune.Core
  4.2.3, whose `QueryAffectedTests` now includes the changed symbols themselves
  in the affected set — so editing a test's own body re-runs that test instead
  of leaving a prior failure pinned in the needs-verification queue (FsHotWatch
  ISSUE B). Previously a test method, having no incoming edges, selected zero
  tests when it was itself the change.

## 0.7.0-alpha.24 - 2026-06-12

- fix!: the test gate can no longer go green without the tests having actually
  run ("false green"). A durable needs-testing queue
  (`.fshw/test-prune/pending-verification.json`) records every changed symbol
  until a test run that COVERED it completes green. Concretely: runs that abort
  (e.g. a failing `beforeRun` hook) or fail no longer absorb the symbols they
  never verified; an Aborted run reports Failed instead of green; "no affected
  tests — skip" is gated on the persisted queue being empty; zero projects ran
  with a non-empty queue reports "tests did not run" instead of green; a cached
  green `TestRunCompleted` can only replay for a state whose queue is empty;
  and a daemon restart re-flags anything still unverified. Breaking for
  plugin-message consumers: `TestPruneMsg.TestsFinished` now carries the
  launch-time queue snapshot (`TestRunLaunch`).
- chore: the pending-verification sidecar persists once per analysis batch (at
  the flush chokepoint, before the snapshot advance) rather than on every
  FileChecked — same crash-safety direction (over-testing), far fewer disk
  writes.
- chore: bump TestPrune.Core to 4.2.2 and TestPrune.Falco to 2.0.2.

## 0.7.0-alpha.23 - 2026-06-11

- fix: the `run-tests` command (`fshw test-rerun --filter-*`) now reports a
  filtered run that matched ZERO tests DISTINCTLY instead of as a silent pass. A
  zero-match-under-filter project result is tagged with a stable marker; the
  command surfaces a run-level `noTestsMatched` flag and a per-project
  `no-tests-matched` status when every project matched nothing. It also no longer
  bails instantly when a background run holds the test slot — it waits (bounded)
  for that run to finish so the explicit force-rerun always executes, and reports
  a distinct `busy` status only if a run is still in progress after the wait.

- feat: `tests.dependsOn` (repo-root-relative globs) salts the test cache key
  with a content hash of the matched EXTERNAL inputs — DB migrations, generated
  files, schemas — that the symbol-diff merkle can't see. Editing such a file
  (e.g. a migration that changes the test DATABASE schema but no test SOURCE)
  now changes the BuildCompleted cache key → cache miss → genuine test re-run,
  instead of replaying a stale verdict. Empty / absent `dependsOn` keeps the key
  byte-identical to before (existing on-disk caches keep hitting); missing files
  and zero-match globs contribute no salt. `TestPrunePlugin.create` gains a
  trailing `dependsOn: string list` parameter (pass `[]` for no external deps).

## 0.7.0-alpha.22 - 2026-06-09

- fix: a filtered test run (`run-tests` / `test-rerun --filter-class` / `--filter-trait`)
  no longer reports projects that contain no matching test as FAILED. The raw
  passthrough filter is fanned out to every test project; a project with no test
  matching the filter runs zero tests and the runner exits non-zero (Microsoft
  Testing Platform exit code 8, "Zero tests ran"), which was interpreted as a test
  failure — producing bogus aggregates like "5 failed: Analyzers, Build, Database,
  Unit" when only one project actually had the targeted class. Such a zero-match
  filtered run is now classified as passed/skipped (like a template-filtered project
  with no affected classes), contributing no coverage. Detection is structural (the
  canonical exit code) with a text fallback for runners that exit non-zero without
  emitting code 8. Gated on `wasFiltered`, so an UNFILTERED project that runs zero
  tests (empty suite, misconfigured runner) still surfaces as a failure.

## 0.7.0-alpha.21 - 2026-06-08

- fix: a FAILED test verdict is no longer served from the task cache as a current
  result ("green tree read as red"). The cache key for a completed test run is
  derived from changed symbols + commit, which does NOT pin the test OUTCOME — so a
  failing run and a later passing run on the same tree shared a key. Caching the
  failure let the framework replay a stale red on a now-green tree, surviving daemon
  restarts via the on-disk cache. The plugin now returns a `None` cache key for any
  non-passing `TestsFinished`, making failures uncacheable (always re-run on the next
  matching event); fully-passing runs still cache for the green fast-path. The
  BuildCompleted merkle salt is bumped `v1`→`v2` so entries written by the prior
  failure-caching code are orphaned without a manual cache wipe.


## 0.7.0-alpha.20 - 2026-06-07

- chore: bump TestPrune.Core 4.2.0 -> 4.2.1 (picks up the now-public `Database.SchemaVersion`). No behavior change.

## 0.7.0-alpha.19 - 2026-06-05

- fix: when a schema bump recreates the TestPrune DB, the plugin now clears the FCS
  check-cache so every file re-indexes on the next scan. Previously the recreated DB
  started empty while the on-disk check-cache survived, so cache-hit files were skipped
  and never re-emitted their symbols — leaving the symbol graph (and therefore coverage)
  permanently partial until a manual cache wipe. Keyed on the new `Database.WasRecreated`
  signal from TestPrune.Core 4.2.0.

## 0.7.0-alpha.18 - 2026-06-04

- fix: cold-start coverage no longer clobbers a prior good emission. On the first run
  after a schema bump recreates the TestPrune DB, the daemon is still indexing, so a
  covered file may not have symbols yet and its coverage lines can't be attributed.
  `ingestAndEmitCoverage` now detects an incomplete symbol graph (most lines unmapped)
  and SKIPS the emit rather than overwriting prior coverage with a partial snapshot; the
  DB persists and max-merges, so a later warm run emits in full.

## 0.7.0-alpha.17 - 2026-06-04

- feat: coverage is now stored end-to-end in the TestPrune DB — edit-aware and
  symbol-relative — instead of a blind per-line max-merge. After each test run the
  plugin ingests each project's raw cobertura into the DB (via TestPrune.Core's new
  coverage API) and emits the full DB once to a single shared cobertura, which the
  coverage plugin checks. This eliminates the stale-line accumulation that inflated
  per-file coverage baselines over successive edits. Requires TestPrune.Core 4.1.0.
- refactor: removed the line-keyed `CoverageMerge` parse/merge/emit logic (kept only
  the artifact filename constants); the TestPrune DB is now the single source of truth.

## 0.7.0-alpha.16 - 2026-06-02

- fix: cold-start apphost-missing is no longer a spurious test FAILED — detected structurally (File.Exists on the apphost) and surfaced as "waiting on build" with a one-shot retry.
- fix: `fshw errors` / the aggregate verdict now reflects only the most recent completed test cycle — superseded stale failures are cleared each cycle.
- fix: a partial/aborted test run can no longer lower a coverage baseline.
- chore: bump TestPrune.Core 4.0.2 → 4.0.3 (AST impact analyzer no longer aborts on un-nameable F# symbols such as anonymous-record projections).

## 0.7.0-alpha.15 - 2026-05-28

- chore: bump TestPrune.Core 4.0.1 → 4.0.2 (picks up the backtick-named-test-method shortName fix).

## 0.7.0-alpha.14 - 2026-05-04

- feat: `run-tests` IPC command (invoked by `fshw test`) now routes through the event machinery, emitting `TestRunStarted` → `TestRunCompleted`. Plugins subscribed to `TestRunCompleted` (e.g. `CoveragePlugin`) now observe manually-triggered runs identically to daemon-triggered ones. No API change.

## 0.7.0-alpha.13 - 2026-05-04

- fix: daemon restart with no source edits no longer spuriously re-runs tests — `detectChanges` now filters extern symbols from both sides internally (TestPrune.Core ≥ 4.0.1), eliminating phantom symbol diffs that caused every warm restart to invalidate the full test suite
- refactor: remove redundant `currentForFile` pre-filter from `TestPrunePlugin`; `detectChanges` handles extern filtering internally

## 0.7.0-alpha.12 - 2026-04-29

### Changed

- **BREAKING: TestPrune no longer second-guesses build success.** `BuildSucceeded` is now treated as a contract: artifacts are guaranteed fresh by BuildPlugin's post-build verification. TestPrune dispatches every project on `BuildSucceeded` and drops all skip-on-stale logic. With the dirty-bit handoff gone, `create` no longer takes `dirtyTracker` or `stalenessCheck` — drop the 8th and 9th positional arguments.
- **BREAKING:** `create` no longer takes `getCommitId`. The parameter was unused under §2a's content-merkle keys; removed.

### Removed

- `isStaleProject` / `staleBinaryEntry` and the skip-on-stale code path.
- Stale-binary warning re-emit block in the `TestsFinished` handler.
- `adaptiveTimeout` helper and the `lastSuccessfulElapsed` map (only meaningful for stale-manual recovery, which no longer exists).
- Manual-run-tests deadlock workaround (no skip → no deadlock).

### Added

- **Per-project elapsed time** is now captured on every test run and round-tripped through `FileTaskCache`. Surfaced via the new `TestResult.elapsed` accessor and the `elapsedMs` field on `test-results` JSON output (per-project entry). When 2+ projects run, the run summary now also names the slowest (`"3 passed, 0 failed in 3 projects (selected: no, slowest: ProjA 1.2s)"`) so a bottlenecked project is visible from the plugin status line without querying JSON.
- **Per-test flakiness tracking.** New `FsHotWatch.TestPrune.Flakiness` module captures individual test pass/fail/duration records from CTRF reports emitted by Microsoft Testing Platform runners (xUnit v3, etc.). Per-run history is persisted to `.fshw/test-history.json` (capped at 20 runs per test). The new `flaky-tests` IPC command returns the top-K tests by flakiness score, computed as `transitions / (n - 1)` over the recent history with skipped runs filtered out. CTRF generation is opt-in via the `dotnet`-vs-non-dotnet command discriminator — non-MTP test runners (echo/sleep stubs in unit tests) are unaffected.

### Fixed

- **Cold-start cache bypass.** TestPrunePlugin's `BuildCompleted` cache key now returns `None` until the first `TestsFinished` in the daemon session, so a stale on-disk cache entry from a prior session can't pre-empt the cold-start full-suite run. Mutable plugin-level refs use `Volatile.Read`/`Volatile.Write` for thread safety.

## 0.7.0-alpha.11 - 2026-04-26

### Fixed

- **`RerunQueued` no longer drops the previous run's outcome from history.** The branch that kicks off a queued rerun now records the just-finished run's terminal Completed/Failed status before starting the rerun, so both runs appear in plugin history.

## 0.7.0-alpha.10 - 2026-04-25

### Changed

- **Timeout outcomes are now structural.** Per-project timeouts produce
  `TestResult.TestsTimedOut(output, after, wasFiltered)` instead of a regular
  `TestsFailed` whose output happens to start with `"timed out after Ns"`.
  Plugin's run-completion logic (terminal status, `onlyFailed` re-run filter,
  failed-projects list) now matches the variant directly. The `formatTestResultsJson`
  command surfaces a `"timed-out"` status.
- `runProcessWithTimeout` is consumed via the new `ProcessOutcome` DU; the
  string-prefix heuristic is gone.
- Emit a `"primary"` subtask label that differentiates filtered vs full suite
  runs (`running N selected test projects` vs `running full suite (N projects)`).
  Terminal summary is now `P passed, F failed in N projects (selected: yes|no)`,
  leveraging the existing `TestResult.WasFiltered` flag.

### Added
- `RanFullSuite: bool` field on the `TestRunCompleted` event — `true` iff
  every project in the run executed without an impact filter. Derived from
  per-project `TestResult.WasFiltered`; downstream consumers (e.g.
  FileCommand's `afterTests`) use it to gate baseline-affecting actions.
- **Partial-run coverage merging.** TestPrune now emits coverlet's native JSON
  format (not Cobertura) per test project. Full runs write
  `coverage.baseline.json`; impact-filtered runs write
  `coverage.partial.json` and then merge it with the baseline (per-line max) to
  produce `coverage.cobertura.xml` for downstream gating (e.g. `coverageratchet`).
  Partial runs without a baseline skip cobertura emission entirely (bootstrap);
  run a full test once to establish the baseline.
- `TestResult.WasFiltered`: per-project boolean on `TestsPassed`/`TestsFailed`
  indicating whether impact analysis reduced the run for that project.
  Downstream consumers can distinguish full vs partial results without
  inspecting the command args.
- `fs-hot-watch coverage refresh-baseline` CLI command: deletes
  `coverage.baseline.json` and `coverage.partial.json` for every configured
  test project so the next full run rebuilds coverage from scratch.

### Caveat
- Coverlet's merge keys by file path + line number, not by content hash. File
  edits between baseline and partial may misattribute hits at the line level;
  coverage % stays correct. Revisit with per-test attribution if that noise
  becomes an issue.

### Breaking
- `TestPrunePlugin.create`'s `coverageArgs: (string -> string) option` is
  replaced by `coveragePaths: (string -> CoveragePaths option) option` — the
  caller supplies per-project baseline/partial/cobertura file paths and
  TestPrune composes the coverlet args + merge step internally.
- `TestResult.TestsPassed` and `TestResult.TestsFailed` each gain a
  `wasFiltered: bool` second field. Consumers pattern-matching on
  `TestsPassed output` must update to `TestsPassed(output, _)`.

## 0.7.0-alpha.9 - 2026-04-23

### Changed
- **BREAKING:** The `TestCompleted` event is replaced by a three-event lifecycle (see FsHotWatch CHANGELOG): `TestRunStarted` → `TestProgress` × N → `TestRunCompleted`. TestPrune emits `TestRunStarted` once at the top of `executeTests`, a `TestProgress` per group as it completes (with `NewResults` as a delta keyed by `RunId`), and `TestRunCompleted` once at the end (with the full cumulative `Results` and a `TestRunOutcome`). Cache replay goes through the same path — cached runs replay all three events with a fresh `RunId` so downstream dedup still works.
- Motivation: before this change, a single slow or hanging group (e.g. integration tests) forever-blocked every `TestCompleted`-triggered downstream (coverage ratcheting, `fileCommands afterTests`, etc.) even though the groups the downstream actually depended on had completed long ago. The new lifecycle lets subscribers fire as soon as their required projects have completed without waiting for the rest of the run.
- Abort path now emits `TestRunStarted` + `TestRunCompleted(Aborted reason)` instead of just a dummy `TestCompleted`, so subscribers see a coherent end to the run.

- chore: bump upstream tool versions

## 0.7.0-alpha.8 (2026-04-22)

### Changed

- **BREAKING**: Bump `TestPrune.Core` 2.0.0 → 3.0.2. Adopts the revised
  `ITestPruneExtension` interface: extensions now implement `AnalyzeEdges`
  (returning `Dependency list` to inject into the graph) rather than
  `FindAffectedTests`. Extension-contributed edges are written to the DB
  via `RebuildProjects` before `QueryAffectedTests` so impact traversal
  unifies AST-based and extension-based dependencies in a single pass.
  3.0.2 also closes the pre-versioning stale-DB hole (`openCheckedConnection`
  now recreates any DB where `user_version` reads 0 *and* user tables
  already exist), so combined with the plugin-side stuck-state fix below
  the schema-drift hang is prevented at both layers.
- `AnalysisResult` construction now passes `Attributes` through from the
  analyzer (new field in `TestPrune.Core` schema v3).

### Fixed

- **Stuck-state bug**: the synchronous `flushAndQueryAffected` call sites in
  `BuildCompleted` and `TestsFinished (RerunQueued)` ran outside the async
  try/with and had no net, so a DB hiccup would leave the plugin permanently
  pinned in `Running` with no work dispatched. Both branches now wrap the
  flush in a try/with that reports `PluginStatus.Failed`, transitions back
  to `TestsIdle`, and leaves the plugin responsive to the next event.
- **Schema-drift self-heal**: when a flush fails with SQLite "no such column"
  / "no column named" (stale cache DB from a previous `TestPrune.Core` schema
  version), the plugin deletes the offending DB file and logs a warning. The
  next run rebuilds from scratch — the cache is derivative and safe to
  regenerate. The caller no longer has to know which file to `rm`.
- `affected-tests` command now updates on every `FileChecked` event
  rather than waiting for the next `BuildCompleted`. Each file check
  re-queries `QueryAffectedTests` against the currently-persisted DB
  state so consumers can observe impact changes incrementally. Fix
  depends on `TestPrune.Core 3.0.0`'s UPSERT row-id preservation and
  post-commit WAL checkpoint.

## 0.5.0-alpha.1 (2026-04-12)

### Changed

- Bump `TestPrune.Core` 1.0.1 → 2.0.0 — adds cross-project extern symbol support via `projectName` parameter in `analyzeSource`
- `buildFilterArgs` changed from private to internal for testability
- Add `InternalsVisibleTo` for FsHotWatch.Tests

### Fixed

- Comment-only source changes no longer add the file to `ChangedFiles` — only genuine AST changes (non-empty `changedNames`) propagate to extension-based tests (e.g. Falco route matching)

---

## 0.3.0-alpha.1 (2026-04-08)

Infrastructure release. No public API changes.

- Bump internal tooling: `coverageratchet` 0.10.0-alpha.1, `syncdocs` 0.10.0-alpha.1, `fssemantictagger` 0.10.0-alpha.1, `fsprojlint` 0.7.0-alpha.1

---

## 0.2.0-alpha.1 (2026-04-07)

Packaging and infrastructure release. No API changes.

- Add MIT license; add SourceLink; replace bespoke scripts with shared NuGet tools and reusable CI workflows
- Bump `TestPrune.Core` 0.1.0-beta.1 → 1.0.1

### Migration from 0.1.0-alpha.3

- Update `TestPrune.Core` dependency to 1.0.1 (check for API changes in that library)

---

## 0.1.0-alpha.2 (2026-03-28)

- Fix: move `ReportStatus` to `TestsFinished` handler to eliminate race condition

---

## 0.1.0-alpha.1 (2026-03-21)

Initial alpha release.

- Test impact analysis via symbol dependency graph
- Test execution with configurable test project configs
