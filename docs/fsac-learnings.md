# Learnings from FsAutoComplete

Things FsHotWatch could adopt, experiment with, or learn from FsAutoComplete (FSAC).
Items marked **(experiment)** mean "try it and compare with the existing implementation."

Each item is scored on three axes:
- **S** Simpler — does this reduce code, delete custom logic, or remove a class of bug?  `++` `+` `0` `-`
- **M** Maintainable — does this make future changes easier or reduce surprise?  `++` `+` `0` `-`
- **R** Result — how much better does the tool get for users?  `★★★` `★★☆` `★☆☆`

---

## Tier 1 — Quick wins: low effort, high payoff

These are either one-liners or small isolated changes with outsized impact.

- [ ] **Script checking semaphore** · Add `SemaphoreSlim(1,1)` in CheckPipeline to
      serialize `.fsx` file checks. FSAC discovered FCS has known concurrency issues
      with script processing — concurrent .fsx checks can corrupt internal FCS state.
      (`Daemon.fs:909`, FSAC `CompilerServiceInterface.fs:113-115`)
      **S** `+`  **M** `+`  **R** `★★★`

- [ ] **Process timeout** · Add a timeout parameter to `ProcessHelper.runProcess`.
      A hung `dotnet build` or test runner currently blocks the daemon indefinitely.
      (`ProcessHelper.fs:24`)
      **S** `0`  **M** `+`  **R** `★★★`

- [ ] **`keepAssemblyContents` conditional on analyzers** · Change the hardcoded
      `keepAssemblyContents = true` to `keepAssemblyContents = hasAnalyzers`. FCS
      holds full assembly IL in memory when this is true — unnecessary unless an
      analyzer actually needs it. One-line change, meaningful memory saving for users
      without analyzers. (`Daemon.fs:911`, FSAC `CompilerServiceInterface.fs:99`)
      **S** `+`  **M** `+`  **R** `★★☆`

- [ ] **`Async.parallel75`** · Cap concurrent type-checks at
      `Max(1, Floor(ProcessorCount * 0.75))`. Currently FsHotWatch passes all files
      in a tier to `Async.Parallel` without a concurrency limit, which can allocate
      N×(check overhead) simultaneously on machines with many cores. Three lines to
      implement. (FSAC `Utils.fs:243-247`)
      **S** `+`  **M** `+`  **R** `★★☆`

- [ ] **`CancellationTokenSource.TryCancel()` / `TryDispose()`** · Adopt FSAC's
      extension methods that swallow `ObjectDisposedException` and
      `NullReferenceException`. Defensive against races in cancellation cleanup.
      Add once, use everywhere. (FSAC `AdaptiveExtensions.fs:19-32`)
      **S** `+`  **M** `++`  **R** `★☆☆`

- [ ] **CTS leak protection in `CancelPreviousCheck`** · Wrap the create-and-store
      sequence in `try/finally` so a token isn't leaked if an exception occurs between
      creating the new `CancellationTokenSource` and inserting it into `fileTokens`.
      (`CheckPipeline.fs:85-103`)
      **S** `0`  **M** `++`  **R** `★☆☆`

- [ ] **`suggestNamesForErrors = true`** · One-line addition to `FSharpChecker.Create`.
      FCS will include "did you mean X?" suggestions in diagnostics for unresolved
      identifiers. Free improvement with no downside. (`Daemon.fs:909`)
      **S** `0`  **M** `0`  **R** `★★☆`

- [ ] **Fsproj fingerprint includes file size** · Currently fingerprinted by
      `(path, lastWriteTimeTicks)` only. Some filesystems have 1-second mtime
      resolution. Adding file size costs nothing and catches missed changes.
      (`Daemon.fs:66`)
      **S** `0`  **M** `+`  **R** `★☆☆`

- [ ] **Preprocessor error recovery** · If one preprocessor throws, currently the
      exception propagates and skips remaining preprocessors. Catch per-preprocessor
      and continue. (`PluginHost.fs:92`)
      **S** `0`  **M** `+`  **R** `★★☆`

