# Repository-host memory, scan and throughput benchmark

`fshw-bench` measures what N concurrent per-worktree daemons on one repository cost, so
that a later repository-host mode can be compared against the same harness. Every
budget for the repository host (for example "four quiescent identical sessions use at
most 1.75× one-session RSS") is stated relative to this harness's numbers. That makes
the method more important than any one number, so this page is mostly method.

## Commands

```bash
dotnet build bench/FsHotWatch.Bench/FsHotWatch.Bench.fsproj
B="dotnet bench/FsHotWatch.Bench/bin/Debug/net10.0/fshw-bench.dll"

# The matrix: 1, 2 and 4 concurrent worktree daemons, 5 repetitions each.
# Run it on a QUIET box. It refuses to start on a contended one (exit 3).
$B run --repo <repo> --rev <rev> --sessions 1,2,4 --reps 5 --tests \
       --prepare "<command that fully builds a fresh worktree>" --out results.jsonl

# One sample of any running daemon (one you did not start included).
$B probe --pid <pid> [--worktree <its root>] --out results.jsonl

# Where is that daemon's diagnostic socket?
$B port --pid <pid>

# The human summary (MB, seconds, ratio of the N-session total to the 1-session total).
$B summarize results.jsonl
```

`mise run bench-memory` runs the matrix against this repository, and
`mise run test-bench` runs the harness's unit tests.

Run the harness through `dotnet <dll>`, and point `--cli` at a `.dll`. That is the
default: the CLI built in this checkout. An apphost under a mise- or nix-installed SDK
cannot find the runtime without `DOTNET_ROOT`.

### Options that change what is measured

| option | effect |
|---|---|
| `--tests` | adds the `after-tests` phase: `fshw check` runs in every worktree at once |
| `--strip <key>` | removes a top-level `.fshw.json` key in the throwaway bench worktrees, e.g. `--strip tests` for a scan-only run. Name it in `--label` |
| `--set <dotted.path>=<json>` | writes a value into the bench worktrees' `.fshw.json` after any `--strip`, creating intermediate objects. It can be repeated; the last write wins. Strings must be quoted JSON (`--set build.args='"build -c Release"'`). A value that is not JSON, or a path through a non-object, is refused before any worktree is created |
| `--mode legacy\|host` | `legacy` (default): one `fshw start` daemon per worktree. `host`: every worktree runs `fshw scan` with `FSHW_REPOSITORY_HOST=1` and a fresh per-repetition `FSHW_STATE_HOME`, so one repository host serves all N sessions. Worktree 1 starts alone, because only the first attach's environment (including the diagnostic port) reaches the host. The rest start once `host.pid` and the port exist. The host is sampled once per phase as **session 0**, and that footprint *is* the N-session total. Sessions 1..N get validity-only records. `summarize` reports host / legacy for every phase and N that both modes measured |
| `--edits K --edit-file <path>` | adds the `edit` phase: K rounds of a marker-line edit to that worktree-relative file, in every session at once. Each record's `phaseMs` is the session's own `[check] settled … after=Nms`, so the summary's phase med/p95 is the settle latency p50/p95. The file is restored afterwards. A round with no settle line within `--settle-timeout-sec` (120 s by default) is invalid |
| `--warm-cache` | keeps caches between repetitions. Without it, every repetition starts `--no-cache` with a fresh `FSHW_CACHE_HOME` and no `.fshw/cache` |
| `--no-heap` | skips the heap walk (footprint only) |
| `--allow-contended` | records on a busy box. Records are marked `contended`, and `summarize` leaves them out unless also given `--allow-contended` |

## One repetition

1. **Worktrees.** N worktrees at the same revision (`jj workspace add` or
   `git worktree add`) are prepared once with `--prepare`, which defaults to
   `dotnet build`. They are built before any daemon starts, so no build can rewrite
   project state under a running scan.
2. **`cold-scan`.** N daemons start together (`<cli> start`, with output appended to
   `logs/daemon.log` the same way the CLI's own launcher does it). Each session is
   sampled when its first `scan-metrics.jsonl` record appears. `physFootprintPeak`
   is the kernel's lifetime high-water mark, so it records the scan's peak even when
   the peak falls between samples.
3. **`settled`.** After `--settle-sec` seconds, each session is sampled again with
   nothing forced.
4. **`post-gc`.** The harness walks each session's heap in turn (see below), which
   induces a full blocking collection, and reads the footprint straight afterwards.
5. **`after-tests`** (only with `--tests`). The harness runs `fshw check` in every
   worktree concurrently, then walks each heap again. It counts the tests from the last
   complete test cycle of each daemon's log.

## Validity: a variant cannot win by doing less work

A record lists what makes it invalid, and `summarize` never scores an invalid record.
The checks are:

- the scan reported `unchecked 0`, no uncovered files and no deps-gated files
  (`scan-metrics.jsonl`);
- the `daemon.log` window announces the pid that was measured;
- the log's `Checked N files` agrees with `scan-metrics.jsonl`;
- every session of a repetition checked the same number of files (parity);
- host mode: every session's log shows `Attached to repository host pid=<the measured host>`,
  and no two sessions share a session id. The host record is invalid if any session is;
- with `--tests`, `fshw check` succeeded and a complete test cycle with a summary exists.
- every `--set` under a section the daemon echoes at startup
  (`[config] checker: cacheSizeFactor=N`) shows up in that echo with exactly the value
  that was set. A run where the daemon ignored the setting can therefore never be scored
  under it. Every record carries `config: { strip, set, echo }`, and `cold-scan`
  records carry the echo read from the log.
- the machine did not sleep or dark-wake since the repetition began. A laptop with its
  lid closed on battery sleeps whatever `caffeinate` says. The .NET `Stopwatch` on macOS
  does not advance during sleep and the wall clock does. On this box they differed by
  20h 38m over 7 days of uptime and 397 sleeps. So a wall-minus-monotonic gap above 2 s
  marks the record invalid. The gap is recorded as `sleepGapMs`, and `pmset -g log`
  names the Sleep/DarkWake events in the window.

## Reading memory on macOS: which counter means what

`ps` refuses the rss and %cpu fields on this box, and `ps -p <pid>` exits 1 for a live
pid. Liveness therefore uses `kill -0`, and memory uses `footprint --json <pid>`.

| quantity | source | meaning |
|---|---|---|
| `physFootprint` | kernel `phys_footprint` (footprint, vmmap "Physical footprint") | What the process costs the box. **Equals the sum of every category's `dirty`**, measured to the byte. It **includes compressed and swapped pages** |
| `swapped` | sum of per-category `swapped` | The part of the footprint that is compressed or swapped out. It is a *subset* of dirty, not an addition to it |
| `resident` | `physFootprint − swapped` | The part in RAM right now. On a box under pressure this drops while the footprint stays the same |
| `physFootprintPeak` | kernel `phys_footprint_peak` | Lifetime high-water mark |
| `clean` pages | mapped files, `__TEXT` | Not in the footprint. The kernel can drop and re-read them |

The harness records the footprint and the swapped part separately. Budgets are stated on
`physFootprint`: it is the number that predicts a gate dying, and it does not change
depending on how much of the process the OS has compressed. `resident` is recorded so
that a reader can see when a box was swapping.

### Managed vs native: the `split`

Every `post-gc` or `after-tests` record carries a partition of the footprint. The parts
add up to `physFootprint` exactly:

| part | how it is obtained |
|---|---|
| `managedLive` | the heap walk: the summed size of every object alive after the induced collection |
| `gcHeapNotLive` | `GCHeapStats.TotalHeapSize` for that collection, minus live |
| `gcCommittedBeyondHeap` | `GC/CommittedUsage.TotalCommittedInUse` minus the heap size. This covers allocation budgets and free regions not yet decommitted |
| **managed** | the sum of the three parts above, i.e. everything the GC has committed |
| `runtimeNative` | footprint `Untagged` dirty minus the GC's in-use commit: the runtime's loader heaps, JIT code and stubs, plus the *touched* part of the GC's bookkeeping tables |
| `malloc` | footprint `Malloc *` dirty |
| `mappedFile`, `image`, `stackAndOther` | the remaining footprint categories |

The GC's bookkeeping commit (`TotalBookkeepingCommitted`, the card table and mark array)
is recorded as a raw field but kept out of **managed**. It covers the whole reserved
region range and is mostly never touched. On this repository's daemon it was 712 MB
committed, more than `Untagged`'s whole dirty total had room for. Untouched pages are
not dirty and therefore not in the footprint.

The GC heap is anonymous `mmap` memory, and `footprint` reports it as `Untagged` along
with the runtime's own allocations. `Untagged` is therefore "GC-heap-like" but it is not
the GC heap. The GC's committed figure is what separates the two. The same figure read
as the System.Runtime `gc-committed` counter is recorded as an independent check. On the
calibration process the two matched to the byte, and the counter excludes bookkeeping.

**Why the earlier readings disagreed.** ADR-003/004 took the managed heap to be the
forced-GC *live set* (~440 MB) and called the rest of a ~3 GB footprint "native". A
later `footprint`/`vmmap` profile found the footprint to be mostly `Untagged` and
called it managed. Each reading counted GC memory that is committed but not live on a
different side. The `split` reports that memory as its own two parts
(`gcHeapNotLive`, `gcCommittedBeyondHeap`), so this question gets a measured answer
per solution size and is no longer a matter of definition.

## The heap walk, and why it is not `dotnet-gcdump report`

`dotnet-gcdump report`'s per-type byte column is **not a per-type total**. In a
controlled process, 9,397 `byte[]` of 124 B each were reported as 24 B, and the rows sum
to a fraction of the report's own header. A size histogram built on that column would
be wrong.

Instead, the harness opens its own EventPipe session with the GC heap-snapshot keywords
and sums `GCBulkNode` sizes per type. Rundown is enabled so that module names arrive,
because nested F# types reach the walk bare (`TType_app`, `EntityRef`) and only their
module identifies them as FCS types. On the calibration process, 500 objects of a
24-byte marker type came out as exactly 12,000 B.

A walk is trusted only when EventPipe dropped no events (`eventsLost = 0`) and its total
reconciles with the GC's own view of the same collection. `GCHeapStats.TotalHeapSize`
includes the free space between objects, so the walk is compared with
`heap × (1 − gc-fragmentation)`, and the two must agree within 15% of the heap. Any
failure is recorded in `walkProblem`, which makes the record invalid.

**Dropped events are the failure to watch for.** The runtime writes the whole walk
during a stopped-the-world collection, faster than any reader drains the socket, so the
session buffer *inside the daemon* has to hold the whole burst. On this repository's own
daemon (209 files), a 1 GB buffer dropped 20,139 events and a 256 MB buffer dropped
31,881. The incomplete walks reported 770 MB and 211 MB live. With a 4 GB buffer, and
with the harness copying the raw stream to a temp file so that parsing never slows
draining, 0 events were lost. That walk found 1,722 MB live against the GC's own
1,738 MB post-GC heap (0.8% fragmentation). A tool that does not check for dropped
events reports an incomplete walk as a small live set without any warning.

The walk costs the daemon memory while it runs: the buffer is committed on demand in
the daemon. In the run above it raised the daemon's lifetime peak from 2,869 MB to
4,303 MB. It released that memory afterwards (footprint 2,100 MB before the walk,
2,092 MB after). Two consequences:

- every walk record carries `footprintBeforeWalk`;
- `physFootprintPeak` is only meaningful as a *scan* peak in `cold-scan` records, which
  are taken before any walk.

### Why the tools could not attach, and how the harness attaches

A .NET process creates `dotnet-diagnostic-<pid>-…-socket` in `$TMPDIR`, or in `/tmp`
when `TMPDIR` is unset. The devenv shells that launch the downstream daemons under the
nix-store SDK run with **`TMPDIR` unset**, so those daemons' sockets are in `/tmp`. A
diagnostic tool started from an ordinary macOS shell has
`TMPDIR=/var/folders/…/T/`, looks only there, and fails with "Failed to collect
gcdump". Separately, the globally installed `dotnet-gcdump` apphost could not start at
all without `DOTNET_ROOT`.

The harness does not rely on `TMPDIR` matching between the two processes:

- the daemons it starts get `DOTNET_DiagnosticPorts=/tmp/fshw-bench-<run>/d<i>.sock,listen,nosuspend`;
- for any other daemon it asks `lsof -U` which socket that pid holds, and falls back
  to a glob in `$TMPDIR` and then `/tmp`;
- it connects with `DiagnosticsClient.FromDiagnosticPort("<path>,connect")`. By hand,
  the equivalent is
  `DOTNET_ROOT=… dotnet-gcdump collect --diagnostic-port "$(fshw-bench port --pid P),connect"`.

## Shareable fraction

A repository host saves memory only on state that more than one session could hold as
one copy. Each walk record carries two estimates.

**By type (`heap.shareableLow`/`High`).** Types are sorted into four groups:
- **metadata**: FCS AbstractIL (`IL*`), the binary readers, System.Reflection.Metadata;
- **perSession**: syntax trees, name resolution, check results, the daemon's own
  state, the MSBuild/NuGet project model;
- **typedTree**: FCS typed-tree nodes;
- **unattributed**: BCL strings, arrays and containers.

This estimate is a wide range (7%–90% on this repository), because type alone cannot
say whether a typed-tree node came from an imported assembly or from checked source.

**By heap graph (`retention`).** This is the estimate that narrows the range. The walk
also records every reference and GC root. The harness partitions every live object by
reachability from the **import roots**:

| class | meaning |
|---|---|
| `importOnly` | reachable from an import root; not reachable from a GC root without passing an import root or owner |
| `overlap` | import contents that a session's checked state *references* (e.g. an `EntityRef` in a `TcState`) |
| `sessionOnly` | reachable from GC roots only by paths that avoid the imports |
| `unreached` | reachable only through an owner (`TcImports`) or a barrier |

The fraction that counts as shareable is `importOnly + overlap`, because a host holds an
imported entity once however many sessions point at it. Overlap is still reported
separately, so the reader can see it. On this repository nearly all import contents are
also referenced by some session, so `importOnly` is ~0 and overlap carries the import
side.

### How the roots were chosen (evidence from this repository's daemon: 40.5M objects, 85.2M references, all resolved)

`fshw-bench graph <trace> --path-from A --path-to B [--block C,D]` prints the shortest
reference chain between two types. `graph <trace> --top N` prints the types whose
instances retain the most, measured with dominators (Lengauer–Tarjan).

1. Dominance alone does not answer the question. The 16 `TcImports` *dominate* only
   1.5 MB, while import-created `MaybeLazy.Lazy[ModuleOrNamespaceType]` module types
   (94,594 instances) retain 868 MB. Imported entities are also referenced from check
   results, so no import object dominates them.
2. `TcImports` is a hub, not contents. Rooted at `TcImports`, 97% of the heap read as
   import-reachable, including 192 MB of syntax trees and check results. The path is
   `TcImports → TcAssemblyResolutions → TcConfig → TcConfigBuilder → IProjectReference
   closure → TransparentCompiler → CompilerCaches → parsed files`.
3. `ImportedAssembly` is the unit of imported contents. Its only route back to session
   state is the same hub, reached through its lazy optimisation-data closure
   (`optdata → TcImports`). With `TcImports` and `TcConfig` as **barriers** there is no
   path from any `ImportedAssembly` to `TransparentCompiler`, `ParsedImplFileInput`,
   `CheckedImplFile`, `TcIntermediate`, `FSharpCheckFileResults` or
   `CapturedNameResolution`.
4. `TcImports` also **owns** import contents directly, so the flood from the GC roots
   must not pass through it. Without that, 993 MB read as overlap.
5. `TcGlobals` is import state too, built from the framework references. The flood from
   the GC roots reached imported entities through `CompilerCaches → framework-imports
   memo → TcGlobals → EntityRef → Entity`. It is a root and an owner, and it has no path
   to the checker.
6. What remains in overlap is genuine reference. The next shortest route from the checker
   into import contents is `TcInfo → TcState → OpenDeclaration → EntityRef → Entity`.
7. Every import here is a DLL read from disk: 1,003 `ImportedAssembly` and 1,003
   `RawFSharpAssemblyDataBackedByFileOnDisk`. So project-to-project references come in
   from each worktree's own `bin/`: identical bytes at the same revision, different
   paths. They are shareable only for a host that keys imports **by content**. The walk
   carries no string contents, so their share of the import side cannot be separated
   here.
8. `FrameworkImportsCache` reaches no `TcImports` under the TransparentCompiler, so a
   "framework imports only" root set would be empty.

The root set is roots `[ImportedAssembly, TcGlobals]`, barriers `[TcImports, TcConfig]`,
and owners `[TcImports, TcGlobals]`.

### Controls (a reading that fails one is recorded but never scored)

- **Leak control:** certainly-per-session types found on the import side must stay
  under 1% of live. On this repository it first read 15 MB. Those turned out to be
  `Syntax`-namespace types that the typed tree also uses for imported members
  (`SynMemberFlags`, `Ident`, `PrettyNaming` maps). They are now classified as ambiguous,
  and the control reads 2.4 KB.
- **Accounting:** the partition must cover the live total to within 1%. It measured
  exactly 0, and the typed-tree part of the partition equals the histogram's typed-tree
  total to the byte.
- **Graph integrity:** node/edge pairing must match, and at most 0.1% of edges may
  point at no walked object. Measured: mismatch 0, unresolved edges 0.

### Cost

Rebuilding the graph of a 40M-object heap takes about 30 s and **~9.6 GB in the
harness**. `run` therefore keeps each walk's trace and analyses it only after that
repetition's daemons have stopped. `probe` analyses inline. `--no-retention` turns the
analysis off. `--keep-traces <dir>` keeps the traces so a larger heap can be analysed
later (`fshw-bench graph`) on a machine with the memory for it.

## Contention

Each record carries the box's load (1/5/15-minute load average, CPU count, memory free
%, swap used) and a list of reasons it counts as contended. The box is judged once
before any daemon starts: load1 above half the cores, less than 30% memory free, or any
FsHotWatch daemon the harness did not start. At each sample it is judged again, and a
foreign daemon is the only reason that counts at that point. Load and memory are still
recorded, but the harness's own daemons are what drive them.

## Record format

The output is append-only JSON Lines, one object per sample, with schema tag
`fshw-bench/1`. A malformed or foreign-schema line is dropped on read.

```jsonc
{ "schema": "fshw-bench/1", "runId": "…", "recordedAt": "…Z", "label": "per-worktree",
  "sessions": 4, "rep": 2, "session": 3, "phase": "post-gc",
  "worktree": "…", "pid": 123, "alive": true,
  "load": { "load1": 2.1, "load5": …, "load15": …, "cpus": 12, "memBytes": …,
            "memFreePercent": 71, "swapUsedBytes": …, "foreignDaemons": [] },
  "contended": [],
  "footprint": { "physFootprint": …, "physFootprintPeak": …, "swapped": …, "resident": …,
                 "buckets": { "untagged": {"dirty":…,"swapped":…}, "malloc": …, "mappedFile": …,
                              "image": …, "stack": …, "other": … } },
  "gc": { "liveBytes": …, "liveObjects": …, "heapSizeAfterGc": …, "generationBytes": [g0,g1,g2,loh,poh],
          "committedBytes": …, "bookkeepingBytes": …, "counterCommittedBytes": …, "walkProblem": null },
  "split": { "managed": …, "managedLive": …, "gcHeapNotLive": …, "gcCommittedBeyondHeap": …,
             "runtimeNative": …, "malloc": …, "mappedFile": …, "image": …, "stackAndOther": … },
  "heap": { "totalBytes": …, "shareableLow": 0.18, "shareableHigh": 0.99,
            "buckets": { "metadata": …, "perSession": …, "typedTree": …, "unattributed": … },
            "top": [ { "type": "TType_app", "module": "FSharp.Compiler.Service.dll",
                       "bytes": …, "count": …, "share": "typedTree" } ] },
  "scan": { /* the daemon's own ScanMetrics record for this start */ },
  "tests": { "total": …, "failed": …, "succeeded": …, "skipped": … },
  "phaseMs": …, "invalid": [] }
```

## Counts from `daemon.log`

`daemon.log` keeps appending across restarts, and one daemon run holds several scan,
build and test cycles. Before counting anything, the harness narrows the log twice:

1. to the lines after the **last** `[config] … verdictInputs:` line, which is the first
   line every daemon start writes;
2. to one start/end-delimited cycle (a scan runs from `files registered` to
   `Checked N files`, a test run from `executeTests starting` to `Tests complete:`),
   using the last cycle that completed.

Lines relayed from a nested daemon (`[tag] hh:mm:ss   |   [tag] …`) are never treated as
cycle markers.

## What the instrument check found (one contended sample, not a result)

On this repository (209 files, 1 session, tests and build stripped, box load ~13 on 12
cores), the `post-gc` split was: footprint 2,092 MB, of which **managed 1,749 MB (84%)**,
live 1,722 MB, runtime-native 297 MB, malloc 38 MB. That is the opposite of ADR-003's
"~85% native". ADR-003's live sets came from `dotnet-gcdump`, and that tool walks the
heap over the same EventPipe path that dropped most of this walk until the buffer was
raised. An undercounted live set is therefore a plausible explanation for ADR-003's
figure. Only the quiet-box matrix on the ~775- and ~1,900-file solutions can settle it.

By type alone, the shareable range was 7%–88% of that sample. The heap graph narrows it
to **58% of live, and 64% of the typed tree**. The rest (42% of live) is reachable only
from session state. That figure assumes project-reference imports are keyed by content;
see the Shareable fraction section above.

## Not built yet

- The one-file-edit phase and its p95 settle latency.
- Diagnostics-count parity. File-count parity is checked, and it would not catch a run
  that checked every file but reported fewer diagnostics.
- The split of project-reference imports from package and framework imports inside the
  import side. It needs assembly names, and the heap walk does not carry string
  contents.
