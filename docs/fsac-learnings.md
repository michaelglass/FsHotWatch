# Learnings from FsAutoComplete

Things FsHotWatch could adopt, experiment with, or learn from FsAutoComplete (FSAC).
Organized roughly by area. Items marked **(experiment)** mean "try it and compare
with the existing implementation before committing."

## FSharpChecker Configuration

- [ ] Add `suggestNamesForErrors = true` to FSharpChecker.Create — FSAC enables this
      to get "did you mean X?" suggestions on unresolved identifiers. Free diagnostics
      improvement. (`Daemon.fs:909`)
- [ ] Add `keepAllBackgroundSymbolUses = true` — FSAC enables this for find-all-references.
      Even without LSP, plugins could use symbol-use data for impact analysis.
      (`Daemon.fs:909`)
- [ ] Add `enableBackgroundItemKeyStoreAndSemanticClassification = true` — enables
      background indexing of items for faster lookups. FSAC uses this for completion
      and semantic tokens. Could benefit AnalyzersPlugin. (`Daemon.fs:909`)
- [ ] Add `captureIdentifiersWhenParsing = true` — makes identifiers available from
      parse results without a full check. Useful for fast approximate analysis.
      (`Daemon.fs:909`)
- [ ] **(experiment)** Try `enablePartialTypeChecking = true` when analyzers are not
      loaded. FSAC uses this to skip full assembly generation when analyzers don't
      need assembly contents. Measure check-time difference. (`Daemon.fs:909`)
- [ ] **(experiment)** Try `useTransparentCompiler = true` (FCS's newer incremental
      compiler). FSAC tests both modes in CI. Measure check times and memory usage
      vs BackgroundCompiler. May require building FSharpProjectSnapshot instead of
      FSharpProjectOptions. (`Daemon.fs:908-914`)
- [ ] Make `keepAssemblyContents` conditional on whether analyzers are loaded (FSAC
      does `keepAssemblyContents = hasAnalyzers`). Saves memory when no analyzers
      registered. (`Daemon.fs:911`)
- [ ] Make `projectCacheSize` configurable rather than hardcoded 200. Different repo
      sizes benefit from different values. (`Daemon.fs:910`)
- [ ] **(experiment)** Try passing `transparentCompilerCacheSizes` when using
      TransparentCompiler mode. FSAC defaults to 10.

## Concurrency & Parallelism

- [ ] Implement `Async.parallel75` — cap concurrent type-checks at
      `Max(1, Floor(ProcessorCount * 0.75))`. FSAC does this to leave headroom for
      the OS and editor. Currently FsHotWatch uses unbounded `Async.Parallel` per
      tier which can cause memory pressure on high-core machines.
      (FSAC `Utils.fs:243-247`)
- [ ] Add a `SemaphoreSlim`-based typecheck limiter in CheckPipeline, similar to
      FSAC's `typecheckLocker`. This would bound concurrent FCS calls across all
      scan tiers.
- [ ] Add a script-file lock (`SemaphoreSlim(1,1)`) to serialize `.fsx` checking.
      FSAC does this because FCS has known issues with concurrent script checking.
      (FSAC `CompilerServiceInterface.fs:115`)
- [ ] Add per-file cancellation that propagates to dependent files. FSAC creates
      linked CancellationTokenSources so that when a root file changes, all
      dependent file checks are automatically cancelled.
      (FSAC `AdaptiveServerState.fs:2551-2561`)

## File-Level Dependency Tracking

- [ ] Implement file-order-based dependency tracking within projects. When file X
      changes, only files *after* X in the source file array need re-checking (F#'s
      single-pass compilation model). Currently FsHotWatch only tracks project-level
      dependencies, re-checking entire dependent projects. This is the single biggest
      efficiency improvement available.
      (FSAC `State.fs:42-48`, `SourceFilesThatDependOnFile`)
- [ ] Also implement `SourceFilesThatThisFileDependsOn` (files before index) for
      use in symbol analysis / test pruning. (FSAC `State.fs:35-39`)
- [ ] Add cross-project dependent file discovery. FSAC's
      `GetDependentProjectsOfProjects` does transitive project-level dependency
      lookup. FsHotWatch has this in ProjectGraph but should combine it with
      file-level tracking for precision.