- [ ] **Distinguish "not registered" from "check failed"** · `CheckPipeline.CheckFile`
      returns `None` for both cases with only debug logging. Return a discriminated
      union so callers and plugins can tell the difference. (`CheckPipeline.fs:119`)
      **S** `0`  **M** `++`  **R** `★☆☆`

- [ ] **Process execution logging** · Log command, args, working directory, and
      duration in `ProcessHelper`. Build failures are currently opaque to diagnose.
      (`ProcessHelper.fs`)
      **S** `0`  **M** `++`  **R** `★★☆`

- [ ] **Make slow-file threshold configurable** · Currently hardcoded to 2.0 seconds.
      (`CheckPipeline.fs:158`)
      **S** `0`  **M** `+`  **R** `★☆☆`

---

## Tier 2 — Meaningful changes requiring a focused session

- [ ] **Replace `InMemoryCheckCache` with `MemoryCache`** · The current implementation
      uses a hand-rolled LRU behind a single lock. `Microsoft.Extensions.Caching.Memory`
      handles eviction, size limits, sliding expiration, and weak references out of the
      box. Deletes ~80 lines of custom locking code. (FSAC `CompilerServiceInterface.fs:119-122`)
      **S** `++`  **M** `++`  **R** `★★☆`

- [ ] **Sliding cache expiration** · Add 5-minute sliding expiration to check-result
      cache entries (or whatever `MemoryCache` provides after the item above).
      Prevents indefinite memory growth for files that were checked once and never
      touched again. (FSAC `CompilerServiceInterface.fs:517`)
      **S** `+`  **M** `+`  **R** `★★☆`

- [ ] **File-level dependency tracking within projects** · When file X changes, only
      files *after* X in the source-file array need re-checking — F#'s single-pass
      compilation model guarantees earlier files can't depend on later ones. Currently
      FsHotWatch re-checks the entire dependent *project*. Implement
      `SourceFilesThatDependOnFile` (FSAC `State.fs:42-48`) and use it in the scan
      loop. This is the single biggest efficiency improvement available.
      **S** `-`  **M** `0`  **R** `★★★`

- [ ] **Script project options via `GetProjectOptionsFromScript`** · Currently .fsx
      files are checked by trying to find a parent .fsproj, which is wrong — scripts
      have their own dependency resolution including `#r` and `#load` directives. Use
      `FSharpChecker.GetProjectOptionsFromScript` instead.
      (FSAC `CompilerServiceInterface.fs:273-295`)
      **S** `+`  **M** `++`  **R** `★★★`

- [ ] **Track `packages.lock.json` and `obj/*.props` for project invalidation** ·
      FSAC watches these files alongside `.fsproj`. NuGet restore changes and SDK
      props changes currently go undetected by FsHotWatch, causing stale project
      options. (FSAC `AdaptiveServerState.fs:1063-1086`)
      **S** `-`  **M** `0`  **R** `★★☆`

- [ ] **Separate stdout and stderr in `ProcessHelper`** · Currently merged into a
      single string, making it impossible for BuildPlugin to distinguish warnings
      from errors or structured output from log noise. (`ProcessHelper.fs:25`)
      **S** `0`  **M** `+`  **R** `★★☆`

- [ ] **Cache transitive dependents in `ProjectGraph`** · `GetTransitiveDependents`
      re-runs DFS on every call. Cache after `RegisterProject` and invalidate on
      the next call. (`ProjectGraph.fs:122-142`)
      **S** `0`  **M** `+`  **R** `★★☆`

- [ ] **Clear errors for deleted/removed files** · `ErrorLedger` only clears a file's
      errors when that file is re-checked and passes. If a file is deleted, its errors
      persist forever. Hook into the file-watcher or scan loop to evict on deletion.
      **S** `0`  **M** `+`  **R** `★★☆`

- [ ] **Per-file and per-tier scan timeouts** · If a single file check hangs, the
      entire scan blocks. Add a configurable per-file timeout so a hung check is
      skipped with a warning and the scan continues.
      **S** `-`  **M** `0`  **R** `★★★`

- [ ] **Plugin restart on repeated failures** · Currently a plugin that throws N
      times continues silently with its stale state. After a configurable threshold,
      restart the agent with initial state and report the restart. (`PluginFramework.fs:134`)
      **S** `-`  **M** `+`  **R** `★★☆`

