# FSAC / Compiler Learnings for TestPrune

Cross-reference with FsHotWatch's `docs/fsac-learnings.md`. Items already
shipped (projectCacheSize, snapshot API, topo sort, caching, etc.) and
items explicitly ruled out (`enablePartialTypeChecking`, parse-only,
intra-project parallelism) are not repeated here.

---

## Done

- **Script checking semaphore** — `SemaphoreSlim(1,1)` serializes concurrent `.fsx` checks
- **Process timeout on build step** — 10-minute timeout on `dotnet build`
- **Process execution logging** — command, args, working dir, duration
- **Resolve relative paths to absolute before FCS**
- **`Async.Parallel` for project tiers** — with `--parallelism` flag
- **Separate stdout/stderr from build/test processes**
- **Indexing benchmarks** — `benchmarks/TestPrune.Benchmarks/`, `mise run bench`
- **`useTransparentCompiler = true`** — ~18% faster cold index on FSAC, 3 more
  symbols, 3 fewer dropped edges vs BackgroundCompiler

## No effect

- **`keepAllBackgroundSymbolUses`** — no change in symbols, deps, or dropped edges.
  TestPrune uses `GetAllUsesOfAllSymbolsInFile` (per-file), not background accumulation.
- **`captureIdentifiersWhenParsing`** — no change. TestPrune doesn't read identifiers
  from parse results.

## Not applicable

- **`CancellationTokenSource.TryCancel()`** — consumer (FsHotWatch) owns CTS lifecycle
- **MSBuild bounded retry** — retry is inside Ionide.ProjInfo, not our code
- **`packages.lock.json` / `obj/*.props` tracking** — belongs in daemon, not Core
- **`keepAssemblyContents` conditional** — always needed for `GetAllUsesOfAllSymbolsInFile`
- **`enablePartialTypeChecking`** — mutually exclusive with `keepAssemblyContents`
- **FSharp.Core / FSI path fixup** — low priority, revisit if users report issues
- **`WorkspaceLoaderViaProjectGraph`** — bottleneck is in Ionide.ProjInfo
- **Sliding cache / WeakReference** — memory managed by daemon process
- **`suggestNamesForErrors`** — TestPrune doesn't surface FCS diagnostics
- **Dual-mode CI** — TransparentCompiler is strictly better, no need to test both
