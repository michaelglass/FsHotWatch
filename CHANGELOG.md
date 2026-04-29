# Changelog

All notable changes to FsHotWatch packages are documented here.

## Unreleased

### BuildPlugin owns artifact-freshness; remove ProjectDirtyTracker

#### Added
- `FsHotWatch.Build.BuildOutcome.BuildArtifactsStale of stale: StaleArtifact list * output: string` — new variant emitted when MSBuild's incremental cache reports success but per-project canonical DLLs are missing or older than their newest source file. Post-build verification runs in the async worker after `decideBuildOutcome` returns `BuildPassed`. Downstream plugins (TestPrune, etc.) can therefore trust `BuildSucceeded` as a guarantee of artifact freshness.
- `FsHotWatch.Build.StaleArtifact` / `StaleReason` types carry the structured diagnostic so cache replay reproduces the same per-project messages deterministically.
- Core `IProjectGraphReader` gained `GetTargetFramework`, `GetCanonicalDllPath`, and `GetMaxSourceMtime` accessors so `BuildPlugin.verifyArtifactsFresh` (and other consumers) can probe canonical paths without re-opening .fsproj files.

#### Removed
- **BREAKING:** `FsHotWatch.ProjectDirtyTracker` module — the dirty-bit handoff between BuildPlugin and TestPrunePlugin is gone. With staleness enforced inline by post-build verification, the heuristic dirty tracker has no consumers (`markDirty` / `clearFreshProjects` / `isStaleProject` removed).
- **BREAKING:** `BuildPlugin.create` no longer takes `dirtyTracker` (drops the 9th positional argument). `TestPrunePlugin.create` no longer takes `dirtyTracker` or `stalenessCheck` (drops the 8th and 9th arguments).
- TestPrune skip-on-stale code path, stale-binary warning re-emit, and the manual-run-tests deadlock workaround. With the freshness contract upstream, TestPrune dispatches every project on `BuildSucceeded`.
- `adaptiveTimeout` helper and `lastSuccessfulElapsed` map (only meaningful for stale-manual recovery, which no longer exists).
- `FsHotWatch.Cli.DaemonConfig.canonicalDllPath` — moved to `IProjectGraphReader.GetCanonicalDllPath` in the core lib.

### Naming normalized to `fshw`

#### Changed
- **BREAKING:** CLI command renamed from `fs-hot-watch` to `fshw` (`ToolCommandName` + IPC pipe-name prefix).
- **BREAKING:** Config file renamed from `.fs-hot-watch.json` to `.fshw.json`. Existing repos must rename.
- **BREAKING:** State directory consolidated from `.fs-hot-watch/` to `.fshw/` — pid, lock, and config-hash now live alongside the existing `cache/`, `errors/`, `logs/`, `test-runs/`, and `test-impact.db`. One directory for everything fshw writes. Existing daemons must be stopped and the legacy `.fs-hot-watch/` directory deleted.

### Drop jj reliance from plugin cache keys; content-hash FCS cache keys

#### Added
- `FsHotWatch.CheckCache.DiagnosticSignature` record (`StartLine/StartColumn/ErrorNumber/Severity/Message`) and `hashDiagnosticSignatures` — extracted from `fcsCheckSignature` so the hashing/sorting logic is unit-testable without a live `FSharpCheckFileResults`.

#### Changed
- `TimestampCacheKeyProvider.GetFileHash` now hashes file **content** (SHA-256) instead of metadata (path + size + mtime). Closes a correctness gap where two files with the same size + mtime but different bytes would collide. Class name preserved for backward compatibility; behavior matches the original "ls-tree merkle hash" design intent.
- `FileCommandPlugin` cache key migrated from `optionalSaltedCacheKey getCommitId` to a pure `merkleCacheKey` over `(command, args, arg-file SHA-256s)`. Editing a config file referenced in `args` (e.g. `coverage-ratchet.json`) now invalidates cached output even when the working-copy commit_id is unchanged.
- `FsHotWatch.Fantomas` `FormatCheckPlugin` cache key migrated from `optionalCacheKey getCommitId` to a content-merkle of `(file path, file source)` per `FileChanged` event.