- [ ] **Resolve relative paths to absolute before FCS** · FSAC does this explicitly
      because script compilation fails with relative paths. Adopt the same defensive
      pass over `SourceFiles` and `OtherOptions`.
      (FSAC `CompilerServiceInterface.fs:207-215`)
      **S** `0`  **M** `+`  **R** `★★☆`

- [ ] **`#r` / `--use:` / `--load:` directive handling for scripts** · Parse FSI
      arguments to discover additional source files included via `--use:` or `--load:`
      and include them in the script's project options.
      (FSAC `CompilerServiceInterface.fs:191-197`)
      **S** `-`  **M** `0`  **R** `★★☆`

---

## Tier 3 — Worthwhile, lower urgency

- [ ] **(experiment)** **`enablePartialTypeChecking = true` when no analyzers** ·
      FSAC skips full assembly generation when analyzers are absent. Measure check
      time difference on a representative project. (`Daemon.fs:909`)
      **S** `0`  **M** `0`  **R** `★★☆` *(if experiment succeeds)*

- [ ] **(experiment)** **`useTransparentCompiler = true`** · FCS's newer incremental
      compiler. FSAC tests both modes in CI. Requires switching from
      `FSharpProjectOptions` to `FSharpProjectSnapshot`. Measure check times and
      memory usage. (`Daemon.fs:908-914`)
      **S** `-`  **M** `0`  **R** `★★★` *(if experiment succeeds)*

- [ ] **(experiment)** **`WorkspaceLoaderViaProjectGraph`** · Uses MSBuild's static
      graph evaluation, faster than the default `WorkspaceLoader` for large solutions.
      Try it and compare project-load time. (FSAC `Parser.fs:126`)
      **S** `0`  **M** `0`  **R** `★★☆` *(if experiment succeeds)*

- [ ] **`WeakReference<FileCheckResult>` in cache** · Lets the GC reclaim check
      results under memory pressure while keeping them available when memory permits.
      Important for long-running daemon sessions. (FSAC `CompilerServiceInterface.fs:519`)
      **S** `+`  **M** `0`  **R** `★★☆`

- [ ] **Content-based cache keys** · Use source-text hash instead of (or in addition
      to) mtime for cache validity. More robust when files are touched without content
      changes. (FSAC `CompilerServiceInterface.fs:608-652`)
      **S** `0`  **M** `+`  **R** `★★☆`

- [ ] **Configurable debounce delays** · Hardcoded 500ms/200ms. Large monorepos
      need longer; fast single-file iteration needs shorter. (`Daemon.fs:657-658`)
      **S** `0`  **M** `+`  **R** `★★☆`

- [ ] **File hash cache max size** · `fileHashes` `ConcurrentDictionary` never evicts.
      For large repos, this grows forever. Add a bounded LRU or switch to
      `MemoryCache`. (`Watcher.fs:10`)
      **S** `0`  **M** `+`  **R** `★★☆`

- [ ] **`Async.parallel75` for built-in post-check passes** · If/when lightweight
      analyzers are added (see tier 4), run them in parallel with this combinator
      rather than sequentially.
      **S** `0`  **M** `+`  **R** `★☆☆`

- [ ] **Optimize `GetParallelTiers` to O(n) via Kahn's algorithm** · Current
      implementation is O(n³). Only matters for solutions with 100+ projects.
      (`ProjectGraph.fs:156-171`)
      **S** `+`  **M** `++`  **R** `★☆☆`

- [ ] **Check-time metrics per file** · Track min/max/avg/p99 per file. Expose via
      `GetStatus` IPC so users can identify slow files.
      **S** `0`  **M** `++`  **R** `★★☆`

- [ ] **`keepAllBackgroundSymbolUses = true`** · Enables future symbol-use analysis
      in plugins (e.g. more precise test impact analysis). Memory cost. (`Daemon.fs:909`)
      **S** `0`  **M** `0`  **R** `★☆☆` *(enables future work)*

- [ ] **FSharp.Core path fixup** · Replace FSharp.Core.dll and FSI settings paths
      in project options with SDK-discovered paths. Avoids version mismatch on some
      SDK configurations. (FSAC `CompilerServiceInterface.fs:143-182`)
      **S** `0`  **M** `0`  **R** `★☆☆`

