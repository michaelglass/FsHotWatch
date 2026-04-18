# Changelog — FsHotWatch (core)

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
