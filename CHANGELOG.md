# Changelog

All notable changes to FsHotWatch packages are documented here.

## Unreleased

## Released — the `alpha.9` line onward (2026-04-22 → 2026-06-19)

_These narratives are all shipped. This root file is a human-readable summary that fell
behind around `core-v0.8.0-alpha.8` — the entries below were released across the alpha.9+
series but only some got closed out of `Unreleased`. For the precise per-version, per-package
history (the source of truth that drives the release tags) see each `src/<package>/CHANGELOG.md`.
Latest released: `core-v0.8.0-alpha.32` · `cli-v0.8.0-alpha.38` · `testprune-v0.7.0-alpha.28` ·
`analyzers-v0.7.0-alpha.19` · `coverage-v0.7.0-alpha.14`._

### The test gate trusts the test report, not the exit code

`fshw check` could go falsely RED when a test host exited non-zero during a dirty
shutdown (e.g. the Microsoft.Testing.Platform exit-7 flake) after writing a clean
report — surfacing "Tests failed in <project>" with zero named tests while a re-run
came back green. The pass/fail verdict is now derived from the CTRF report's summary
counts and is authoritative over the process exit code (only a tie-break when no
report exists): a non-zero exit with a clean, complete report is GREEN. A run that
aborts before writing any parseable report (non-zero exit, no results) gets a new
`TestsErrored` verdict — not a failure, not a pass, never cached, surfaced as an
honest "errored — re-run". CTRF report injection is scoped per project via the new
`.fshw.json` `reportVerificationFormat` (`auto` | `ctrf` | `off`), so a non-xUnit
runner that would choke on `--report-ctrf` keeps the exit code authoritative. Also
fixed: per-test flakiness tracking, which silently recorded nothing because the
parser read a top-level `tests` array instead of the real nested `results.tests`.

### The test gate can no longer go green without running the tests

`fshw check` could report "No errors" while executing zero tests: TestPrune's
impact baseline advanced on symbol ANALYSIS, not on tests passing, so a run
that aborted (e.g. a failing test `beforeRun` hook) or failed still absorbed
the symbols it never verified — a later check then found "0 affected tests"
and exited 0. TestPrune now keeps a durable needs-testing queue
(`.fshw/test-prune/pending-verification.json`); a symbol leaves it only when a
covering test run completes green. Aborted runs report Failed instead of
green, the "nothing to test" fast path requires the persisted queue to be
empty, a cached green can only replay for a queue-empty state, and a daemon
restart re-flags anything still unverified. The sidecar is written once per
analysis batch, so the per-file hot path gains no I/O.

### Deterministic unit-suite coverage under machine load

The coverage ratchet no longer flakes when the machine is busy: the two
real-subprocess plugin timeout tests moved to the coverage-excluded integration
suite, the post-kill drain tail was extracted into an internal
`ProcessHelper.drainedOrEmpty` helper with direct deterministic tests, and the
absent-key `EndSubtask` arm gained a direct test. Per-file line coverage is now
identical run-to-run (quiet or loaded) and ratchet floors are settled to the
stable actuals.

### Daemon: idle-exit — quit after a configurable idle period

An idle daemon still holds a large warm working set (mostly FCS-rooted native
memory, ~2.8-3.1 GB). With one daemon per jj workspace, idle workspace daemons
waste gigabytes between bursts of work. The daemon can now shut itself down
after a configurable idle period to reclaim that memory. This is transparent:
the next `fshw` command auto-starts a fresh daemon and the file-backed check
cache survives restarts, so the next `check` pays one auto-start plus a
mostly-cache-hit scan. Shutdown is the daemon's normal graceful path (the same
`cts.Cancel()` the IPC `stop` request uses — clean pid/lock release and plugin
disposal), guarded by an atomic fire-once latch so it can never fire twice.