#### Removed
- **BREAKING:** `getCommitId` parameter dropped from all six plugin `create` signatures (`BuildPlugin`, `TestPrunePlugin`, `AnalyzersPlugin`, `LintPlugin`, `FormatCheckPlugin.createFormatCheck`, `FileCommandPlugin`). New positional orders are documented in each package README.
- **BREAKING:** `FsHotWatch.JjHelper` module (`JjScanGuard`, `JjScanDecision`, `getWorkingCopyCommitId`, `getChangedFiles`) — the scan-skip-when-commit-unchanged optimization saved <5ms on a no-op trigger and was the only runtime jj reliance.
- **BREAKING:** `FsHotWatch.CheckCache.JjCacheKeyProvider` — was a stub that delegated to `TimestampCacheKeyProvider`; only role was as a marker for `Daemon.fs` runtime type-test.
- **BREAKING:** `Daemon.DaemonOptions.EnableJjScanGuard` field.
- **BREAKING:** `DaemonConfig.JjFileBackend` variant. The string `"jj"` is still accepted as a legacy alias and falls back to `FileBackend`.
- **BREAKING:** `force` parameter removed from the Scan API: `Daemon.ScanAll(?force)` → `ScanAll()`, `DaemonRpcConfig.RequestScan: bool -> unit` → `unit -> unit`, `IpcClient.scan pipeName force` → `IpcClient.scan pipeName`. The CLI `scan --force` flag is gone (had been a no-op since the scan-guard was deleted).

### TestPrune: per-test flakiness + per-project elapsed

#### Added
- `FsHotWatch.TestPrune.Flakiness` module: parses CTRF (Common Test Report Format) JSON from Microsoft Testing Platform runners (xUnit v3, MSTest v3+), persists per-test rolling history to `.fshw/test-history.json` (capped at 20 runs per test), and computes a `transitions / (n - 1)` flakiness score with skipped runs filtered out.
- `flaky-tests` IPC command — returns the top-K flakiest tests with name, score, and run count. CTRF generation is opt-in via a `dotnet`-vs-non-dotnet command discriminator so non-MTP runners (echo/sleep stubs in unit tests) are unaffected.
- **BREAKING:** Core `TestResult` DU widened with `elapsed: TimeSpan` on all three constructors (`TestsPassed`, `TestsFailed`, `TestsTimedOut`). Round-tripped via a new `elapsedSeconds` field in `FileTaskCache`; older cached entries deserialize as `TimeSpan.Zero`. New `TestResult.elapsed` accessor; `elapsedMs` field on per-project `test-results` JSON output.
- TestPrune run summary now names the slowest project when 2+ projects ran (e.g. `"3 passed, 0 failed in 3 projects (selected: no, slowest: ProjA 1.2s)"`) so a bottlenecked project surfaces from the plugin status line without querying JSON.

### CLI: warn when FileCommand plugin inputs go stale

#### Added
- Run-once output now scans each `FileCommand` plugin's args for files modified after the plugin's last successful run and emits `cached output may be stale → run fshw rerun <plugin>`. Defense-in-depth alongside the FileCommand cache-key salt fix. New helpers: `FsHotWatch.Cli.RunOnceOutput.PluginRunInfo`, `detectStalePluginInputs`, `formatStalenessWarning`; `FsHotWatch.FileCommand.collectArgFiles`, `argsStalerThan`.
- Cold-start cache bypass for `BuildPlugin`, `TestPrunePlugin`, and `FileCommandPlugin` — `CacheKey` returns `None` until each plugin's first work completes in the daemon session, so a stale on-disk entry from a prior session can't pre-empt the cold-start replay.

### Analyzers: failOnSeverity threshold

#### Added
- `failOnSeverity` parameter on `AnalyzersPlugin.create` — promotes analyzer diagnostics at or above the given severity to error. Default `Hint` (everything is fail-worthy). Configurable via `analyzers.failOnSeverity` in `.fshw.json`; unknown strings are warned and ignored.
- `FsHotWatch.ErrorLedger.DiagnosticSeverity.order` — total order on `Error/Warning/Info/Hint` for severity-threshold comparisons. `fromString` now returns `DiagnosticSeverity option` instead of throwing on unknown strings.

### MSBuild orphan workers fixed at the ProcessHelper layer

