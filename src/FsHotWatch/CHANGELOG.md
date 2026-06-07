# Changelog — FsHotWatch (core)

## Unreleased

### Added

- feat: memory pressure shortens idle-exit (`pressureIdleFloorMin` in `.fshw.json`). When a daemon is already idle-exit-eligible (a `/.workspaces/` checkout in AUTO mode, or an explicit `idleExitMin N`) AND the machine is under memory pressure, the effective idle window is shortened to `min(idleExitMin, pressureIdleFloorMin)` so a tight machine sheds idle daemons fast — a 30-min workspace daemon quits after 2 min idle under pressure. Pressure is the GC's own high-load mark (`GC.GetGCMemoryInfo().MemoryLoadBytes >= HighMemoryLoadThresholdBytes`), re-evaluated each 30s tick (if pressure subsides the full window is restored). The default/main workspace stays **exempt**: pressure only shortens an already-applicable window, it never creates one. Absent → floor at `2` min; `0`/`false` → pressure-shortening disabled; positive `N` → floor at `N` min. This replaces the earlier (unreleased, same-session) `pressureTrimPct` in-place cache-trim, which was reversed before release: trimming kept ~400 MB plus the process resident yet still forced a cold FCS rebuild on the next edit (the file-backed CheckCache survives both trim and quit), so quitting dominates. See ADR-005.

- `ErrorLedger` now accepts an optional `logError: string -> string -> unit` sink
  (defaults to the process-global `Logging.error`/stderr) used when a reporter throws.
  The failure log is emitted on the ledger's MailboxProcessor agent thread, so a
  `Console.Error` capture in tests raced any concurrent `Console.SetError`; the
  injectable sink lets callers (notably tests) observe reporter failures without
  touching process-global console state. No behavior change at the default.
## 0.8.0-alpha.23 - 2026-06-07

