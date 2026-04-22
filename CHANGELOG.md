# Changelog

All notable changes to FsHotWatch packages are documented here.

## Unreleased

### FsHotWatch

#### Added
- `PathFilter` module — shared path filtering with gitignore-style glob matching (via `Ignore` 0.2.1 package)
- `excludePatterns` parameter on `Daemon.create` / `Daemon.createWith` for excluding project trees from discovery
- `CheckPipeline.RegisterProject` filters out generated files in obj/ and bin/
- `IgnoreFilterCache` — caches .gitignore/.fantomasignore rules, auto-reloads on file changes
- `TaskCache.saltedCacheKey` / `optionalSaltedCacheKey` — cache-key builders that fold a per-event salt into the commit-based key, for plugins whose cache validity depends on state beyond the commit

#### Fixed
- `PluginFramework.registerHandler` now auto-reports `Failed(ex.Message, now)` when a handler's `Update` throws. Previously an uncaught throw after `ReportStatus(Running)` left the plugin stuck displaying `Running` indefinitely. Structural: no plugin author can forget it; impossible for a throw to leave the observed status non-terminal.

### FsHotWatch.Coverage

#### Fixed
- Cache key now carries a tristate salt derived from the thresholds file (absent / unreadable / content SHA-256), so editing `coverage-ratchet.json` under the same commit invalidates the cached plugin status, and a transient IO error on the thresholds file no longer presents as "file absent" to the cache

#### Changed
- `performScan` takes `BatchContext` instead of 12 individual parameters
- Path filtering consolidated through `PathFilter` module (Watcher, CheckPipeline, Daemon)

### FsHotWatch.Cli

#### Added
- `exclude` config field in `.fs-hot-watch.json` — gitignore-style glob patterns to exclude project trees

#### Fixed
- `start` is a singleton per repo, enforced by an OS exclusive lock on `.fs-hot-watch/daemon.lock` held for the daemon's lifetime; concurrent invocations cannot both proceed. Second invocation exits 0 with "Daemon already running at pipe <name> (pid <n>)".
- `stop` drains until the pipe is observed quiet for two consecutive probes (30 s overall timeout), cleanly taking down any number of historically-accumulated duplicate daemons and no longer misreporting "No daemon running" during pipe tear-down.

### Tests / CI

#### Changed
- Split end-to-end FCS / analyzer / lint / format / build tests into a new `tests/FsHotWatch.IntegrationTests` project. These tests hit SDK-reflection paths that fire nondeterministically across runs, so letting them contribute to coverage made the ratchet flaky. They still run via `mise run test-integration`; the main `test-direct` coverage now only aggregates `FsHotWatch.Tests`.
- `AnalyzersPlugin` grew two extracted helpers — `isKnownNonAnalyzerPrefix` and `buildAnalyzerProjectOptions` — with deterministic unit tests covering all branches the live-SDK integration tests used to hit flakily.

### FsHotWatch.TestPrune

#### Fixed
- **Stuck-state bug**: `flushAndQueryAffected` call sites in `BuildCompleted` and `TestsFinished (RerunQueued)` were unguarded; a DB hiccup pinned the plugin in `Running` forever. Both now report `Failed` and transition back to `TestsIdle` on exception.
- **Schema-drift self-heal**: SQLite "no such column" errors on a stale cache DB now trigger automatic deletion of the DB file with a warning, so the caller no longer has to know which file to remove.

### FsHotWatch.Fantomas

#### Added
- Format preprocessor and format-check plugin respect `.gitignore` and `.fantomasignore`

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
