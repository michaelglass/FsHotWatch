# Changelog — FsHotWatch (core)

## Unreleased

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
