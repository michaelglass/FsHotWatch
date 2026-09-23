# Repository host, phase 2: sharing imports across sessions

Status: plan, awaiting go. Nothing here is built.

FCS anchors are `path:line` in dotnet/fsharp at `7ad874a23` (main, 2026-09-10). fshw pins
FSharp.Compiler.Service **43.12.401**, which is older than some of the code cited. Where
that matters, the text says so. Every anchor gets re-read against the exact version
before the spike that depends on it.

## 0. What the measurements already say

- **Measured on one legacy FsHotWatch daemon** (677 harness, d677's records):
  - managed live set after an induced GC: 1,742 MB;
  - reachable from import roots (`ImportedAssembly`, `TcGlobals`): 1,004 MB, which is 58% of the live set;
  - footprint on the same record: 2,244 MB. Of that, managed commit is 1,907 MB, runtime-native about 300 MB and malloc about 40 MB.
  - So imports are about 45% of the footprint.
  - ADR-003's "85% native" does not hold on this daemon. The ADR-003-shape re-measurement is queued.
- **Phase 1, settled totals** (smoke run, contended, 1 repetition, directional):

  | | N=1 | N=2 | Marginal session |
  |---|---|---|---|
  | legacy | 2,525 MB | 5,233 MB | +2,708 MB |
  | host | 2,774 MB | 4,808 MB | +2,034 MB |

- **The imports repeat per project.** That same daemon held 1,003 `ImportedAssembly` objects across 16 `TcImports`, each backed by its own `RawFSharpAssemblyDataBackedByFileOnDisk`, but only 458 `ILModuleReaderCacheKey`s. So FCS shares IL readers by path but builds a typed import per project. The duplication exists inside ONE session, before any second session is added.

## 1. What FCS does today

### Keys

- **A project's identity is absolute paths.**
  - `FSharpProjectIdentifier of projectFileName * outputFileName` (`src/Compiler/Service/FSharpProjectSnapshot.fs:572-573`, built at `:425-428`).
  - Every per-project cache key in `TransparentCompiler` includes it (`FSharpProjectSnapshot.fs:187,201,221,252`).
  - Every version hashes absolute source paths (`:193-196`, `:209-218`) and `-r:` paths with their mtimes (`ProjectConfig.fullHash`, `:404-408`).
  - So 929's content versions make two worktrees' versions equal but leave their KEYS different.
- **Framework imports and `TcGlobals` are keyed by the framework DLL paths, not the project.**
  - `FrameworkImportsCacheKey(frameworkDLLsKey, primaryAssembly, targetFrameworkDirectories, fsharpBinariesDir, importReuseKey)` (`TransparentCompiler.fs:608-627`, key type `IncrementalBuild.fs:490-500`).
  - `TcGlobals` is built with them (`CompilerImports.fs:3014-3035`) and cached per checker (`CompilerCaches`, `TransparentCompiler.fs:339-394`; the instance is at `:440`).
  - Two sessions that share one checker and one SDK therefore share both, with no relocation at all: SDK paths are identical in every worktree.
- **Non-framework references are imported per project.**
  - `BootstrapInfoStatic` (`TransparentCompiler.fs:947`, key `:945` includes the project) → `TcImports.BuildNonFrameworkTcImports` (`:656-662`) → a fresh `TcImports` (`CompilerImports.fs:3051-3067`).
  - That is the 16× duplication above.
- **IL readers are process-global, keyed by path and mtime.**
  - `ILModuleReaderCacheKey(fullPath, lastWriteTime, …)` (`src/Compiler/AbstractIL/ilread.fs:5016`, built `:5107-5111`).
  - The caches are module statics (`:5022`, `:5032`).
  - So phase 1's single process already shares readers for SDK and NuGet paths across sessions.

### The piece upstream added after our pin: shared imports (#20296, `e631ed7f4`, merged 2026-08-28)

- **What it is:** a process-global `ConcurrentDictionary<SharedCcuKey, WeakReference<CcuThunk>>` (`CompilerImports.fs:469`).
  - The key is `AssemblyFileId(resolvedPath + "|" + writeStamp)` (`:2570-2576`).
  - It is used when `shareImportedAssemblies && importsBase.IsSome && reduceMemoryUsage = Yes` and the `importReuseKey`s match (`:2735-2739`). TransparentCompiler sets both flags (`TransparentCompiler.fs:871`, `:935`).
- **Where it stops:**
  - It skips any reference that is a project reference (`r.ProjectReference.IsNone` is required, `:2613`).
  - It skips type-provider assemblies (`:2616`), and multi-module and ambiguous ones.
  - It is on by default: `?shareImportedAssemblies` defaults to `true` (`src/Compiler/Service/service.fs:223`). The doc comment (`service.fsi:35`) says assemblies containing type providers, and assemblies produced by a project in the same solution, are never shared.
- **What it would do for us:**
  - It removes the per-project duplication for every SDK/NuGet reference, inside a session.
  - Because the dictionary is process-global, it does the same across sessions in the host, even with one checker per session.
- **Not released yet:**
  - 43.12.401 lacks it; a string search of the shipped DLL finds none of `shareImportedAssemblies`, `SharedCcuKey` or `AssemblyFileId`, while known names like `BootstrapInfoStatic` are found.
  - The newest preview, 43.13.101-rc1.26425.128, was built 2026-08-25, before the merge.

### What stays per session whatever we do

- **In-repo F# project references.**
  - `FSharpReference` is served from the referenced project's in-memory `GetAssemblyData` (`TransparentCompiler.fs:743-773`). That is its typed result, and it is excluded from shared imports (`CompilerImports.fs:2613`).
  - Sharing it means sharing typed results, which AUTOMATION-927 ruled out.
  - d677's count bound: at most about 13 of each project's roughly 63 imports.
- **Ranges.** File names are interned in a process-global, append-only table (`src/Compiler/Utilities/range.fs:247-259`). Each worktree's absolute paths get their own indices, so diagnostics carry the session's real paths.

## 2. Hypothesis

- **Model.** Host N-session footprint = H₁ + (N−1)·m.
  - Phase 1: H₁ = 2,774 MB and m = 2,034 MB.
  - Phase 2 lowers m by the part of a session's import memory that becomes shared, S.
  - Converting the 1,004 MB managed import set to footprint (commit ÷ live = 1,907 ÷ 1,742 ≈ 1.09) gives about 1,100 MB per session.
- **Stage A alone** (the FCS upgrade, sharing SDK/NuGet imports across sessions):
  - S is at most the non-project-reference share. By count that is at least 80%.
  - Estimate: S ≈ 0.8 × 1,100 ≈ 880 MB, so m ≈ 1,150 MB.
  - **N=2 ≈ 3.9 GB (−18% against phase 1's 4.8 GB).**
  - **N=4 ≈ 6.2 GB (−30% against phase 1's projected ~8.9 GB).**
- **The per-project duplication inside a session also shrinks H₁ itself.** How much can't be estimated until the byte split exists (Spike 0). If it's large, it improves legacy daemons too, and N=1.
- **The ticket's bar looks out of reach with import sharing alone.**
  - "4 quiescent sessions ≤ 1.75× one-session" needs m ≤ 0.25·H₁ ≈ 690 MB. That means sharing about 1.35 GB per session, more than a session's entire import set by these numbers. The estimate above gives about **2.25×**.
  - So I do not expect import sharing alone to meet 1.75×. Getting there would also need a smaller per-session typed state:
    - 928's cache factor, or
    - the bounded FileCheckResult mailboxes in 937;
    - or the bar restated.
  - The numbers are directional: a contended smoke run, one repetition, and host heap walks that dropped events. The quiet matrix decides. I'm raising the conflict now rather than after building.

## 3. Design

### 3.1 Checker partitions

- **The rule:** the host owns checkers; a session no longer creates one.
- **The key:** a `CheckerPartition` is keyed by (SDK version the session resolves, FCS assembly identity, the checker config: cache-size factor, `keepAssemblyContents`, and the `importReuseKey` inputs, i.e. LangVersion and nullness).
  - Sessions with equal keys share one `FSharpChecker`, so they share framework `TcImports` and `TcGlobals` (§1).
  - A mismatch gets its own partition, never a refusal. Phase 1's toolchain refusal still applies to the SDK.
- **The seam:** `Daemon.createWithCore` already takes the checker (`src/FsHotWatch/Daemon.fs:3780-3809`), and the checker is created in one place (`Daemon.fs:3869-3899`).
  - The partition is handed in through `DaemonHosting` (§5).
  - A standalone daemon keeps `createCheckerWithCacheSizes`.
- **Invalidation stays per session.**
  - `ProjectSnapshots.invalidate` clears by the session's own absolute identity (`src/FsHotWatch/ProjectSnapshots.fs`, `invalidate`).
  - Process-wide clears are already refused in host mode (`HostingSeams.ClearsProcessCaches`).
  - The new thing to guard is `checker.ClearCache`/`ClearLanguageServiceRootCaches` on a SHARED checker: the seam refuses both.

### 3.2 Shared imports: the FCS upgrade (Stage A)

- **Take #20296.** The options:
  1. the next FCS preview that contains it (rc2);
  2. a dotnet-tools nightly FCS package, pinned to a build that contains `e631ed7f4`;
  3. the stable release that follows.
- **Recommendation:** start with 2 for the spike, and land on 1 or 3. Stage A needs no fshw design work, only the pin (its FSharp.Core and analyzer-FCS lockstep rules apply).
- **It also helps legacy daemons,** which makes it worth doing even if the host changes nothing else.

### 3.3 In-repo DLLs: one canonical path per content (Stage C)

- **The problem:**
  - A `-r:` to a DLL inside the worktree has a different `resolvedPath` in every worktree. That covers vendored DLLs, C#/VB project outputs, and anything an F# `FSharpReference` does not cover.
  - So neither `SharedCcuKey` nor `ILModuleReaderCacheKey` matches across sessions.
- **Two ways to fix it:**

  | | Content-addressed copy at one canonical path | `IFileSystem` shim |
  |---|---|---|
  | Mechanism | Copy each in-repo reference into the host's store at `<store>/<sha256>/<FileName>`, with its mtime set to a content-derived stamp (`ProjectSnapshots.contentStamp`), and rewrite that `-r:` to the store path in the snapshot | Replace `FileSystemAutoOpens.FileSystem` (`src/Compiler/Utilities/FileSystem.fs:860-862`) |
  | Cross-session key equality | Yes: the path string and the mtime are equal, so both FCS keys match | No: both keys use the path **string** (`ilread.fs:5107`, `CompilerImports.fs:2570`). A shim can change what bytes and mtime a path returns, not the path in the key |
  | Blast radius | Only the rewritten `-r:` entries of one snapshot | Process-global mutable: every read in every session, the SDK and NuGet included |
  | Cost | Copy bytes on change (the store deduplicates identical builds), and store GC | None in bytes; all risk |
  | Diagnostics | A diagnostic that names the reference path shows the store path, so the reporter maps it back | Unchanged |

  **Choice: the canonical copy.** The shim cannot produce the key equality that sharing needs.
- **Rules for the rewrite:**
  - Rewrite only `-r:` entries under the session's root that are NOT matched by a `ReferencedProjects` entry. FCS matches them by exact string (`src/Compiler/Driver/CompilerConfig.fs:1049-1051`), and an F# project reference must keep its path so FCS serves it in memory.
  - A DLL that differs by one byte gets its own store entry, so it is not shared.
  - Store writes are atomic, and a corrupt entry is a miss. That is 938's artifact-store contract (§7).
- **No relocation of project identities.**
  - The ticket suggests feeding FCS logical, repository-relative project identities. I propose NOT doing that.
  - Identity keys every typed-check cache (§1). Logical identities would make typed results shared across sessions, which is exactly what 927 excluded.
  - It would also put logical paths into ranges (the process-global file table), which the reporter would have to rewrite.
  - Imports don't need it: their sharing keys on the reference path, not the project.

### 3.4 What stays session-local, and why

- **Typed check results** (`ParseAndCheckFileInProject`, `TcIntermediate`, `ParseAndCheckProject`, `AssemblyData`): per 927. They stay separate automatically, because keys include the session's absolute identity.
- **In-memory F# project-reference imports:** these are typed results (§1).
- **Diagnostics and the error ledger:** they carry the session's real paths through the range table.
- **Snapshots and the check cache:** built per session from the session's files.

## 4. Correctness risks, and tests written to fail first

| # | Risk | Test | How it fails before the change |
|---|---|---|---|
| T1 | Duplicated import graphs | Two sessions whose projects reference byte-identical assemblies at different worktree paths. Their `CcuThunk`s for the shared assembly are the SAME object (reference equality through `FSharpCheckProjectResults.ProjectContext.GetReferencedAssemblies()`'s underlying CCU, or a retention probe) | On 43.12.401, and without the canonical rewrite, they are different objects |
| T2 | An edit in A leaks into B | Sessions A and B on one partition. Edit a source file in A so it produces an error. B's diagnostics and ledger stay exactly as they were, and B's typed results are for B's paths | Fails if typed caches were ever keyed logically, or if an invalidation on the shared checker hits B |
| T3 | Near-identical references wrongly shared | The same test as T1, with one reference differing by one byte. The CCUs are distinct, and each session sees its own types, e.g. a member present only in B's copy resolves in B and errors in A | Fails if the store keys on name or path rather than content |
| T4 | Hosted checks drift from legacy | Extend `RepositoryHostParityTests`: legacy vs a hosted session on a shared partition, plus a second, idle session on the same partition, so sharing is actually exercised. Same phases, test runs, coverage and stripped JSON as today | New `modeOwned` fields must be declared explicitly, as they are now |
| T5 | Session A's directory inside shared `TcGlobals` | `implicitIncludeDir` is the FIRST project's directory (`CompilerImports.fs:3014-3035`). A test where B uses a relative `#load`/`#r` path must resolve against B's directory | Fails if the shared `TcGlobals` makes B resolve relative to A |
| T6 | A shared checker cleared under a sibling | Source guard plus a test: in host mode, `ClearCache`/`ClearLanguageServiceRootCaches` is unreachable, and invalidating A's project leaves B's cached results warm (B's next check is a cache hit) | — |

Each test is written first and shown failing on the current code, or with the stage reverted, then green.

## 5. Mode guards

- **The seam.** New fields on `DaemonHosting.HostingSeams`, and nothing branches on the mode elsewhere:
  - `Checker: CheckerSource` (own vs `Partition p`);
  - `References: ReferenceRewrite` (identity vs `CanonicalStore store`).
  - `HostingSeamTests`' source guard gains `FSharpChecker.Create`: it may be called only in `Daemon`'s standalone path and the partition module.
- **Parity:** T4 extends the existing test. Every new `modeOwned` field is listed with the reason it is expected to differ.

## 6. Abandon criteria

- **Stage A:** if the FCS build that contains #20296 breaks diagnostic parity (T4) or analyzer FCS compatibility, and neither can be fixed by pinning, stop there and file it upstream.
- **Stage C:** if canonical rewriting changes any diagnostic or test outcome against legacy (T4), or needs FCS source changes to hold key equality, abandon Stage C. We then keep Stage A plus partitions, and fork nothing.
- **Relocation of project identities:** out, per §3.3. If a later measurement shows typed state must be shared to meet the budget, that is a new ticket reopening 927, not a phase 2 extension.
- **Memory:** if Spike 1 shows under 10% saving at N=2 against phase 1 on the quiet matrix, stop and report. That would mean the import set is dominated by per-session project references, not shared assemblies.
- **Latency:** if one-session warm p95 regresses more than 10% (the ticket's bar) because of the shared checker, the partition must be revertible per worktree. The seam makes that a config choice.

## 7. Ordering against 937 and 938

- **937 (the scheduler) is not needed for memory, but it is needed before any N > 1 latency claim.**
  - A shared checker means sessions contend on one TransparentCompiler, so session A's confirm can delay B's edit.
  - Phase 2's measured claims are N=1 latency (the ticket's ≤ 10%) and N=2/N=4 memory. Neither needs 937.
  - Multi-session latency is 937's own "Done when".
- **938 (epochs and the artifact store) overlaps Stage C.**
  - Stage C needs a small immutable content-addressed store: atomic writes, corruption is a miss, retention bounded by bytes. That is 938's artifact store.
  - Proposal: Stage C builds that store to 938's contract as the first slice of 938, so 938 extends it rather than replacing it.
  - Stage A and the partitions don't depend on 938.

## 8. Order of work

| Step | What | Output |
|---|---|---|
| Spike 0 | d677's `dotnet-dump` + ClrMD pass on a legacy daemon: import bytes by load path (SDK/packs, `~/.nuget`, in-repo bin/obj), and the per-assembly copies across `TcImports`. Needs the quiet box | The byte split that turns §2's range into a number |
| Spike 1 | Pin an FCS nightly with #20296 on a branch. Measure legacy N=1 and host N=1/2/4 with the 677 harness. No fshw design changes | Stage A's real saving, within and across sessions, and its diagnostic parity |
| Stage A | Land the FCS pin (preview or release), with T1-for-SDK/NuGet and T4 | — |
| Stage B | Checker partitions and seam fields, T2, T5, T6 | Shared framework imports and `TcGlobals` |
| Spike 2 | Canonical rewrite for one vendored DLL, by hand, then T1 and T3 | Confirms key equality at `CompilerImports.fs:2570` and `ilread.fs:5107` on the pinned FCS |
| Stage C | Store plus rewrite, sharing the store with 938 | In-repo DLLs shared |
| Measure | The 677 matrix, quiet box, N = 1/2/4, legacy vs phase 1 vs phase 2 | Against §2's numbers and the ticket's bar |