#### Added
- **`idleExitMin` config key** in `.fshw.json` (`number | false`):
  - **absent → AUTO mode**: enabled with a 30-minute threshold **iff** the repo
    root path contains a `/.workspaces/` segment (non-default jj workspaces);
    disabled otherwise — the default/main workspace daemon never auto-quits.
  - **`0` or `false`**: disabled everywhere (explicit opt-out, overrides AUTO).
  - **positive integer `N`**: enabled with an `N`-minute threshold regardless of
    path (explicit opt-in, even for the default workspace).

  The daemon only exits when idle (no file events, no running plugin work) for
  the full window; work in flight at the threshold defers the exit to a later
  check.

### Daemon: ship `System.GC.ConserveMemory=9` default

The daemon keeps `FSharpChecker` and its FCS caches warm, which generates a
large amount of collectable managed churn above the live working set. Left to
the default GC policy, that churn accumulates into multiple gigabytes of
retained heap. The CLI now bakes `System.GC.ConserveMemory=9` into its
`runtimeconfig.json`, which in benchmarks cut the daemon's steady footprint by
~25-40% (settled ~3.0GB vs 3.9-4.4GB; peak 5.0 vs 5.9-7.8GB against a
32-project solution) with no measurable cost to scan speed or diagnostic
parity.

#### Changed
- **`FsHotWatch.Cli` runtime config.** Added the `System.GC.ConserveMemory=9`
  `RuntimeHostConfigurationOption` so the daemon runs with conservative GC by
  default. Override per-process with the `DOTNET_GCConserveMemory` environment
  variable (`0`-`9`), which takes precedence over the baked-in default.

#### Removed
- **Dead `projectCacheSize` argument.** Dropped the `projectCacheSize = 200`
  argument from the daemon's `FSharpChecker.Create` call. It is ignored by the
  `TransparentCompiler` path (`useTransparentCompiler = true`), which never
  reads it, so the value had no effect.

### Daemon: fix silent truncation of cold scans (cancelled-check race)

During a cold scan the BuildPlugin's `dotnet build` touches `obj/**/ref/*.dll`;
the watcher fires, `processBatch` re-checks the affected files, and
`CancelPreviousCheck` cancels the scan-side in-flight check of the *same* file. A
cancelled check surfaces as `None`, and the scan emit loop silently dropped it
(`| None -> ()`). The dropped files were never reported to NOR cleared from the
ErrorLedger, so a scan could report **green** while diagnostics for hundreds of
never-checked files were missing — observed as `Checked 103 files … skipped 46`
on a 742-file registration with exit 0 and a known diagnostic absent from
`check` output.

#### Fixed
- **Scan now retries cancelled/aborted/failed checks** within a bounded budget
  (3 retry rounds per tier). The retry re-invokes the same per-file check, which
  re-reads current disk content via `CancelPreviousCheck` — so a newer user edit
  that legitimately superseded an in-flight check is observed on retry (not
  duplicated), preserving the cancellation ordering guarantee. The common
  single-race case converges to all files checked.
- **Incomplete scans no longer present as clean.** If files remain unchecked
  after the retry budget, the scan-complete state carries the unchecked count;
  daemon status renders `incomplete: N files checked, M unchecked …` (a non-ok
  condition) instead of `complete: …`, and the scan log line gains an
  `, unchecked M` suffix. The existing `Checked N files (T tiers), skipped M`
  prefix is preserved for external tooling that greps it.

#### Changed
- `ScanState.ScanComplete` now carries `unchecked: int`
  (`ScanComplete of total * unchecked * elapsed`).

### Daemon: auto-recovering deps-freshness gate before FCS analysis

When a project's restored dependency state (`obj/project.assets.json`) goes
stale relative to its declared deps — a `PackageReference` added without a
`dotnet restore`, or a restore that half-completed — FCS otherwise emits a
phantom error-storm (`namespace`/`type not found` across the whole project)
that looks like broken code but is really a stale restore. The daemon now
catches that state before type-checking, and recovers from it automatically
where it can.

