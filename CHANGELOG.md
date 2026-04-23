# Changelog

All notable changes to FsHotWatch packages are documented here.

## Unreleased

### FsHotWatch.Coverage

#### Removed
- **BREAKING:** Package retired. Coverage enforcement now flows through `fileCommands afterTests` in FsHotWatch.Cli, invoking an external CLI (e.g. `coverageratchet`).

### FsHotWatch.FileCommand

#### Added
- `afterTests` trigger: react to `TestCompleted` events, optionally filtered by test project names.

#### Changed
- **BREAKING:** `FileCommandPlugin.create` takes a `CommandTrigger` record instead of positional `fileFilter` + `runOnStart` args.
- `afterTests` list-form fires iff **every** listed project appears in `TestResults.Results`. Paired with TestPrune's progressive cumulative emission (below), the command fires exactly once per batch and is unblocked by slow non-listed groups.

#### Removed
- `runOnStart` config/API field.

### FsHotWatch.TestPrune

#### Changed
- **BREAKING:** `TestCompleted` emits progressively once per group with a cumulative prefix-chain of all projects completed so far, rather than once at batch end. Fixes downstream plugins being blocked by the slowest group.

### FsHotWatch.Cli

#### Added
- `--agent` / `-a` global flag for AI-agent-friendly parseable output: banner, `name: state [summary="..."]` per non-idle plugin, state-aware `next:` hint. States: `ok | fail | warn | running`. No ANSI.

#### Removed
- **BREAKING:** `coverage` config block.

#### Changed
- **BREAKING:** `--compact` / `-q` promoted to a global flag. `fs-hot-watch check -q` → `fs-hot-watch -q check`. Now accepted on every subcommand (including `status` and `errors`), matching the placement of `--verbose` and `--agent`.
- `fileCommands` entries accept `name` and `afterTests`; validation requires at least one of `pattern` / `afterTests` and an explicit `name` when `afterTests` is set.
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
