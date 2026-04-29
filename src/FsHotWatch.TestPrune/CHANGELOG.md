# Changelog — FsHotWatch.TestPrune

## Unreleased

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