#### Added
- `FsHotWatch.ProcessHelper.isDotnetCommand` and `mergeDotnetEnv` (public).
- `runProcessWithTimeout` now injects `MSBUILDDISABLENODEREUSE=1` automatically whenever the command is `dotnet` (or `dotnet.exe`) and the caller hasn't set the key. Eliminates orphan `MSBuild.dll /nodemode:1` workers across daemon-spawned builds without requiring per-plugin opt-in. See `docs/msbuild-node-reuse-bug.md` for the reproduction (verified: 5 builds → 22 orphan workers without env, single-generation with).
- `FsHotWatch.PluginFramework.PluginCtxHelpers.reportOrClearFile` — collapses the per-file "if entries.IsEmpty then ClearErrors else ReportErrors" idiom shared by Lint, Analyzers, and FormatCheck.

### TestPrune: rerun history + IPC error formatting + silent-build diagnostic

#### Added
- TestPrune's `RerunQueued` branch now records the just-finished run's terminal Completed/Failed status before kicking off the rerun. Without this, the previous run's outcome was silently dropped from history.
- `FsHotWatch.Build.BuildPlugin.formatSilentFailureDiagnostic` — surfaces exit code, output size, and "Time Elapsed" tail when `dotnet build` exits non-zero with no parseable diagnostics (typically MSBuild bailing during evaluation/restore).
- CLI: `unwrapIpcException` peels `AggregateException` wrappers so `dotnet fs-hot-watch` surfaces the underlying OOM / Timeout instead of "One or more errors occurred".

### Per-task timeouts (cross-package)

#### Added
- `timeoutSec` configuration at three levels:
  - Top-level (`"timeoutSec": 120`) — default for plugins/projects that don't set their own.
  - Per-build-entry (`build.timeoutSec`) and per-file-command entry (`fileCommands[].timeoutSec`).
  - Per-test-project (`tests.projects[].timeoutSec`).
- `FsHotWatch.Events.RunOutcome.TimedOut of reason: string` — new variant recorded when a plugin's configured timeout fires.
- `FsHotWatch.ProcessHelper.ProcessOutcome` DU (`Succeeded` / `Failed of exitCode * output` / `TimedOut of after * tail`) replaces the historical `bool * string` return on `runProcessWithTimeout` / `runProcess`. Callers pattern-match instead of parsing a magic prefix from the output.
- `FsHotWatch.ProcessHelper.WorkOutcome<'a>` DU (`WorkCompleted` / `WorkTimedOut of after`) replaces `Result<'a, string>` on `runWithTimeout`.
- `FsHotWatch.Events.TestResult.TestsTimedOut of output * after * wasFiltered` — distinguishes timeout-killed test runs from regular failures. `TestResult.isTimedOut` helper added.
- `PluginCtx.CompleteWithTimeout reason` — lets a plugin flip its terminal outcome to `TimedOut` without introducing a new `PluginStatus` case. Backed by `PluginHostServices.SetNextTerminalOutcome` + `PluginActivity.SetNextTerminalOutcome`.
- Renderer: distinct `⏱` glyph in compact/verbose modes; `timed-out` token with `summary="timed out: …"` in agent mode.

#### Removed
- **BREAKING:** `FsHotWatch.ProcessHelper.TimedOutPrefix` literal. Pattern-match `ProcessOutcome` / `TestResult.TestsTimedOut` instead.

#### Behavior
- On timeout the daemon kills the process tree, records `TimedOut`, and keeps running. The next change retriggers normally.
- Plugins wired: `TestPrune` (per-project), `Build` (per build entry), `FileCommand` (per entry). Lint / Analyzers / Fantomas are in-process and use `Timeout.InfiniteTimeSpan` by default; timeout wrapping for those runs on a future change.

### Daemon shutdown reaps in-flight child processes

#### Added
- `FsHotWatch.ProcessRegistry` — per-daemon `AsyncLocal`-scoped registry of live `Process` handles. `Daemon.Dispose` calls `processRegistry.KillAll()` so `dotnet fs-hot-watch stop` no longer leaves orphan dotnet test runners (and their playwright drivers) competing with the next start.
- `Daemon.ProcessRegistry` (internal) — used by tests to track child processes against a daemon's registry without going through `runProcessWithTimeout`.