#### Added
- **Deps-freshness gate (`FsHotWatch.DepsFreshness`).** Before FCS analysis the
  daemon compares each project's restored-assets mtime against its declared
  dependency files (`.fsproj`, `Directory.Packages.props` /
  `Directory.Build.props`, `paket.lock` / `paket.dependencies`,
  `.config/dotnet-tools.json`). On a staleness signal it first attempts a
  **one-shot restore to recover automatically**; only if recovery fails does it
  **fail fast with a single actionable diagnostic**, instead of letting the
  type-checker produce a misleading "namespace not found" storm. Detection and
  orchestration are pure (injected restore runner + freshness probe), unit-tested
  without shelling out or touching FCS. See
  `docs/plans/2026-06-02-deps-freshness-gate.md`.

### Dependencies: refresh external packages

Routine maintenance bump of external (non-FsHotWatch) NuGet dependencies to
current releases. All gated checks (daemon `check`, 1230 unit tests with
coverage) stay green; no public API change.

#### Changed
- `FSharp.Compiler.Service` 43.12.203 → 43.12.204 (core daemon + Analyzers
  plugin + ExampleAnalyzer).
- `StreamJsonRpc` 2.24.84 → 2.24.92 (IPC).
- `Microsoft.SourceLink.GitHub` 10.0.203 → 10.0.300 (all packable projects).
- `Microsoft.Testing.Extensions.CodeCoverage` 18.6.2 → 18.7.0 (test).
- `CommandTree` 0.6.1 → 0.6.2 (CLI) — picks up the revision-stamping target fix,
  so building `fshw` outside a VCS repo (e.g. a `.git`-less jj sub-workspace) no
  longer emits `MSB3073` warnings.
- Pinned transitives advanced to current patched releases:
  `System.Security.Cryptography.Xml` 10.0.7 → 10.0.8 and
  `Nerdbank.MessagePack` 1.1.62 → 1.2.4 (both still cover their respective
  CVE pins in `Directory.Build.props`).

`FSharp.Core` stays on the pinned `10.1.*` float (already current). No
YamlDotNet dependency exists in this repo.

### Daemon: auto-refresh FCS on `.fsproj` and `obj/project.assets.json` changes

Reported by `thellma/intelligence` during the `bedrock-spike` landing
(docs/fr-auto-refresh-fsproj-changes.md, 2026-05-25). Adding an
`AWSSDK.Bedrock` `PackageReference` and running `dotnet restore` left
the daemon reporting `FS0039: namespace 'Bedrock' is not defined`
until the user ran `dotnet fshw stop && dotnet fshw start` — discarding
the entire FCS cache for ~20 unrelated projects unnecessarily.

Closing the loop took three contracts: detect the change, re-evaluate
only what's affected, and keep everything else hot.

#### Fixed
- **The daemon now re-runs FCS on the affected project's source files
  after a project-tier change.** Previously `processBatch` invalidated
  the FCS cache and re-discovered options on a `.fsproj` change, but
  the per-file re-check only ran when the same batch also contained
  source-file edits. A pure project change had no source files, so the
  error ledger retained the previous cohort's stale diagnostics until
  the user saved a `.fs` file or restarted the daemon. The boot-scan
  re-check on restart is what made the "stop && start" workaround
  appear to "fix" the bug.
- **A project change no longer cold-starts every other project.** The
  re-check above, as first written, re-checked *all* registered files
  and cleared the whole check cache — on a 20-project solution that
  reintroduced the ~30s cold start the FR set out to eliminate. The
  daemon now scopes invalidation to the changed project **and its
  transitive dependents** (`Daemon.resolveAffectedProjects`): it calls
  `FSharpChecker.InvalidateConfiguration` for just that set, re-discovers
  without clearing unrelated projects' cached results, and re-checks only
  that set. Dependents are explicitly cache-invalidated so a dependent
  that breaks when the changed project's public surface changes still
  recomputes (correctness over warmth). Repo-wide changes (`.props`,
  solution edits, a brand-new project) and the case where watcher and
  project-graph paths diverge (a repo under a symlink) fall back to the
  full invalidate-and-recheck path.

