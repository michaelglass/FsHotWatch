# FSAC / Compiler Learnings for TestPrune

Cross-reference with FsHotWatch's `docs/fsac-learnings.md`. Items already
shipped (projectCacheSize, snapshot API, topo sort, caching, etc.) and
items explicitly ruled out (`enablePartialTypeChecking`, parse-only,
intra-project parallelism) are not repeated here.

Primary use case: TestPrune.Core as a library consumed by a long-running
daemon holding a hot FCS instance. Priorities reflect daemon resilience,
cancellation safety, and incremental re-analysis over CLI ergonomics.

Scores: **S** Simpler · **M** Maintainable · **R** Result quality (`★★★/★★☆/★☆☆`)

---

## Tier 1 — Quick wins

- [x] **Script checking semaphore** · Add `SemaphoreSlim(1,1)` to serialize
      concurrent `.fsx` checks. FSAC discovered FCS has internal state corruption
      under concurrent script processing. TestPrune already calls
      `GetProjectOptionsFromScript` for scripts — worth protecting.
      (`AstAnalyzer.fs:734`, FSAC `CompilerServiceInterface.fs:113-115`)
      **S** `+`  **M** `+`  **R** `★★★`

- [x] **Process timeout on the pre-index build step** · The build that runs before
      indexing (`dotnet build`) has no timeout. A hung build blocks the CLI
      indefinitely. (`Orchestration.fs`, build invocation)
      **S** `0`  **M** `+`  **R** `★★★`

- [x] **Process execution logging** · Log command, args, working directory, and
      duration for build/test invocations. Build failures are opaque to diagnose
      in the current output.
      **S** `0`  **M** `++`  **R** `★★☆`

- [x] **Resolve relative paths to absolute before FCS** · FSAC does this
      defensively because script compilation fails with relative paths. Apply the
      same pass over `SourceFiles` and `OtherOptions` before passing to
      `ParseAndCheckFileInProject`. (FSAC `CompilerServiceInterface.fs:207-215`)
      **S** `0`  **M** `+`  **R** `★★☆`

- [x] ~~**`CancellationTokenSource.TryCancel()` / `TryDispose()`**~~ · Skipped —
      FsHotWatch (primary consumer) already has a local `cancelAndDispose` helper
      in CheckPipeline.fs. No value duplicating it in Core when the consumer owns
      CTS lifecycle.

- [x] ~~**MSBuild lock: bounded retry, not infinite**~~ · Dropped — the infinite
      retry is inside Ionide.ProjInfo's `WorkspaceLoader`, not our code. Our
      `ProjectLoader.fs` uses a simple `lock msbuildLock` monitor. Can't fix
      upstream behavior from here.

---

## Tier 2 — Focused sessions

- [x] **`Async.parallel75` for project tiers** · Already implemented.
      `Orchestration.fs:369` uses `Async.Parallel(tasks, maxDegreeOfParallelism = parallelism)`
      with a `--parallelism` CLI flag (default: `ProcessorCount`).

- [x] ~~**Track `packages.lock.json` and `obj/*.props` for project invalidation**~~ ·
      Dropped — file-change invalidation belongs in the daemon (FsHotWatch), not
      in Core. Tracking `obj/` causes infinite loops (build writes trigger
      reindexing). `.fsproj` changes are already detected by existing invalidation.

- [x] **Separate stdout and stderr from build/test processes** · `TestResult`
      now has `Stdout` and `Stderr` fields. Callers print stdout to stdout and
      stderr to stderr. Build runner already handled them separately.
      **S** `0`  **M** `+`  **R** `★★☆`

---

## Tier 3 — Experiments

- [ ] **Indexing benchmarks** · Add a benchmark measuring cold and warm index time,
      symbol count, and cache hit rate on a representative multi-project solution.
      Required before the TransparentCompiler and WorkspaceLoaderViaProjectGraph
      experiments produce meaningful numbers. **Do this first.**
      **S** `0`  **M** `++`  **R** `★★☆`

- [ ] **(experiment)** **`useTransparentCompiler = true`** · FCS's newer incremental
      compiler; FSAC tests both modes in CI. Requires `FSharpProjectSnapshot` API
      (TestPrune already has `createProjectSnapshot` in AstAnalyzer.fs, so the
      infrastructure is partially there). Designed for exactly the hot-FCS daemon
      use case — incremental recompilation with a warm compiler. Measure indexing
      time vs BackgroundCompiler.
      (`AstAnalyzer.fs`, FSAC `CompilerServiceInterface.fs:91-109`)
      **S** `-`  **M** `0`  **R** `★★★` *(if experiment succeeds)*

- [ ] **(experiment)** **`keepAllBackgroundSymbolUses = true`** · FSAC enables this
      for find-all-references. TestPrune uses `GetAllUsesOfAllSymbolsInFile` which
      operates on per-file check results, so this may not affect correctness. With
      a hot FCS, background symbol uses accumulate across checks — enabling this
      could improve graph completeness for incremental re-analysis.
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

---

## Deprioritized / Not applicable

- **FSharp.Core / FSI path fixup in project options** — FSAC explicitly replaces
  `FSharp.Core.dll` and `FSharp.Compiler.Interactive.Settings.dll` paths in project
  options with SDK-discovered paths. Edge case for non-standard SDK configurations.
  Low priority; revisit if users report issues.
  (FSAC `CompilerServiceInterface.fs:143-182`)

- **`WorkspaceLoaderViaProjectGraph`** — Uses MSBuild's static graph evaluation.
  Faster for multi-project solutions. However, the bottleneck in TestPrune's MSBuild
  path is inside Ionide.ProjInfo, not in our code. Revisit if Ionide adds support
  or if we replace the project loader.
  (FSAC `Parser.fs:126`)

- **`keepAssemblyContents` conditional on hasAnalyzers** — TestPrune always needs
  assembly contents for `GetAllUsesOfAllSymbolsInFile`. Cannot be made conditional.
- **`enablePartialTypeChecking`** — explicitly ruled out (mutually exclusive with
  `keepAssemblyContents`).
- **File-level dependency tracking** — TestPrune already implements this via
  `firstChangedIndex` in Orchestration.fs.
- **Script project options via `GetProjectOptionsFromScript`** — already done.
- **Sliding cache expiration / WeakReference in cache** — memory pressure is managed
  by the daemon process, not TestPrune.Core. Not our concern.
- **`suggestNamesForErrors`** — TestPrune doesn't surface FCS diagnostics to users.