#### Fixed
- `runProcessWithTimeout` now registers the spawned process and unregisters in a `finally` block so daemon shutdown can tear it down even mid-call.

### Build plugin: skip-for-test-files-only no longer races FCS

#### Fixed
- `FsHotWatch.Build.BuildPlugin` test-only-skip path used to emit `BuildSucceeded` instantly, beating FCS to the file. Test-prune then dispatched off stale `AffectedTests` and skipped runs that should have happened.
- New `BuildPhase.WaitingForFcsPhase` variant: when `SourceChanged` carries only test files, the plugin transitions into a wait phase carrying the awaiting set (path-normalized via `Path.GetFullPath`) and emits `BuildSucceeded` only once every file has produced a `FileChecked`. Subscribes to `SubscribeFileChecked`.
- **BREAKING:** `BuildPhase` is a public DU; consumers that pattern-match on it must add a `WaitingForFcsPhase` case.

### FsHotWatch.Coverage

#### Removed
- **BREAKING:** Package retired. Coverage enforcement now flows through `fileCommands afterTests` in FsHotWatch.Cli, invoking an external CLI (e.g. `coverageratchet`).

### FsHotWatch (core)

#### Changed
- **BREAKING:** Test-lifecycle events split into three: `TestRunStarted` (once per run, with `RunId` + `StartedAt`), `TestProgress` (per-group delta with `RunId` + `NewResults`), and `TestRunCompleted` (once per run, with `TotalElapsed` + `Outcome` + final cumulative `Results`). All three share one `RunId` per run. Replaces the single `TestCompleted` event. `PluginEvent`, `SubscribedEvent`, `PluginDispatchEvent`, `PluginCtx<_>`, and `PluginHostServices` all updated.
- `TestResults` retained as a plain internal value type (for TestPrune internals + afterRun hooks); no longer dispatched as an event.

#### Added
- `TestRunOutcome` DU (`Normal` / `Aborted of reason`). Per-project pass/fail derived from `TestResult` values in `Results`.

### FsHotWatch.FileCommand

#### Added
- `afterTests` trigger: fires after a test run completes, optionally filtered by test project names.

#### Changed
- **BREAKING:** `FileCommandPlugin.create` takes a `CommandTrigger` record instead of positional `fileFilter` + `runOnStart` args.
- `afterTests` list-form fires iff **every** listed project is present. Combined with TestPrune's per-group progress emission, the command fires exactly once per run — on the first `TestProgress` whose cumulative accumulator covers every listed project, or on `TestRunCompleted` (cache replay) — and is unblocked by slow non-listed groups (e.g. integration tests).
- **BREAKING:** Subscribes to `SubscribeTestProgress` + `SubscribeTestRunCompleted` (not the removed `SubscribeTestCompleted`). Dedup is keyed on `RunId` via `FileCommandState.LastFiredRunId`.

#### Fixed
- Idempotency across back-to-back runs with identical project sets. The previous `Set.isSubset`-based batch-boundary heuristic silently skipped every run after the first when project sets were stable (the dominant case).

#### Removed
- `runOnStart` config/API field.

### FsHotWatch.TestPrune

#### Changed
- **BREAKING:** `executeTests` emits the three-event lifecycle (`TestRunStarted` → `TestProgress` × N → `TestRunCompleted`) instead of the single `TestCompleted`. The abort path emits `TestRunStarted` + `TestRunCompleted(Aborted reason)` so subscribers see a coherent end to every run.
- The per-group accumulator is now a mutable `Map<string, TestResult>` under the emission lock (per-project `Map.add`) instead of rebuilding a `Map` from a `ResizeArray` on every emission.

### FsHotWatch.Cli

#### Added
- `--agent` / `-a` global flag for AI-agent-friendly parseable output: banner, `name: state [summary="..."]` per non-idle plugin, state-aware `next:` hint. States: `ok | fail | warn | running`. No ANSI.

#### Removed
- **BREAKING:** `coverage` config block.