- [ ] **Make `WaitForAllTerminal` timeout configurable** · Hardcoded 30 minutes.
      (`Daemon.fs:499`)
      **S** `0`  **M** `+`  **R** `★☆☆`

- [ ] **Make FSEvents latency configurable** · Hardcoded 0.05s on macOS.
      (`MacFsEvents.fs:281`)
      **S** `0`  **M** `+`  **R** `★☆☆`

- [ ] **Evict deleted file hashes from watcher** · `fileHashes` never removes entries
      for deleted files. (`Watcher.fs:10`)
      **S** `0`  **M** `+`  **R** `★☆☆`

- [ ] **Process group management** · Terminate child processes when the daemon is
      killed. Prevents orphaned `dotnet build` / `dotnet test` processes.
      **S** `-`  **M** `0`  **R** `★★☆`

- [ ] **Dual-mode CI: BackgroundCompiler + TransparentCompiler** · Run the test
      suite against both FCS compiler modes (as FSAC does). Catches regressions
      before users hit them. (FSAC `.github/workflows/build.yml:31-33`)
      **S** `0`  **M** `++`  **R** `★☆☆`

- [ ] **Dual-loader CI: `WorkspaceLoader` + `WorkspaceLoaderViaProjectGraph`** ·
      Same rationale as above for project loading.
      **S** `0`  **M** `++`  **R** `★☆☆`

- [ ] **Check pipeline benchmarks** · Add a benchmark project measuring check time,
      cache hit rate, and memory per file on a representative solution. Required
      before the TransparentCompiler and partial-type-checking experiments are
      meaningful.
      **S** `0`  **M** `++`  **R** `★☆☆`

---

## Tier 4 — Experiments and future directions

High potential but significant effort or uncertainty.

- [ ] **Built-in lightweight analyzers** · Add unused-opens (FSAC0001),
      unused-declarations (FSAC0003), simplifiable-names (FSAC0002), and
      unnecessary-parentheses (FSAC0004) as built-in post-check passes using
      `FSharp.Compiler.EditorServices`. These run on check results and parse trees
      already in memory — no external process. Make each independently configurable.
      (FSAC `AdaptiveServerState.fs:474-565`)
      **S** `-`  **M** `0`  **R** `★★★`

- [ ] **Two-tier cache warming on startup** · Read `FileCheckCache` entries at daemon
      start and populate `InMemoryCheckCache` for files whose content hasn't changed.
      Eliminates cold-start re-checks — the daemon's biggest first-impression problem.
      **S** `-`  **M** `-`  **R** `★★★`

- [ ] **OpenTelemetry tracing** · Instrument type-checking, project loading, plugin
      dispatch, and scan phases with `System.Diagnostics.ActivitySource`. Enables
      Jaeger/Zipkin traces for diagnosing performance issues in large repos.
      (FSAC `Parser.fs:13-16`)
      **S** `-`  **M** `++`  **R** `★★☆`

- [ ] **Shared check-result cache for FSAC sidecar** · Expose warm check results
      via a file-based cache that a co-located FSAC instance could read on startup.
      Gives editors near-instant first-check by reading pre-computed results from
      the running FsHotWatch daemon.
      **S** `--`  **M** `-`  **R** `★★★`

- [ ] **LSP-compatible diagnostics output** · Emit diagnostics in LSP format over
      a named pipe so editors can subscribe to real-time diagnostics without running
      their own FCS instance.
      **S** `--`  **M** `-`  **R** `★★★`

- [ ] **Per-file dependent-check cancellation** · When a file changes mid-scan,
      cancel in-flight checks for files that depend on it (not just the file itself).
      FSAC uses linked `CancellationTokenSource` chains for this.
      (FSAC `AdaptiveServerState.fs:2551-2561`)
      **S** `-`  **M** `-`  **R** `★★☆`

- [ ] **NuGet vs project-reference distinction in `ProjectGraph`** · NuGet-only
      reference changes don't require transitive source re-checking. Tracking the
      distinction would let the scan loop skip large subtrees.
      **S** `-`  **M** `0`  **R** `★★☆`