#### Added
- `Watcher.isProjectAssetsJson` / extended `Watcher.classifyChange` —
  `obj/project.assets.json` (the post-`dotnet restore` materialization
  of the package graph) is now treated as a project-tier change. The
  `FileSystemWatcher` / FSEvents enumeration picks it up despite living
  under `obj/` (every other `obj/` entry stays excluded). This gives
  the daemon a second, canonical "package graph is coherent on disk"
  signal that doesn't race with a `.fsproj` edit's "package graph is
  intended to change."

#### Changed
- `Pinned transitive Nerdbank.MessagePack 1.1.62` (GHSA-2cwq-pwfr-wcw3).
  `StreamJsonRpc 2.24.84` pulls in vulnerable `1.0.2` by default,
  failing fresh `dotnet restore` under `NU1903`. Same Directory.Build.props
  pattern as the existing `System.Security.Cryptography.Xml` pin.

### TestPrune: test-skip now works correctly after daemon restart

TestPrune prunes the test suite in two phases: **Phase A** runs during the initial cold scan (FCS checks every file from scratch and records symbol fingerprints), and **Phase B** runs on subsequent daemon restarts when FCS is warm (the daemon reloads the persisted fingerprints and skips tests whose symbol fingerprints haven't changed). Before this fix, Phase B would always re-run tests even with no source edits, because the fingerprint comparison included extern symbols (cross-file type references) on the current side but not the stored side, producing phantom diffs on every restart.

#### Fixed
- fix: daemon restart with no source edits no longer spuriously re-runs tests — `detectChanges` now filters extern symbols from both sides internally (requires TestPrune.Core ≥ 4.0.1), eliminating the phantom symbol diffs that caused already-passing tests to re-run on every warm restart.

#### Changed
- refactor: remove redundant `currentForFile` pre-filter from `TestPrunePlugin` (`detectChanges` now handles extern filtering internally in TestPrune.Core).

### Drop hardcoded FS1182 default suppression

#### Changed
- **BREAKING (behavior):** `Daemon.DaemonOptions.FcsSuppressedCodes = None` now resolves to an empty `Set<int>` instead of `Set.ofList [ 1182 ]`. The daemon no longer ships a built-in suppression for FS1182 ("unused binding"), which embedded a project-level policy (originally a workaround for SqlHydra-generated code) at the wrong layer. Projects that need FS1182 silenced should declare it explicitly via `<NoWarn>FS1182</NoWarn>` in the fsproj (e.g. `Directory.Build.props`, as Intelligence already does) or `#nowarn "1182"` in source — both paths report at the correct scope.

#### Added
- `Daemon.resolveFcsSuppressedCodes : int list option -> Set<int>` — public helper exposing the option→Set resolution so it's directly testable.

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

### FsHotWatch.Coverage (new package)

#### Added
- New `FsHotWatch.Coverage` NuGet package — checks per-file line and branch coverage
  thresholds after every `TestRunCompleted` event. Reads Cobertura XML produced by the
  test runner and compares against per-file thresholds in a `coverage-ratchet.json` config.
  Violations surface via `fshw errors`; thresholds are updated via `fshw coverage-ratchet`.
- `CoveragePlugin.create (configPath: string) (searchDir: string)` — factory function.
- IPC commands: `coverage-ratchet [configPath]` (update thresholds), `coverage-status`.
- `.fshw.json` `"coverage"` section: `{ "configPath": "...", "searchDir": "..." }`. Both
  fields are optional (defaults: `"coverage-ratchet.json"` and `"."`).
- Always merges partial runs into a coverage baseline (`mergeIntoBaselines`) before
  checking; replaces baseline wholesale (`refreshBaselines`) only after a passing
  full-suite run, so partial/impact-filtered runs accumulate coverage rather than resetting it.

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
- **Behavior:** `run-tests` IPC command (invoked by `fshw test`) now routes through the
  event machinery, emitting `TestRunStarted` → `TestProgress` × N → `TestRunCompleted`.
  Plugins subscribed to `SubscribeTestRunCompleted` (including `CoveragePlugin`) now
  observe manually-triggered runs the same way as daemon-triggered ones. No API change.
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