#### Changed
- **BREAKING:** `--compact` / `-q` promoted to a global flag. `fs-hot-watch check -q` → `fs-hot-watch -q check`. Now accepted on every subcommand (including `status` and `errors`), matching the placement of `--verbose` and `--agent`.
- `fileCommands` entries accept `name` and `afterTests`; validation requires at least one of `pattern` / `afterTests` and an explicit `name` when `afterTests` is set. The config record now carries `PluginName: string` (derived at parse time) instead of `Name: string option`, eliminating a `failwith "unreachable"` fallback in the registration loop.
- Coverage output directory moves from the removed `coverage.directory` to `tests.coverageDir` (default `"coverage"`). Files are emitted at `<repoRoot>/<tests.coverageDir>/<project>/coverage.cobertura.xml`.

### FsHotWatch.Fantomas

#### Added
- Format preprocessor and format-check plugin respect `.gitignore` and `.fantomasignore`

---

## 2026-04-22 (`core-v0.8.0-alpha.8` · `testprune-v0.7.0-alpha.8` · `analyzers-v0.7.0-alpha.7` · `coverage-v0.7.0-alpha.7`)

### FsHotWatch 0.8.0-alpha.8

#### Added
- `PathFilter` module — shared path filtering with gitignore-style glob matching (via `Ignore` 0.2.1 package)
- `excludePatterns` parameter on `Daemon.create` / `Daemon.createWith` for excluding project trees from discovery
- `CheckPipeline.RegisterProject` filters out generated files in obj/ and bin/
- `IgnoreFilterCache` — caches .gitignore/.fantomasignore rules, auto-reloads on file changes
- `TaskCache.saltedCacheKey` / `optionalSaltedCacheKey` — cache-key builders that fold a per-event salt into the commit-based key, for plugins whose cache validity depends on state beyond the commit

#### Changed
- `performScan` takes `BatchContext` instead of 12 individual parameters
- Path filtering consolidated through `PathFilter` module (Watcher, CheckPipeline, Daemon)
- **BREAKING (IPC)**: `WaitForComplete` RPC now accepts a `timeoutMs: int` argument; `<= 0` means no client-imposed timeout. `DaemonRpcConfig.WaitForAllTerminal` signature changed from `unit -> Task<unit>` to `TimeSpan -> Task<unit>`.

#### Fixed
- `PluginFramework.registerHandler` now auto-reports `Failed(ex.Message, now)` when a handler's `Update` throws. Previously an uncaught throw after `ReportStatus(Running)` left the plugin stuck displaying `Running` indefinitely. Structural: no plugin author can forget it; impossible for a throw to leave the observed status non-terminal.

### FsHotWatch.Cli 0.8.0-alpha.8

#### Added
- `exclude` config field in `.fs-hot-watch.json` — gitignore-style glob patterns to exclude project trees
- `errors --wait [--timeout <seconds>]` — block until every tracked plugin reaches a terminal state before printing diagnostics

#### Fixed
- `start` is a singleton per repo, enforced by an OS exclusive lock on `.fs-hot-watch/daemon.lock` held for the daemon's lifetime; concurrent invocations cannot both proceed. Second invocation exits 0 with "Daemon already running at pipe <name> (pid <n>)".
- `stop` drains until the pipe is observed quiet for two consecutive probes (30 s overall timeout), cleanly taking down any number of historically-accumulated duplicate daemons and no longer misreporting "No daemon running" during pipe tear-down.

### FsHotWatch.TestPrune 0.7.0-alpha.8

#### Changed
- **BREAKING**: Bump `TestPrune.Core` 2.0.0 → 3.0.2. Adopts the revised `ITestPruneExtension` interface: extensions now implement `AnalyzeEdges` (returning `Dependency list` to inject into the graph) rather than `FindAffectedTests`. 3.0.2 also closes the pre-versioning stale-DB hole — `openCheckedConnection` recreates any DB where `user_version = 0` with existing user tables — so the schema-drift hang is prevented at both the Core and plugin layers.
- `AnalysisResult` construction now passes `Attributes` through from the analyzer (new field in `TestPrune.Core` schema v3).

#### Fixed
- **Stuck-state bug**: `flushAndQueryAffected` call sites in `BuildCompleted` and `TestsFinished (RerunQueued)` were unguarded; a DB hiccup pinned the plugin in `Running` forever. Both now report `Failed` and transition back to `TestsIdle` on exception.
- **Schema-drift self-heal**: SQLite "no such column" errors on a stale cache DB now trigger automatic deletion of the DB file with a warning, so the caller no longer has to know which file to remove.
- `affected-tests` command now updates on every `FileChecked` event rather than waiting for the next `BuildCompleted`.