## Caching

- [ ] **(experiment)** Use `WeakReference<FileCheckResult>` in InMemoryCheckCache
      instead of strong references. FSAC does this so GC can reclaim memory under
      pressure while keeping results available when memory permits. Measure memory
      usage of long-running daemon. (FSAC `CompilerServiceInterface.fs:519`)
- [ ] Add sliding expiration to cache entries (FSAC uses 5-minute sliding expiration
      via `MemoryCacheEntryOptions`). Prevents indefinite memory growth for files
      that were checked once and never touched again.
      (FSAC `CompilerServiceInterface.fs:517`)
- [ ] **(experiment)** Replace InMemoryCheckCache's single-lock `Dictionary` with
      `Microsoft.Extensions.Caching.Memory.MemoryCache` (what FSAC uses). It
      handles eviction, size limits, and sliding expiration out of the box.
- [ ] Add source-text-content-based cache keys. FSAC uses source text hashes to
      determine if cached check results are still valid
      (`TryGetRecentCheckResultsForFile`). More robust than timestamp-based keys
      when files are touched without content changes.
      (FSAC `CompilerServiceInterface.fs:608-652`)
- [ ] Implement two-tier cache warming: on startup, read FileCheckCache entries and
      populate InMemoryCheckCache for files whose content hasn't changed. Currently
      FileCheckCache is write-through only — cold starts always require full FCS
      re-checks.

## Built-in Lightweight Analyzers

