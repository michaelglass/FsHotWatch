# FSAC / Compiler Learnings for TestPrune

Cross-reference with FsHotWatch's `docs/fsac-learnings.md`. Items already
addressed in `TODO.md` (projectCacheSize, snapshot API, topo sort, caching,
etc.) and items explicitly ruled out (`enablePartialTypeChecking`,
parse-only, intra-project parallelism) are not repeated here.

Scores: **S** Simpler · **M** Maintainable · **R** Result quality (`★★★/★★☆/★☆☆`)

---

## Tier 1 — Quick wins

- [ ] **Script checking semaphore** · Add `SemaphoreSlim(1,1)` to serialize
      concurrent `.fsx` checks. FSAC discovered FCS has internal state corruption
      under concurrent script processing. TestPrune already calls
      `GetProjectOptionsFromScript` for scripts — worth protecting.
      (`AstAnalyzer.fs:734`, FSAC `CompilerServiceInterface.fs:113-115`)
      **S** `+`  **M** `+`  **R** `★★★`

- [ ] **Process timeout on the pre-index build step** · The build that runs before
      indexing (`dotnet build`) has no timeout. A hung build blocks the CLI
      indefinitely. (`Orchestration.fs`, build invocation)
      **S** `0`  **M** `+`  **R** `★★★`

- [ ] **MSBuild lock: bounded retry, not infinite** · `ProjectLoader.fs` retries
      MSBuild acquisition in an infinite `while true` loop with 50ms sleeps. Should
      have a timeout/retry limit and return an error. (`ProjectLoader.fs:17,55`)
      **S** `0`  **M** `++`  **R** `★★☆`

- [ ] **`CancellationTokenSource.TryCancel()` / `TryDispose()`** · Adopt FSAC's
      extension methods that swallow `ObjectDisposedException` and
      `NullReferenceException` on cancellation. Defensive against races.
      (FSAC `AdaptiveExtensions.fs:19-32`)
      **S** `+`  **M** `++`  **R** `★☆☆`

- [ ] **Process execution logging** · Log command, args, working directory, and
      duration for build/test invocations. Build failures are opaque to diagnose
      in the current output.
      **S** `0`  **M** `++`  **R** `★★☆`

- [ ] **Resolve relative paths to absolute before FCS** · FSAC does this
      defensively because script compilation fails with relative paths. Apply the
      same pass over `SourceFiles` and `OtherOptions` before passing to
      `ParseAndCheckFileInProject`. (FSAC `CompilerServiceInterface.fs:207-215`)
      **S** `0`  **M** `+`  **R** `★★☆`

---

## Tier 2 — Focused sessions

- [ ] **`Async.parallel75` for project tiers** · The topo-sort produces dependency
      tiers. Projects at the same tier are currently processed sequentially.
      `Async.Parallel` with a `ProcessorCount * 0.75` cap would parallelize
      FCS analysis across independent projects. SQLite writes would still serialize
      (WAL allows one writer), but FCS work would overlap.
      Note from TODO.md: `getProjectOptions` serializes via MSBuild lock, but FCS
      analysis after options load can overlap.
      (FSAC `Utils.fs:243-247`)
      **S** `0`  **M** `+`  **R** `★★★`

- [ ] **Track `packages.lock.json` and `obj/*.props` for project invalidation** ·
      The project-key hash currently covers source file paths/sizes/mtimes.
      NuGet restore changes and SDK props changes (which alter `OtherOptions`) are
      not detected, causing stale project options until the next full reindex.
      (FSAC `AdaptiveServerState.fs:1063-1086`)
      **S** `-`  **M** `0`  **R** `★★☆`

- [ ] **Separate stdout and stderr from build/test processes** · Currently merged.
      Separating them lets the caller distinguish structured output (test results,
      error codes) from log noise.
      **S** `0`  **M** `+`  **R** `★★☆`

- [ ] **FSharp.Core / FSI path fixup in project options** · FSAC explicitly
      replaces `FSharp.Core.dll` and `FSharp.Compiler.Interactive.Settings.dll`
      paths in project options with SDK-discovered paths. Avoids version mismatches
      on non-standard SDK configurations.
      (FSAC `CompilerServiceInterface.fs:143-182`)
      **S** `0`  **M** `0`  **R** `★★☆`

---

## Tier 3 — Experiments

- [ ] **(experiment)** **`WorkspaceLoaderViaProjectGraph`** · Uses MSBuild's static
      graph evaluation. Faster for multi-project solutions because it avoids
      sequential design-time builds. TestPrune's sequential MSBuild lock is a
      known bottleneck for large solutions — worth measuring.
      (FSAC `Parser.fs:126`)
      **S** `0`  **M** `0`  **R** `★★☆` *(if experiment succeeds)*

- [ ] **(experiment)** **`useTransparentCompiler = true`** · FCS's newer incremental
      compiler; FSAC tests both modes in CI. Requires `FSharpProjectSnapshot` API
      (TestPrune already has `createProjectSnapshot` in AstAnalyzer.fs, so the
      infrastructure is partially there). Measure indexing time vs BackgroundCompiler.
      (`AstAnalyzer.fs`, FSAC `CompilerServiceInterface.fs:91-109`)
      **S** `-`  **M** `0`  **R** `★★★` *(if experiment succeeds)*

- [ ] **(experiment)** **`keepAllBackgroundSymbolUses = true`** · FSAC enables this
      for find-all-references. TestPrune uses `GetAllUsesOfAllSymbolsInFile` which
      operates on per-file check results, so this may not affect correctness. Worth
      enabling and checking whether symbol resolution completeness improves (more
      complete call graph = fewer false negatives in test selection).
      (`Orchestration.fs:98-104`)
      **S** `0`  **M** `0`  **R** `★★☆` *(if experiment shows improvement)*

- [ ] **(experiment)** **`captureIdentifiersWhenParsing = true`** · Makes identifier
      captures available from parse results without a full check. Could potentially
      speed up definition-name extraction in AstAnalyzer, or allow a fast pre-pass
      to detect whether a file's definitions have changed before committing to a
      full check.
      **S** `0`  **M** `0`  **R** `★☆☆` *(investigate applicability)*

- [ ] **Dual-mode CI: BackgroundCompiler + TransparentCompiler** · Run tests against
      both FCS compiler modes (as FSAC does). Catches regressions introduced by FCS
      upgrades before users hit them.
      **S** `0`  **M** `++`  **R** `★☆☆`

- [ ] **Indexing benchmarks** · Add a benchmark measuring cold and warm index time,
      symbol count, and cache hit rate on a representative multi-project solution.
      Required before the TransparentCompiler and WorkspaceLoaderViaProjectGraph
      experiments produce meaningful numbers.
      **S** `0`  **M** `++`  **R** `★☆☆`

---

## Notes on items from FsHotWatch's list that do NOT apply here

- **`keepAssemblyContents` conditional on hasAnalyzers** — TestPrune always needs
  assembly contents for `GetAllUsesOfAllSymbolsInFile`. Cannot be made conditional.
- **`enablePartialTypeChecking`** — explicitly ruled out in TODO.md (mutually
  exclusive with `keepAssemblyContents`).
- **File-level dependency tracking** — TestPrune already implements this via
  `firstChangedIndex` in Orchestration.fs.
- **Script project options via `GetProjectOptionsFromScript`** — already done.
- **Sliding cache expiration / WeakReference in cache** — TestPrune is a CLI tool,
  not a long-running daemon. Memory pressure between runs is not a concern.
- **`suggestNamesForErrors`** — TestPrune doesn't surface FCS diagnostics to users.