### FsHotWatch.Analyzers 0.7.0-alpha.7

#### Changed
- Extracted `isKnownNonAnalyzerPrefix` and `buildAnalyzerProjectOptions` from `createCliContext` (internal) to enable deterministic unit tests for branches that live-SDK integration tests used to hit nondeterministically.

### FsHotWatch.Coverage 0.7.0-alpha.7

#### Fixed
- Cache key now carries a tristate salt derived from the thresholds file (absent / unreadable / content SHA-256), so editing `coverage-ratchet.json` under the same commit invalidates the cached plugin status, and a transient IO error on the thresholds file no longer presents as "file absent" to the cache.

### Tests / CI (this cycle)

- Split end-to-end FCS / analyzer / lint / format / build tests into a new `tests/FsHotWatch.IntegrationTests` project, excluded from the coverage aggregate to stabilize the ratchet.

---

## 0.5.0-alpha.1 (2026-04-12)

### FsHotWatch

#### Added
- Enable TransparentCompiler for hash-based deterministic FCS caching (`useTransparentCompiler = true`)
- Parse `#nowarn` directives to suppress FCS TransparentCompiler warnings (workaround for dotnet/fsharp#9796)
- Plugin teardown support in `PluginHandler`

#### Changed (Breaking)
- Type safety overhaul: `AbsFilePath`/`AbsProjectPath` single-case DUs replace raw strings; `PluginName` DU with uniqueness check; `ContentHash` wrapper; `CommandOutcome` DU replaces `Succeeded: bool` + `Output: string`; `FileCheckState` DU replaces `CheckResults option`; `AffectedTestsState` DU; `RerunIntent` DU; `Set<SubscribedEvent>` replaces `PluginSubscriptions` bool record; `TaskCacheKey` struct; `TestExtensionKind` DU; `CacheClearFilter` DU
- Plugin registration uses `PluginHostServices` record instead of multi-param function
- `Daemon` changed from F# record to class with `internal` constructor
- `IProjectGraphReader` interface decouples `BuildPlugin` from mutable `ProjectGraph`

#### Fixed
- Propagate cancellation token into `CheckFileCore` — `CancelPreviousCheck` now actually stops in-flight FCS checks
- Handle shared source files (linked items): `fileToProjects` now stores all projects per file; `GetProjectsForFile` returns all; Daemon checks shared files in each project context via `CheckFileWithOptions`
- `Daemon` implements `IDisposable` and stops all internal `MailboxProcessor` agents on dispose
- `RunWithIpc` races initial scan against cancellation to prevent test-process hangs
- Standalone files not in any project now checked via uncovered-files fallback

### FsHotWatch.Cli

#### Added
- Filter Info/Hint diagnostics from CLI output — only Error and Warning shown

#### Changed
- `DiagnosticEntry.Severity` typed as `DiagnosticSeverity` DU instead of string
- `startFreshDaemon` startup poll deadline configurable via `startupTimeoutSeconds` parameter (default: 30s)
- Process launch in `startFreshDaemon` injectable via `IpcOps.LaunchDaemon`
- Bump `CommandTree` 0.3.5 → 0.4.0, `TestPrune.Falco` 1.0.1 → 1.0.2

#### Fixed
- `renderIpcResult` crash on JSON containing array values (e.g. test results)
- Deduplicate `DisplayStatus`/`formatStatusLine`/error formatting — reuse `PluginStatus` from core and shared formatting from `RunOnceOutput`

### FsHotWatch.Analyzers

#### Changed
- Run parse-only analyzers (passing `null` for check results) instead of skipping files without full type-check results

### FsHotWatch.Lint

#### Changed
- Lint runner injectable via `lintRunner` parameter for testability

### FsHotWatch.TestPrune

#### Changed
- Bump `TestPrune.Core` 1.0.1 → 2.0.0 — cross-project extern symbol support

#### Fixed
- Comment-only source changes no longer add the file to `ChangedFiles` — only genuine AST changes propagate to extension-based tests