- [ ] Add unused-opens detection as a built-in check (FSAC's FSAC0001). Uses
      `FSharp.Compiler.EditorServices.UnusedOpens.getUnusedOpens` which only needs
      check results + source text. Very cheap to run after a check completes.
      (FSAC `AdaptiveServerState.fs:474-491`)
- [ ] Add unused-declarations detection (FSAC's FSAC0003). Uses
      `UnusedDeclarations.getUnusedDeclarations` from FCS editor services.
      (FSAC `AdaptiveServerState.fs:493-514`)
- [ ] Add simplifiable-names detection (FSAC's FSAC0002). Uses
      `SimplifyNames.getSimplifiableNames` from FCS editor services.
      (FSAC `AdaptiveServerState.fs:516-534`)
- [ ] Add unnecessary-parentheses detection (FSAC's FSAC0004). AST-based fold over
      parse tree — no check results needed, just parse results.
      (FSAC `AdaptiveServerState.fs:536-565`)
- [ ] Make each built-in analyzer independently configurable (enable/disable per
      analyzer code). FSAC lets clients configure which analyzers run.
- [ ] Run built-in analyzers in parallel after check completes (FSAC uses
      `Async.parallel75` for these).

## Project Loading

- [ ] **(experiment)** Try `WorkspaceLoaderViaProjectGraph` instead of
      `WorkspaceLoader`. FSAC supports both; the graph-based loader is faster for
      large solutions because it uses MSBuild's static graph evaluation.
      (FSAC `Parser.fs:126`)
- [ ] Track transitive project assets for invalidation — `packages.lock.json`,
      `.props` files in `obj/` folder. FSAC watches these via
      `AdaptiveFile.GetLastWriteTimeUtc` and triggers project reload when they
      change. Currently FsHotWatch only fingerprints `.fsproj` files directly.
      (FSAC `AdaptiveServerState.fs:1063-1086`)
- [ ] Add `.props` file tracking to the file watcher. Changes to `Directory.Build.props`
      or `Directory.Packages.props` should trigger project re-evaluation, not just
      file re-checking.
- [ ] Improve fsproj fingerprinting: include file size alongside mtime. Some
      filesystems have 1-second mtime resolution, which can miss rapid changes.
      (`Daemon.fs:66`)
- [ ] Resolve relative file paths to absolute before passing to FCS (FSAC does this
      explicitly in `resolveRelativeFilePaths`). Script compilation fails with
      relative paths. (FSAC `CompilerServiceInterface.fs:207-215`)
- [ ] Handle FSharp.Core / FSI path fixup. FSAC has explicit logic to replace
      FSharp.Core.dll and FSharp.Compiler.Interactive.Settings.dll paths in project
      options with SDK-discovered paths. This avoids version mismatch issues.
      (FSAC `CompilerServiceInterface.fs:143-182`)

## Diagnostics & Error Reporting

- [ ] Add diagnostic source categorization. FSAC groups diagnostics by source
      (compiler, unused opens, analyzers, etc.) with distinct codes (FSAC0001-0004).
      FsHotWatch's ErrorLedger could tag entries with a source category for
      better filtering.
- [ ] Add error deduplication within a single check. If the same diagnostic appears
      multiple times for the same file+location, only report it once.
- [ ] Clear errors for deleted/removed files. Currently ErrorLedger only clears errors
      when a file is re-checked and passes. If a file is deleted, stale errors persist.
- [ ] Add error expiration/TTL. For a long-running daemon, errors from files that
      haven't been touched in hours should eventually be cleared or flagged as stale.
- [ ] Version the error ledger state for atomic snapshots. Currently concurrent reads
      during updates may see partial state.

## Observability & Tracing

- [ ] Add OpenTelemetry tracing support. FSAC instruments all major operations
      (type-checking, project loading, dependent file analysis) with
      `System.Diagnostics.ActivitySource`. This enables Jaeger/Zipkin traces for
      debugging performance issues. (FSAC `Parser.fs:13-16`,
      `AdaptiveServerState.fs:1784`)
- [ ] Add check-time metrics per file. Track min/max/avg/p99 check times. Currently
      only logs when a check exceeds a hardcoded 2.0s threshold.
      (`CheckPipeline.fs:158`)
- [ ] Make the slow-file threshold configurable instead of hardcoded 2.0 seconds.
      (`CheckPipeline.fs:158`)
- [ ] Add cache hit/miss rate tracking. Essential for tuning cache sizes and
      eviction policies.
- [ ] Add project-load time metrics. Track how long MSBuild evaluation takes per
      project.
- [ ] Emit structured log events for scan start/end, file check start/end, plugin
      dispatch, and preprocessor execution. FSAC uses structured logging extensively
      via LogProvider.

## Configuration

- [ ] Make debounce delays configurable (currently hardcoded: 500ms source, 200ms
      project). Large monorepos may need longer delays; fast iteration on small
      projects needs shorter. (`Daemon.fs:657-658`)
- [ ] Make WaitForAllTerminal timeout configurable (currently hardcoded 30 minutes).
      CI systems may need different values. (`Daemon.fs:499`)
- [ ] Make FSEvents latency configurable on macOS (currently hardcoded 0.05s).
      (`MacFsEvents.fs:281`)
- [ ] Make the file hash cache have a max size. Currently unbounded
      ConcurrentDictionary grows forever for repos with many files.
      (`Watcher.fs:10`)
- [ ] Make watched file extensions configurable. Currently hardcoded to
      .fs/.fsx/.fsproj/.sln/.slnx/.props. Users may want to watch .editorconfig
      or other files. (`Watcher.fs:48-53`)
- [ ] Add a max-concurrent-checks configuration option (ties into the parallel75
      concurrency limiter above).

## Process Management

- [ ] Add process timeout to ProcessHelper.runProcess. Long-running build commands
      can hang indefinitely, blocking the daemon. (`ProcessHelper.fs:24`)
- [ ] Add process group management — terminate child processes when the parent is
      killed. Prevents orphaned build/test processes.
- [ ] Separate stdout and stderr in process output. Currently they're merged, making
      it hard to distinguish errors from warnings. (`ProcessHelper.fs:25`)
- [ ] Add process execution logging (command, args, working directory, duration).
      Makes debugging build/test failures much easier.

## Error Handling & Resilience

- [ ] Add per-file and per-tier scan timeouts. If a single file check hangs, skip
      it and continue with the remaining files instead of blocking the entire scan.
- [ ] Add plugin restart on repeated failures. Currently a plugin that throws in its
      update function has the exception caught and logged, but the plugin continues
      with its old state. After N failures, restart the plugin agent.
      (`PluginFramework.fs:134`)
- [ ] Distinguish "file not registered" from "check failed" in CheckPipeline.
      Currently both return `None` with only debug logging. Callers can't tell the
      difference. (`CheckPipeline.fs:119`)
- [ ] Add preprocessor error recovery. If a preprocessor fails, continue with
      remaining preprocessors instead of skipping them all.
      (`PluginHost.fs:92`)
- [ ] Add CancellationTokenSource leak protection in CancelPreviousCheck. If an
      exception occurs between creating a new CTS and storing it, the token leaks.
      Wrap in try/finally. (`CheckPipeline.fs:85-103`)
- [ ] Adopt FSAC's `CancellationTokenSource.TryCancel()` / `TryDispose()` pattern
      that swallows ObjectDisposedException and NullReferenceException. Defensive
      against race conditions in cancellation cleanup.
      (FSAC `AdaptiveExtensions.fs:19-32`)

## ProjectGraph Improvements

- [ ] Cache transitive dependents. Currently `GetTransitiveDependents` recomputes
      via DFS on every call. Cache the result and invalidate when
      `RegisterProject` is called. (`ProjectGraph.fs:122-142`)
- [ ] Optimize `GetParallelTiers` from O(n^3) to O(n) via topological sort with
      Kahn's algorithm. Current implementation rebuilds remaining list and checks
      dependencies for each tier. (`ProjectGraph.fs:156-171`)
- [ ] **(experiment)** Add NuGet-only vs project-reference distinction in the
      dependency graph. NuGet reference changes don't need transitive re-checking
      of the same kind as source-level project references.

## IPC Improvements

- [ ] Make connection timeouts configurable (currently hardcoded 5000ms connect,
      500ms probe). (`Ipc.fs:251,308`)
- [ ] Add connection pooling on the client side. Currently each RPC call creates a
      new pipe connection, which is expensive for rapid-fire calls (e.g., in tests).
      (`Ipc.fs:248-249`)
- [ ] Add a heartbeat/keepalive for long-running operations like WaitForScan. Clients
      in network-restricted environments may time out.

## Memory Management

- [ ] Profile memory usage of long-running daemon (8h+ session). Identify the biggest
      memory holders. FSAC found that check results and project options dominate.
- [ ] Add GC.Collect hint after large scan completes (tier-based parallel checking
      can allocate significant temporary objects).
- [ ] Consider periodic cache trimming independent of LRU eviction — e.g., halve
      the cache if process memory exceeds a threshold.
- [ ] Evict file content hashes for deleted files. The watcher's `fileHashes`
      ConcurrentDictionary never removes entries. (`Watcher.fs:10`)

## Script (.fsx) Handling

- [ ] Serialize .fsx file checking with a dedicated semaphore. FSAC discovered that
      FCS has issues with concurrent script processing.
      (FSAC `CompilerServiceInterface.fs:113-115`)
- [ ] Use `FSharpChecker.GetProjectOptionsFromScript` / `GetProjectSnapshotFromScript`
      for .fsx files instead of trying to find a parent .fsproj. Scripts have their
      own dependency resolution model.
      (FSAC `CompilerServiceInterface.fs:273-295`)
- [ ] Handle `--use:` and `--load:` directives in script files. FSAC parses FSI
      arguments to discover additional source files that should be included in the
      script's project options. (FSAC `CompilerServiceInterface.fs:191-197`)

## Testing

- [ ] **(experiment)** Test with both BackgroundCompiler and TransparentCompiler modes
      in CI (FSAC runs its full test suite against both modes in its CI matrix).
      (FSAC `.github/workflows/build.yml:31-33`)
- [ ] **(experiment)** Test with both `WorkspaceLoader` and
      `WorkspaceLoaderViaProjectGraph` in CI.
- [ ] Add benchmarks for check pipeline: measure check time, cache hit rate, and
      memory usage per file for a representative project. FSAC lacks formal
      benchmarks too, but FsHotWatch's daemon model makes it easier to measure.

## Sidecar / Integration Opportunities

- [ ] Expose warm check results via a shared file-based cache that FSAC could read
      on startup. This would give editors instant startup by reading pre-computed
      results from the running FsHotWatch daemon.
- [ ] Add an LSP-compatible diagnostics output mode. FsHotWatch could emit diagnostics
      in LSP format over a named pipe, allowing editors to subscribe to real-time
      diagnostics without FSAC doing the checking.
- [ ] Expose a "check result ready" notification channel. FSAC or other tools could
      subscribe to be told when a file's check results are fresh, then pull them
      from a shared cache.