- chore: republish so the bundled `fshw` carries the CLI's dead-code schema-probe fix
  (now sourced from TestPrune.Core's public `Database.SchemaVersion`, 4.2.1). Also folds
  in an internal `ProcessHelper` refactor unifying the child-env sanitization lists into
  one `sanitizedChildEnvKeys`. No core API change.

## 0.8.0-alpha.22 - 2026-06-07

- fix: spawned `dotnet build` (and any other child process) no longer inherits the
  `MSBUILD_EXE_PATH` / `MSBuildExtensionsPath` / `MSBuildSDKsPath` variables that
  Ionide.ProjInfo writes into the daemon's own environment during in-process project
  evaluation. On a multi-SDK machine those leaked vars pinned the child's MSBuild to a
  different (or incomplete) SDK band than the muxer resolved, so the implicit restore
  failed with exit 1 and zero diagnostics — surfacing as the long-standing
  `fshw check --run-once` "Build FAILED / 0 Error(s)" while a plain-shell `dotnet build`
  of the same tree was clean. `ProcessHelper.runProcessWithTimeout` now strips these keys
  before every spawn (same treatment as the arch-specific `DOTNET_ROOT_*` keys);
  caller-supplied overrides still win. See docs/leaked-msbuild-env-bug.md.
- fix: the deps-freshness gate no longer counts .config/dotnet-tools.json as a dependency input — a dotnet-tool bump no longer false-stales every project into restore-recovery/skipped scans

## 0.8.0-alpha.21 - 2026-06-06

- feat: `fshw dead-code` — runs TestPrune's unreachable-symbol analysis against the daemon's .fshw/test-impact.db (same semantics as the standalone test-prune CLI: --entry/--include-tests/--verbose), no DB copying needed.

## 0.8.0-alpha.20 - 2026-06-05

- chore: bundle FsHotWatch.TestPrune 0.7.0-alpha.19 (clears the FCS check-cache after a
  schema-bump DB recreate, so the symbol graph re-indexes fully instead of staying
  partial). No core API change; republished so the bundled tool ships the fix.

## 0.8.0-alpha.19 - 2026-06-04

- chore: bundle FsHotWatch.TestPrune 0.7.0-alpha.18 (cold-start coverage no longer
  clobbers prior coverage while the symbol graph is still indexing). No CLI API change;
  republished so `dotnet fshw` carries the fix.

## 0.8.0-alpha.18 - 2026-06-04

- chore: bundle the DB-backed coverage plugins (FsHotWatch.TestPrune 0.7.0-alpha.17,
  FsHotWatch.Coverage 0.7.0-alpha.11). No CLI API change; republished so the `fshw`
  tool's bundled plugins carry TestPrune-native single-source coverage.

## 0.8.0-alpha.17 - 2026-06-03

- feat: auto-recovering deps-freshness gate before FCS analysis. When a project's `obj/project.assets.json` is stale relative to its declared deps (a `PackageReference` added without `dotnet restore`, or a half-completed restore), the daemon detects it *before* type-checking, attempts a one-shot `dotnet`/`paket`/`tool restore` to recover automatically, and only if recovery fails reports a single actionable `deps` diagnostic — instead of letting FCS emit a phantom "namespace/type not found" error-storm. Detection and orchestration are pure and unit-tested (`FsHotWatch.DepsFreshness`: `compareFreshness`, `dependencyFiles`, `evaluateProject`, `RecoveryTracker`); each restore step is bounded by a 5-minute timeout so a hung restore fails fast rather than wedging the scan.

## 0.8.0-alpha.16 - 2026-06-02

- feat: TestsDeferred result case — a test project that never ran (apphost not yet produced) is reported as deferred ("waiting on build"), non-passing, instead of a false-green pass; the aggregate verdict/exit code can no longer be green when tests didn't actually run.
- fix: a throwing IErrorReporter no longer yields a false-clean verdict — the ledger self-reports the reporter failure so the verdict/exit code reflects that diagnostics couldn't be recorded.

## 0.8.0-alpha.15 - 2026-05-28

- fix: `FileErrorReporter` caps oversized `message`/`detail` fields before serialization to avoid `System.Text.Json` transcode `OverflowException` (FR `fr-fileerrorreporter-overflow.md`).

## 0.8.0-alpha.14 - 2026-05-26

- feat: daemon auto-refreshes FCS state on `.fsproj` / `obj/project.assets.json` changes — adding a `PackageReference` + `dotnet restore` now resolves without a daemon restart or a `.fs` save (FR `docs/fr-auto-refresh-fsproj-changes.md`). `Watcher.classifyChange` routes `obj/project.assets.json` through `ProjectChanged` (recovers the `.fsproj`-edit-races-`restore` window), and `Daemon.processBatch` re-checks the affected project's source files after re-discovery.
- feat: scoped FCS invalidation — a single project change invalidates only that project plus its transitive dependents (`Daemon.resolveAffectedProjects`), keeping unrelated projects' warm FCS state and cached check results instead of cold-starting the whole solution. Repo-wide changes (`.props`, solution edits, a brand-new project) fall back to full re-discovery.
- feat: `CheckPipeline.PrepareForRediscovery` gained an optional `?clearCheckCache` parameter (default `true`); the scoped project-change path passes `false` to retain unrelated projects' cached check results across re-discovery.

## 0.8.0-alpha.13 - 2026-05-04

- fix: `Daemon.DaemonOptions.FcsSuppressedCodes = None` now resolves to an empty `Set<int>` instead of `Set.ofList [ 1182 ]`; projects that need FS1182 silenced should declare `<NoWarn>FS1182</NoWarn>` in their fsproj
- feat: `Daemon.resolveFcsSuppressedCodes : int list option -> Set<int>` — public helper exposing the option→Set resolution so it is directly testable

## 0.8.0-alpha.12 - 2026-04-29

### Added

- **`FsHotWatch.ErrorLedger.DiagnosticSeverity.order`** — total order on `Error/Warning/Info/Hint` for severity-threshold comparisons.
- **`FsHotWatch.CheckCache.DiagnosticSignature`** record (`StartLine/StartColumn/ErrorNumber/Severity/Message`) and **`hashDiagnosticSignatures`** — extracted from `fcsCheckSignature` so the hashing/sorting logic is unit-testable without a live `FSharpCheckFileResults`.
- **`FsHotWatch.FileTaskCache`** — atomic on-disk writes (write-temp-then-rename) and startup size telemetry logging total entry count and on-disk bytes.
- **`IProjectGraphReader.GetTargetFramework`** + **`ProjectGraph.GetTargetFramework`** — exposes the first `<TargetFramework>` (or first entry of `<TargetFrameworks>`) parsed from each .fsproj at registration time. Avoids re-opening + re-parsing the .fsproj from downstream consumers. **`extractTargetFramework`** is the underlying pure XDocument-taking helper.
- **`IProjectGraphReader.GetCanonicalDllPath`** — returns `<projDir>/bin/Debug/<TFM>/<projectName>.dll` (or `None` when TFM is missing). Centralises the canonical-DLL convention so consumers like `BuildPlugin.verifyArtifactsFresh` don't reinvent the path.
- **`IProjectGraphReader.GetMaxSourceMtime`** — newest `LastWriteTimeUtc` across a project's on-disk source files. Drives mtime-based artifact-freshness checks.

### Removed

- **`FsHotWatch.ProjectDirtyTracker` module.** The dirty-bit handoff between BuildPlugin and TestPrunePlugin is gone — staleness is enforced inline by BuildPlugin's post-build verification, so the heuristic dirty tracker has no consumers. Files using it: `markDirty`, `clearFreshProjects`, `isStaleProject`, the manual-run-tests deadlock workaround. See FsHotWatch.Build / FsHotWatch.TestPrune CHANGELOGs for downstream impact.

### Changed

- **BREAKING — `TestResult` DU widened with `elapsed: TimeSpan`.** All three constructors (`TestsPassed`, `TestsFailed`, `TestsTimedOut`) now carry a per-project wall-clock duration. `FileTaskCache` round-trips it via a new `elapsedSeconds` JSON field; older cached entries that omit the field deserialize as `TimeSpan.Zero` (no recorded duration). Pattern-match callers must add the new bind position. New `TestResult.elapsed` accessor is the recommended way to read it.

### Changed

- **`FsHotWatch.ErrorLedger.fromString`** now returns `DiagnosticSeverity option` instead of throwing on unknown severity strings. Callers that previously caught the exception should match on `None`.
- **`FsHotWatch.CheckCache.fcsCheckSignature`** guards `Unchecked.defaultof<FSharpCheckFileResults>` and other null cases — returns `"full-check-null"` / `"full-check-error"` instead of throwing.
- **`TimestampCacheKeyProvider.GetFileHash`** now hashes file content (SHA-256) instead of metadata (path + size + mtime). Closes a correctness gap where two files with the same size but different bytes (or same bytes with different mtime) would produce the wrong key. The name is preserved for backward compatibility; behavior matches the original "ls-tree merkle hash" design intent that was deferred at module creation.

### Removed

- **`JjCacheKeyProvider`** — was a stub that delegated to `TimestampCacheKeyProvider`. Its only role was as a marker for `Daemon.fs` to wire up `JjScanGuard` via runtime type-test.
- **`FsHotWatch.JjHelper` module** — `JjScanGuard`, `JjScanDecision`, `getWorkingCopyCommitId`, and `getChangedFiles`. The scan-skip-when-commit-unchanged optimization is gone. Plugin caches are content-addressed (post-§2a) and the FCS check-result cache now hashes file content directly; together they make the scan-skip path's marginal benefit negligible while removing the only jj runtime reliance.
- **`Daemon.DaemonOptions.EnableJjScanGuard`** — no longer needed; the option is dropped from the public surface.
- **`DaemonConfig.JjFileBackend`** variant — `"jj"` cache config string is still accepted as a legacy alias and falls back to `FileBackend`.
- **BREAKING — `force` parameter removed from scan API:** `Daemon.ScanAll(?force)` → `ScanAll()`, `DaemonRpcConfig.RequestScan: bool -> unit` → `unit -> unit`, `DaemonRpcTarget.Scan(force)` → `Scan()`, `IpcClient.scan pipeName force` → `IpcClient.scan pipeName`. The flag had been a no-op since `JjScanGuard` was deleted.

### Changed

- **`DaemonConfig.createCacheComponents`** return type went back from `(backend, provider, enableJjScanGuard)` triple to `(backend, provider)` pair (the third element was always paired with the now-removed `JjFileBackend`).
- **`DaemonConfig.detectDefaultCacheBackend`** now always returns `FileBackend` (kept for API compatibility; previously returned `JjFileBackend` when a `.jj/` directory existed).
- **`InitConfig.generateConfig`** signature dropped its `hasJj: bool` parameter.

## 0.8.0-alpha.11 - 2026-04-26

### Added

- **`FsHotWatch.ProcessHelper.isDotnetCommand`** and **`mergeDotnetEnv`** — public helpers that detect a `dotnet`/`dotnet.exe` command basename and merge `MSBUILDDISABLENODEREUSE=1` into its env (unless already set).
- `runProcessWithTimeout` now injects the env automatically for `dotnet` commands. Eliminates orphan `MSBuild.dll /nodemode:1` workers across daemon-spawned builds without per-plugin opt-in. See `docs/msbuild-node-reuse-bug.md`.
- **`FsHotWatch.PluginFramework.PluginCtxHelpers.reportOrClearFile`** — collapses the per-file `if entries.IsEmpty then ClearErrors else ReportErrors` idiom shared across analyzer-style plugins.

## 0.8.0-alpha.10 - 2026-04-25

### Added

- **`FsHotWatch.ProcessHelper.ProcessOutcome` DU** (`Succeeded of output` /
  `Failed of exitCode * output` / `TimedOut of after * tail`) replaces the
  historical `bool * string` return on `runProcessWithTimeout` / `runProcess`.
  Callers pattern-match instead of parsing a magic prefix from the output. Helpers:
  `isSucceeded`, `isTimedOut`, `outputOf`.
- **`FsHotWatch.ProcessHelper.WorkOutcome<'a>` DU** (`WorkCompleted of 'a` /
  `WorkTimedOut of after`) replaces `Result<'a, string>` on `runWithTimeout`.
- **`FsHotWatch.Events.TestResult.TestsTimedOut of output * after * wasFiltered`** —
  new variant distinguishing timeout-killed test runs from regular failures.
  `TestResult.isTimedOut` helper added; existing helpers updated to handle the
  new case. `FileTaskCache` round-trips it under the `"timed-out"` JSON tag.
- **`FsHotWatch.ProcessRegistry`** module — per-daemon, `AsyncLocal`-scoped
  registry of live `Process` handles. `Daemon.Dispose` calls `KillAll` so
  `dotnet fs-hot-watch stop` reaps in-flight test runners (and their playwright
  drivers etc.) instead of leaving orphans that contend with the next start.
  `runProcessWithTimeout` registers spawned children and unregisters in
  `finally`.

### Changed

- **BREAKING:** `runProcessWithTimeout` / `runProcess` return `ProcessOutcome`
  (was `bool * string`).
- **BREAKING:** `runWithTimeout` returns `WorkOutcome<'a>` (was `Result<'a, string>`).
- **BREAKING:** `FsHotWatch.ProcessHelper.TimedOutPrefix` literal removed.
  Pattern-match the new DUs.
- **Plugin status visibility sweep.** Plugins are now responsible for calling
  `ctx.CompleteWithSummary` explicitly at the end of each run; the framework
  no longer derives a summary from the last log line or the longest-running
  subtask. `IActivitySink` / `PluginCtx` gain `UpdateSubtask(key, label)` for
  in-place label updates on a long-lived primary subtask without churning
  state. The compact renderer now shows the `"primary"` subtask's descriptive
  label when present, instead of falling back to the activity tail.

## 0.8.0-alpha.9 - 2026-04-23

### Changed (breaking)

- **Test lifecycle events split into three**: `TestCompleted` is replaced by
  `TestRunStarted` (once per run, with `RunId` + `StartedAt`), `TestProgress`
  (per-group delta with `RunId` + `NewResults`), and `TestRunCompleted` (once
  per run, with final cumulative `Results` + `Outcome`). All three share one
  `RunId` per run. Subscribers that only care about end-of-run state read
  `TestRunCompleted.Results`; subscribers that want per-group progress consume
  `TestProgress` deltas and accumulate locally keyed by `RunId`.
- `PluginEvent` adds `TestRunStarted` / `TestProgress` / `TestRunCompleted`;
  drops `TestCompleted`.
- `SubscribedEvent` / `PluginDispatchEvent` gain matching variants; drop
  `SubscribeTestCompleted` / `DispatchTestCompleted`.
- `PluginCtx<_>` and `PluginHostServices` replace `EmitTestCompleted` with
  `EmitTestRunStarted` / `EmitTestProgress` / `EmitTestRunCompleted`.
- `TestResults` kept as a plain value type for internal TestPrune use; no
  longer dispatched as an event.

### Added

- `TestRunOutcome` DU (`Normal` / `Aborted of reason`). Per-project pass/fail
  lives in `TestRunCompleted.Results` (derivable from `TestResult` values).

### Fixed

- `FileCommandPlugin`'s `afterTests` trigger previously used a superset
  heuristic to detect batch boundaries and would silently skip every run
  after the first when project sets were identical (the dominant case for
  stable configs). Now keyed on `RunId`: fires exactly once per distinct run.

- chore: bump upstream tool versions

## 0.8.0-alpha.8 (2026-04-22)

### Added

- `TaskCache.saltedCacheKey` / `TaskCache.optionalSaltedCacheKey` — cache-key
  builders that fold a per-event salt into the commit-based key. Plugins whose
  cache validity depends on state beyond the commit (e.g. a config file whose
  edits don't change the commit) can salt with a hash of that state. Empty salt
  produces the pre-existing key format, so on-disk cache compatibility is
  preserved.

### Changed

- **BREAKING (IPC)**: `WaitForComplete` RPC now accepts a `timeoutMs: int` argument; `<= 0` means no client-imposed timeout. `DaemonRpcConfig.WaitForAllTerminal` signature changed from `unit -> Task<unit>` to `TimeSpan -> Task<unit>` so clients can pass their own deadline. The daemon's previous hard-coded 30-minute cap no longer applies when the client supplies a timeout.

### Fixed

- `PluginFramework.registerHandler`: when a handler's `Update` throws, the framework now auto-reports `PluginStatus.Failed(ex.Message, now)` in addition to logging. Previously an uncaught handler throw left the plugin in whatever transient status it had reported beforehand (classic case: TestPrune reports `Running`, hits a schema-drifted DB, never transitions further → UI shows "running" forever). This is a structural fix: no plugin author can forget to do it, and no plugin can leave an observable status non-terminal due to an exception inside its handler.

## 0.8.0-alpha.3 (2026-04-18)

### Added

- Project-discovery diagnostics: `Ionide.ProjInfo.IWorkspaceLoader.Notifications` is now
  subscribed during `discoverAndRegisterProjects` so per-project design-time failures
  (e.g. `ProjectNotRestored`, `ReferencesNotLoaded`) are logged instead of silently dropped
- Per-project FCS options dumped to `.fshw/logs/projinfo/<Project>.opts.txt` after every
  discovery pass. Contains source files, `OtherOptions` (incl. `-r:` references), and
  referenced project outputs. Registration log line now includes the `-r:` reference count
- `FSHW_PROJINFO_BINLOG=1` env var enables MSBuild binary-log capture at
  `.fshw/logs/projinfo/binlogs/<project>.binlog` for diffing design-time eval vs `dotnet build`
- `PathFilter` module with shared path filtering utilities:
  - `isGeneratedPath` — checks if a path is inside obj/ or bin/ directories
  - `isExcludedPath` — gitignore-style glob matching via the `Ignore` package (replaces string-contains matching)
  - `loadIgnoreFile` / `collectIgnoreRules` — load .gitignore and .fantomasignore files and combine into a single predicate
  - `IgnoreFilterCache` — caches ignore rules per repo root, auto-reloads when files change on disk
- `excludePatterns` parameter on `Daemon.create` / `Daemon.createWith` — exclude entire project trees from discovery using gitignore-style globs
- `CheckPipeline.RegisterProject` filters out generated files in obj/ and bin/ directories

### Changed

- `Daemon.performScan` takes `BatchContext` instead of 12 individual parameters
- Path filtering across Watcher, CheckPipeline, and Daemon consolidated through `PathFilter` module

### Dependencies

- Added `Ignore` 0.2.1 (gitignore-style pattern matching, same package used by Fantomas)

---

## 0.5.0-alpha.1 (2026-04-12)

### Added

- Enable TransparentCompiler for hash-based deterministic FCS caching (`useTransparentCompiler = true`)
- Parse `#nowarn` directives to suppress FCS TransparentCompiler warnings (workaround for dotnet/fsharp#9796)
- Plugin teardown support in `PluginHandler` (disposes semaphores, CTS, DB handles)

### Changed (Breaking)

- Type safety overhaul: `AbsFilePath`/`AbsProjectPath` single-case DUs replace raw strings; `PluginName` single-case DU with uniqueness check; `ContentHash` wrapper; `CommandOutcome` DU replaces `Succeeded: bool` + `Output: string`; `FileCheckState` DU replaces `CheckResults option`; `AffectedTestsState` DU; `RerunIntent` DU; `Set<SubscribedEvent>` replaces `PluginSubscriptions` bool record; `TaskCacheKey` struct replaces string key; `TestExtensionKind` DU; `CacheClearFilter` DU
- Plugin registration uses `PluginHostServices` record instead of multi-param function
- `Daemon` changed from F# record to class with `internal` constructor
- `IProjectGraphReader` interface decouples `BuildPlugin` from mutable `ProjectGraph`
- `BuildPhase` folds `PendingFiles` into `IdlePhase` (only meaningful when idle)

### Improved

- Extract pure filtering functions from `MacFsEvents` for testability
- `Watcher` accepts injectable `isMacOS` flag for cross-platform testability

### Changed (Breaking)

- `IProjectGraphReader` adds `GetProjectsForFile` method returning `AbsProjectPath list`
- `ProjectGraph.fileToProjects` now stores all projects per file (was `fileToProject` storing one)
- `CheckPipeline.projectOptionsByFile` stores all project options per file (list instead of single)
- New `CheckPipeline.CheckFileWithOptions` method for checking a file with explicit project options
- New `CheckPipeline.GetProjectOptions` method

### Fixed

- Propagate cancellation token into `CheckFileCore` — `CancelPreviousCheck` now actually stops in-flight FCS checks (previously only checked at entry, not around the expensive FCS call)
- Handle shared source files (linked items): a file appearing in multiple projects now triggers re-checks in all projects, not just the last-registered one
- `Daemon` implements `IDisposable` and stops all internal `MailboxProcessor` agents on dispose — agents previously ran indefinitely, keeping processes alive after tests
- `RunWithIpc` races initial scan against cancellation to prevent test-process hangs when `cts` is cancelled during slow `ScanAll`
- Standalone files not in any project now checked via uncovered-files fallback

---

## 0.3.0-alpha.1 (2026-04-08)

Infrastructure and tooling release. No public API changes.

- CLI moved under core's shared tag in `semantic-tagger.json` — CLI now versions and releases together with the core package
- Bump internal tooling: `coverageratchet` 0.10.0-alpha.1, `syncdocs` 0.10.0-alpha.1, `fssemantictagger` 0.10.0-alpha.1, `fsprojlint` 0.7.0-alpha.1

---

## 0.2.0-alpha.1 (2026-04-07)

Packaging and infrastructure release. No public API changes beyond dependency bumps.

- Add MIT license; add SourceLink; replace bespoke scripts with shared NuGet tools and reusable CI workflows
- Bump `TestPrune.Core` 0.1.0-beta.1 → 1.0.1
- Bump `Ionide.ProjInfo` / `Ionide.ProjInfo.FCS` 0.68.0 → 0.74.2
- Bump `FSharp.Data.Adaptive` 1.2.16 → 1.2.26

### Migration from 0.1.0-alpha.3

- Update `TestPrune.Core` dependency to 1.0.1 (check for API changes in that library)
- Update `Ionide.ProjInfo` to 0.74.2 (may affect project loading behavior)
- No FsHotWatch API signature changes

---

## 0.1.0-alpha.3 (2026-03-28 → 2026-04-02)

Severity-aware diagnostics, FCS warning reporting, MSBuild diagnostic parsing.

- **Breaking:** `ErrorLedger.HasErrors()` removed → use `HasFailingReasons(warningsAreFailures: bool)`
- **Breaking:** `ErrorLedger.Count()` removed → use `FailingReasons(warningsAreFailures: bool)` which returns `Map<string, (string * ErrorEntry) list>`
- **Breaking:** `PluginHost.HasErrors()` removed → use `HasFailingReasons(warningsAreFailures: bool)`
- **Breaking:** `PluginHost.ErrorCount()` removed → use `FailingReasons(warningsAreFailures: bool)`
- **Breaking:** IPC method `GetErrors` renamed to `GetDiagnostics` (both server and client)
- **Breaking:** `Daemon.create` gains `fcsSuppressedCodes: int list option` parameter (pass `None` for default — suppresses FS1182)
- **Behavioral change:** FCS now reports all diagnostic severities (Error, Warning, Info, Hidden), not just errors. Warnings will appear in the error ledger. Use `--no-warn-fail` or filter by severity if this is unwanted.
- Add `ErrorEntry.isFailing` helper for severity-aware failure checks

### Migration from 0.1.0-alpha.2

```fsharp
// ErrorLedger: before
ledger.HasErrors()
ledger.Count()

// ErrorLedger: after
ledger.HasFailingReasons(false)       // errors only
ledger.HasFailingReasons(true)        // errors + warnings
ledger.FailingReasons(false)          // get failing entries

// PluginHost: same pattern
host.HasFailingReasons(false)
host.FailingReasons(false)

// IPC client: before
IpcClient.getErrors proxy

// IPC client: after
IpcClient.getDiagnostics proxy

// Daemon.create: add fcsSuppressedCodes parameter
Daemon.create(config, plugins, fcsSuppressedCodes = None)
```

**Behavioral change:** FCS warnings now appear in the error ledger. If your workflow only expected errors, either:
- Pass `warningsAreFailures = false` to `HasFailingReasons`/`FailingReasons`
- Use `--no-warn-fail` CLI flag
- Filter `ErrorEntry` items by severity

---

## 0.1.0-alpha.2 (2026-03-28)

Subcommands, `--run-once` mode, `CommandCompleted` events, build dependencies, config enhancements.

- **Breaking:** `PluginEvent<'Msg>` gains `CommandCompleted of CommandCompletedResult` case — exhaustive matches must handle it
- **Breaking:** `PluginSubscriptions` gains `CommandCompleted: bool` field — direct construction must include it (or use `PluginSubscriptions.none`)
- **Breaking:** `PluginCtx` gains `EmitCommandCompleted: CommandCompletedResult -> unit` field
- **Breaking:** `CachedEvent` gains `CachedCommandCompleted` case
- Add `CommandCompletedResult` type and full event pipeline
- Add `Daemon.RunOnce()` for single-pass in-process mode

### Migration from 0.1.0-alpha.1

```fsharp
// PluginSubscriptions: add CommandCompleted field
{ PluginSubscriptions.none with FileChanged = true; CommandCompleted = false }
```

Handle new union cases in exhaustive matches:
```fsharp
match event with
| FileChanged _ -> ...
| BuildCompleted _ -> ...
| CommandCompleted _ -> ()  // new
// etc.
```

---

## 0.1.0-alpha.1 (2026-03-21)

Initial alpha release.

- Daemon with FSharpChecker warm cache
- File watcher with source/project/solution change detection
- Plugin host with event dispatch and command registration
- IPC server/client over named pipes (StreamJsonRpc)
- Preprocessor pipeline (format-on-save runs before events dispatch)
- Debounced file changes (500ms source, 200ms project)
- ProjectGraph for cross-project dependency tracking
- TaskCache for event deduplication
