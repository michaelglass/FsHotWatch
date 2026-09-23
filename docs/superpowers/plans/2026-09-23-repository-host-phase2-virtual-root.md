# Repository host, phase 2: one virtual root, per-worktree overlays

Status: plan, awaiting go. Nothing here is built.

This supersedes `2026-09-23-repository-host-phase2.md` as the target. That plan shared
only imports, at an estimated 2.25× at N=4. Its FCS findings still hold and are used
here:
- the shared-import cache of #20296;
- the IL reader and shared-import keys are path strings;
- `FileSystem` is process-global.

FCS anchors are `path:line` in dotnet/fsharp at `7ad874a23`. AsyncMemoize is read at
that commit, since the local checkout carries an extra commit on it. fshw pins 43.12.401,
and every anchor is re-read against the pinned version before the step that relies on it.
AUTOMATION-927's spike (FsHotWatch workspace `d-cow-spike`, `spikes/cow-fcs/`,
`5d040456`/`b5988bc8`) measured most of the mechanics below on 43.12.401, and is cited as
"927".

## The target

- **The target, as the user set it:**
  - two identical worktrees cost ≈ 1× one worktree;
  - a one-file difference costs ≈ 1× plus that difference;
  - four sessions stay ≤ 1.75× one (the ticket's bar).
- **The idea:**
  - Every worktree is checked under ONE canonical virtual root. Identical content then gives byte-identical FCS inputs, `__SOURCE_DIRECTORY__` and `CallerFilePath` values included, and FCS's content-versioned caches share it.
  - A worktree's differences are an overlay, and only the overlay and what depends on it is checked separately.
  - Results are rebased to real paths when they leave the checker.
- **How this differs from 927.** 927 rewrote paths after checking, and found the typed tree cannot be rewritten. Here nothing is rewritten after the fact: checking happens under the virtual paths, and only what we publish is translated.

## 1. Where the virtual root enters

- **The virtual root:** `V = <host state>/virtual/<repository id>`. It is a path that must never exist.
  - If something exists at a project's virtual `-o:` path, FCS stops using the in-memory reference and reads the DLL on disk (§1.4).
  - The host refuses to start if `V` exists, and a test asserts nothing ever creates it.
- **Each project in a session has a frame:** the pair (virtual root, real root) its paths map between (§2).
- **A path under the worktree root R becomes V + (path − R).** Paths outside R are left alone. SDK and NuGet paths are already identical in every worktree.

### 1.1 Project file, directory and identity

- `FSharpProjectSnapshot.Create(projectFileName = virtual, …)` (fshw: `src/FsHotWatch/ProjectSnapshots.fs`, `build`).
- **The identity becomes shared:** `FSharpProjectIdentifier(projectFileName, outputFileName)` (`src/Compiler/Service/FSharpProjectSnapshot.fs:572-573`, built `:440-444`) is then equal across worktrees.
- **The project directory becomes virtual too:** it is `implicitIncludeDir` (`TransparentCompiler.fs:870`; `ProjectDirectory` is `Path.GetDirectoryName projectFileName`, `FSharpProjectSnapshot.fs:463`).
- **FCS reads nothing from disk through it for a normal project, but it uses it in these places:**
  - relative `-r:` resolution (`CompilerConfig.fs:1480-1481`): ProjInfo hands us absolute `-r:`, so this doesn't apply;
  - `--version:@file` (`CompilerConfig.fs:198-210`, with `errorR` if missing) — see §4;
  - `--load`/`--use` (`:1445-1463`, error) — see §4;
  - the type-provider `ResolutionFolder` (`CompilerImports.fs:2073-2079`) — see §4;
  - string-only uses: `compileTimeWorkingDir` in signature data (`CompilerImports.fs:238-257`) and symbol file names (`:2270-2273`).

### 1.2 Sources

- **How sources are read:** a snapshot check reads sources only through `FSharpFileSnapshot.GetSource` (`TransparentCompiler.fs:1176-1200`).
- **What fshw hands FCS:** each file is created with its virtual name, its content-hash version (929), and a getter that reads the session's real file.
- **Why a shared entry is safe:** two sessions with the same version have the same bytes, so FCS may call either session's getter.
- **The one exception:** `#load` reads through `CreateFromFileSystem` (`:1098-1102`), and only in scripts. fshw checks project sources, not scripts.

### 1.3 Output paths

- **The rule:** `-o:` is virtual and never created (927's "logical output paths").
- **Why:** `ComputeAssemblyData` (`TransparentCompiler.fs:1886-1934`) uses the on-disk DLL only if `FileExistsShim fileName` is true and it is at least as new as the sources (`:1902-1915`).
  - A missing `-o:` therefore always builds the reference in memory, and never reads source mtimes.
  - Today's daemon uses the built DLL whenever it is newer than the sources, and that choice is not in the key (927). This removes that nondeterminism.
- **The paths FCS derives from `-o:`** (`--doc`, the XML doc path of references) are string-only (`CompilerOptions.fs:920`; `TransparentCompiler.fs:915-923`).

### 1.4 `-r:` references

- **In-repo F# project references** (`FSharpReference`) are matched to their `-r:` by exact string (`CompilerConfig.fs:1049-1051`) and then never touch the disk (`CompilerImports.fs:698-711`, `2516-2528`). Both the `-r:` and the reference's output name get the virtual path.
- **Other references inside R** (vendored DLLs, non-F# project outputs) are read by path through the IL reader (`ilread.fs:5107-5111`, `5060-5062`). A missing path is an unresolved-reference error (`CompilerConfig.fs:66-70`).
  - They get content-addressed copies at real store paths `<store>/<sha256>/<name>`, with a content-derived mtime (from the previous plan's Stage C, 938's artifact store).
  - So the `-r:` string and stamp are equal across worktrees, the IL-reader key (`ilread.fs:5107`) matches, and #20296's shared-import key (`CompilerImports.fs:2570-2576`) matches once that FCS is adopted.
- **Reference stamps:** the public `ProjectConfig` constructor stats every reference for its mtime (`FSharpProjectSnapshot.fs:449-455`, no error when missing). fshw already supplies `referencesOnDisk` itself with content stamps (929), which removes the stat's effect on the key.

### 1.5 What still reads the disk by a path we control

| Read | Where | With the virtual root |
|---|---|---|
| IL reader, non-project `-r:` | `ilread.fs:5107-5111`, `5170-5174` | store path, which is real and shared |
| `ComputeAssemblyData`, `-o:` exists? | `TransparentCompiler.fs:1902-1905` | virtual, missing, so in-memory (intended) |
| `--version:@file` | `CompilerConfig.fs:198-210` | would miss: `errorR` → §4 |
| `--load`/`--use`, response files | `CompilerConfig.fs:1445-1463`, `CompilerOptions.fs:333-350` | would miss → §4 |
| Type-provider files under `ResolutionFolder` | `CompilerImports.fs:2075` | would miss → §4 |
| Primary assembly probe | `CompilerConfig.fs:1148-1157` | SDK path (real); a miss falls back silently |
| Reference XML docs | `TransparentCompiler.fs:915-923` | real (SDK/NuGet/store); a miss only means no tooltips |

**Answer to question 1:** yes. For a project with none of the §4 constructs, FCS reads nothing from the real worktree path. Sources come through our getter, and every other disk read goes to an SDK, NuGet or store path.

## 2. Overlays

- **The problem.** FCS keeps one strong version per key and weakens the rest (`src/Compiler/Utilities/LruCache.fs:140-158`).
  - Parse results keep exactly one version (`ParseFileKeepWeakly = 0`, `TransparentCompiler.fs:298-300`; eviction `LruCache.fs:63-80`).
  - So two sessions with different content under one identity thrash. 927 measured it: 27 jobs recomputed every phase, permanently.
  - Its fix, also measured, is to give the diverged part its own identity: 0 recomputes after the first re-check.
- **The design, per project p:**
  - **Closure hash H(p, s):** the content hashes of p's files in session s, its options with paths in virtual form, and H of every project p references. This mirrors what FCS's keys depend on: `baseVersion` includes the referenced projects' versions (`FSharpProjectSnapshot.fs:179-182`).
  - **Canonical entry:** the closure hash p is currently checked under at the canonical frame, `V/…`.
  - **A session's frame for p:**
    - canonical, when H(p, s) equals the canonical hash;
    - otherwise scoped, `V/.overlay/<session>/…`: every path of p under a per-session prefix, so p's identity, file names and file indices are its own.
  - **Scoped p references canonical upstream projects** (their `-r:` and output names) wherever those are canonical for s. A mixed snapshot is fine for FCS.
- **Consequences:**
  - **Identical worktrees:** every project is canonical, so one set of cache entries serves all sessions, typed results included.
  - **A one-file difference in p:** p and every project whose closure includes p become scoped for that session, and nothing else does.
    - Downstream projects are keyed on p's `SignatureVersion` (`FSharpProjectSnapshot.fs:549-551`, feeding `BootstrapInfoStatic` `TransparentCompiler.fs:945`, `NoFileVersionsKey` `:1131`, and every `FileKey` `:230`).
    - A `.fs` file without an `.fsi` puts its content version into the signature hash (`FSharpProjectSnapshot.fs:43-58`). So an implementation-only edit still diverges the dependents.
    - A leaf project (test projects, most edits) diverges alone. An edit to a core file with no `.fsi` diverges its downstream closure.
    - A later optimization, not in this plan: keep dependents canonical when p's public signature is provably unchanged.
  - **Inside a diverged project,** per-file keys are prefix-only (`FileKey = UpTo(index).LastFileKey`, `FSharpProjectSnapshot.fs:361`, `:308`, `:236-254`). Successive edits in the same session reuse the files before each edit, as today.
- **The single session stays in place.** When the only session on a canonical entry edits p, the canonical hash follows it and the identity doesn't change. That keeps today's prefix reuse, so N=1 edit latency doesn't change. A session moves to a scoped frame only when another session still holds the old canonical content.
- **Reverting and merging.** A session whose content returns to the canonical hash (an undo, a merge result equal to its parent) moves back to canonical automatically, because the frame is recomputed from H on every snapshot. That is 927's open question, answered by construction. It is also what makes 938's merges cheap.
- **Invalidation must not clear shared identities.**
  - `InvalidateConfiguration(snapshot)` clears by identifier (`TransparentCompiler.fs:2531`), and so does a type-provider invalidation (`:1050`). On a canonical identity that clears every session's entries.
  - Content versions make most invalidation unnecessary, since a stale entry is just a different version.
  - So a hosted session invalidates only its scoped identities. A source guard and a seam enforce it (§5).
- **A counter caveat.** `bootstrapInfo.Id` comes from a process counter (`TransparentCompiler.fs:941`, taken at `:1045`) and goes into every per-file key (`:1371`). If `BootstrapInfoStatic` is evicted, the project's per-file entries are orphaned even for identical content. Sharing keeps the number of distinct projects near one worktree's, so the defaults (100 strong/200 weak at factor 100, `:296-331`) should hold. The measurement checks for evictions.

## 3. Rebasing on publish

- **The rule:** everything that leaves the checker is translated by the frame it was checked under, from virtual (canonical or scoped) back to the session's real root.
- **Diagnostics.**
  - Range file names are mapped by prefix, through the process-global file table (`src/Compiler/Utilities/range.fs:247-259`), so one virtual path has one index.
  - Message text is mapped too. 927 showed FS0044's text can contain the path, and that rebased diagnostics equal a cold check byte for byte, FS0001/FS0025/FS0044 included.
- **Where it happens:** the daemon's check pipeline, once, before the error ledger. `FileChecked` events keep the real `File`; the FCS objects inside them stay virtual.
- **Consumers of the typed tree and check results:**

  | Consumer | What it takes | With virtual paths inside |
  |---|---|---|
  | Error ledger / `GetDiagnostics` | FCS diagnostics | rebased at the boundary |
  | Lint (`src/FsHotWatch.Lint/LintPlugin.fs:113-122`) | `ParseTree`, `TypeCheckResults` from `FileChecked` | warnings carry ranges and are keyed to the event's real `File`. A rule that opens `range.FileName` would miss; parity (T4) catches it |
  | Analyzers (`src/FsHotWatch.Analyzers/AnalyzersPlugin.fs:274-295`, `CliContext` built by reflection) | file name, source text, parse/check results, typed tree | give the context the virtual file name and rebase each message's range on the way out. An analyzer that reads `range.FileName` from disk would miss; parity catches it |
  | TestPrune (`src/FsHotWatch.TestPrune/TestPrunePlugin.fs:475`, `:2057`, `:2112`, `:7540`) | symbols and their declaration files | **needs a change**: it relativizes against the real `repoRoot`, which turns a virtual path into `../…`. Symbol paths from FCS get relativized against the frame's virtual root, so repository-relative paths come out identical in every worktree. File hashing keeps reading real paths |
  | Typed-tree string constants (`CallerFilePath`, `MethodCalls.fs:1537-1538`; `__SOURCE_DIRECTORY__`, `SyntaxTreeOps.fs:1079-1098`; incomplete-match exception file names) | values inside `FSharpExpr` | stay virtual and cannot be rebound (927). No fshw consumer reads string constants for paths. The analyzer corpus in T4 checks no analyzer does |
  | fshw's check-result cache (909) | per-file results | keyed by repository-relative path, so it shares across sessions (938 moves it into the host) |

## 4. Where it breaks

- **The criterion:** does the construct change check RESULTS (diagnostics, what typechecks), or only compiled values?
  - If it changes results, the project is checked at its real root, not shared.
  - Its dependents follow automatically, because their keys include its signature and identity.

| Construct | Effect | Evidence | In intelligence | Handling |
|---|---|---|---|---|
| `__SOURCE_DIRECTORY__`, `__SOURCE_FILE__` | **Changes typing.** The value is a string literal that can flow into format strings, literals and TP arguments. 927: a root containing `%d` gives FS0001, while the shared result is clean | lexer `LexHelpers.fs:433-436`; `SyntaxTreeOps.fs:1079-1098`; not in any key | 4 of 23 projects (`src/Intelligence.Build.Ops`, 3 test projects) | Real root for that project |
| `CallerFilePath` | Typed-tree constant only; no diagnostic prints it | `MethodCalls.fs:1537-1538`, `:1580-1581` | 3 projects, including `src/Intelligence` | Shared (it's a value, not a result) |
| `#line` | Maps range file names through a process-global store keyed by file index, last writer wins (`range.fs:269-278`; lexer `lex.fsl:788-790`) | — | none | Safe by construction: shared only for identical content (identical directives); diverged content has its own virtual path and index. Still flagged, and real root while the store's behaviour is unverified on 43.12.401 |
| Type providers | `ResolutionFolder = implicitIncludeDir`, i.e. virtual (`CompilerImports.fs:2073-2079`); generated providers never use the in-memory reference (`TransparentCompiler.fs:1863-1864`) | — | none | Real root, detected by a TP assembly among the references |
| `--version:@file`, `--load`/`--use`, response files | Resolved against the virtual directory, so an error | §1.5 | none seen | Real root when present |
| Absolute paths outside R (a `Compile Include` from elsewhere) | Left as is. Equal across worktrees only if they point outside every worktree; otherwise the keys differ and nothing is shared | — | to audit | Nothing to do: it can't produce a wrong answer, only no sharing |

- **The safety argument.** A shared result is wrong only when a check input that depends on the real path is missing from FCS's key. The table lists every such input FCS reads, from the anchors above.
- **Detection is static:**
  - a source scan for the `__SOURCE_*__` tokens and `#line`;
  - a reference scan for type-provider assemblies;
  - an options scan for `--version:@`, `--load`, `--use` and `@` response files.
  
  Anything found puts that project on its real root.

## 5. Stage B: what survives

- **Everything written in qrqruqyl survives, and becomes step 1.**
  - Shared checkers (`CheckerPartitions`, tests) are the precondition: sharing needs one checker.
  - The `HostingSeams.Checker` seam and its tests stay.
  - The whole-checker drop guard stays, and now matters more.
  - Isolation T0–T4, where both sessions are on one partition, now test shared typed results, not just a shared checker.
  - Parity beside a sibling becomes the strongest test: the observed session's checks are served from the sibling's cache entries.
- **New seam fields:**
  - `Frame: PathFrame option` — standalone gets none: real paths, as today;
  - `InvalidatesSharedIdentities: bool` — false for hosted.
- **The drop guard is extended:**
  - `InvalidateConfiguration` may be reached only through the frame's "invalidate my scoped identities";
  - `FSharpChecker.Create` stays confined to its constructor.
- **Mode guards:** the existing source guard, plus parity with `modeOwned` extended only if a published field legitimately differs. None is expected, since results are rebased.

## 6. Measurement

All with d677's harness in host mode: sessions attached with `fshw start`, `--set build=false`, quiet box, 5 repetitions, the `physFootprint` of the host process.

| # | Scenario | Measures | Expected | Bar |
|---|---|---|---|---|
| M1 | N = 1, 2, 4 identical worktrees | settled and post-gc footprint ratio to N=1; **FCS jobs computed** by sessions 2..N's first scan (`FSharpChecker.Caches` counters, 927's method) | jobs ≈ 0; N=2 ≤ 1.15×, N=4 ≤ 1.4× (the marginal session is then its non-FCS state, estimated ≤ 300 MB against phase 1's +2,034 MB) | ticket: N=4 ≤ 1.75× |
| M2 | N=2, B differs by one file: (a) a leaf test file, (b) a core file with no `.fsi`, (c) a file with an `.fsi`, implementation only | footprint ratio to M1 N=2; projects diverged; jobs recomputed | (a) ≈ 1.0×+ that project; (b) + its downstream closure; (c) as (a) plus the dependents' prefix | user: one-file difference ≈ 1× |
| M3 | N=1 warm edit loop, legacy vs host with frames | settle p50/p95 | within noise | ticket: p95 ≤ +10% |
| M4 | Correctness corpus: intelligence and the parity fixture, checked at two roots, shared vs cold at the real root | rebased diagnostics equal byte for byte, per file; flagged projects excluded | equal | must be equal |

Spike 0 (the dump + ClrMD split, already scheduled) tells us how much of a session is not FCS state. That is the floor M1's marginal session can't go below.

## 7. Abandon criteria

- **A wrong answer outside §4.** Any file where the shared, rebased diagnostics differ from a cold real-root check (M4), and the cause is not one of §4's detected constructs.
  - Stop sharing typed results and fall back to the import-only plan.
  - A wrong answer we can't detect statically can't be allowed.
- **A consumer that can't be rebased.** An analyzer or lint rule that needs real paths inside the typed tree, fixable only by forking FSharpLint or the analyzers SDK. That rule's plugin then runs on a real-root check, i.e. unshared, for that session. If that covers most projects, stop.
- **Thrash.** If M1/M2 show recomputation of canonical entries in steady state (`BootstrapInfoStatic` evictions, weakened versions), and cache sizing can't stop it within the memory bar.
- **Latency:** M3 p95 regresses more than 10%.
- **Memory:** M1 N=2 above 1.3×. That would mean the sharing isn't materializing, so stop and diagnose before building overlays.

## 8. Order of work

| Step | What | Gate |
|---|---|---|
| 1 | Stage B as written (qrqruqyl): shared checkers, seam, guards | its tests red → green; full gate |
| 2 | Frames for identical trees only: virtual root, canonical frame, rebasing at the boundary, TestPrune relativizing against the frame. A session whose closure differs anywhere stays on its real root (unshared) in this step | M1, M4 on the fixture; T0–T4, parity |
| 3 | §4 detection; flagged projects on their real root | M4 on intelligence at two roots |
| 4 | Overlays: closure hashes, scoped frames, canonical follows a sole holder, return to canonical, invalidation only of scoped identities | M2, M3; thrash counters |
| 5 | In-repo DLL store (938's first slice) | M1 on a repo with vendored DLLs |
| 6 | The quiet matrix | M1–M3 against the bars |

Step 2 alone should deliver the identical-worktree case (≈ 1×) with no overlay machinery, and it's the cheapest way to test the premise.
