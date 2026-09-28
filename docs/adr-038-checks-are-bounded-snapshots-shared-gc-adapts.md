# ADR-038: checks are bounded, project snapshots are shared, the GC adapts to live data

Status: Accepted (2026-09-28)

## Context

A long-lived daemon serving a large downstream solution reached a 33 GB footprint,
32 GB of it the managed heap. Its heartbeats showed a steady 1.5–2 GB/min climb with
gen0 +~2000, gen1 +~450 and gen2 +0 per 30 s, and 55–73% of wall time paused for GC,
while one `WaitForScan` ran for 36 minutes. A thread dump showed 313 threads, 237 of
them inside `CheckPipeline.CheckFileCore` building a snapshot, 222 in
`ProjectSnapshots.snapshotOf` (top frame `String.concat`). A forced full GC left 17k
live objects and returned ~30 GB. It was garbage, not a leak.

Two defects combined:

1. **No bound on concurrent checks.** A scan checks each dependency tier, and a change
   batch its files, with an unbounded `Async.Parallel`. The snapshot build at the start
   of each check is synchronous. Thousands of checks at once blocked every pool
   thread, and the pool grew by starvation injection, one thread at a time, for as
   long as the scan ran.
2. **Every check rebuilt every snapshot.** `buildFramed` built the snapshot of the
   file's project and of every project upstream of it, per file checked: a closure
   string over each project's sources, options and references (`String.concat`, then
   SHA-256), and a new `FSharpProjectSnapshot` for each. For a tier of N files over a
   tree of P projects that is N×P builds, each allocating in proportion to the
   project's size. With hundreds running at once, their garbage was live for as long as
   the build ran, so it was promoted before it died.

## Decision

1. `CheckPipeline` runs at most `maxConcurrentChecks` checks at once (default: the
   processor count). A check holds its slot while building its snapshot and while FCS
   checks it. Joining another check's answer, or giving way to a newer one, does not
   hold a slot. The wait is a `SemaphoreSlim.WaitAsync`, so it holds no thread, and a
   caller that gives up leaves the queue.
2. `ProjectSnapshots.SnapshotMemo` shares project snapshots between checks, keyed by
   the options object, so a new project model starts fresh. An entry is reused only
   while its inputs are unchanged, and every check re-reads them: source versions,
   on-disk reference stamps, upstream builds (by identity), frame and generation. The
   checked file's snapshot serves the text read for it. When that text's version is
   not the disk's (the file moved while it was read), the tree is built for that check
   alone.
3. The CLI's runtimeconfig turns on Server GC with DATAS
   (`System.GC.DynamicAdaptationMode=1`), keeping `ConserveMemory=9` (ADR-003).
4. The watchdog's heap valve is a backstop, not a mechanism. When the heap exceeds a
   quarter of the memory the GC may use and no gen2 has run for two minutes, it forces
   one compacting gen2, logs `HEAP VALVE FIRED`, and `fshw status` reports it. It fires
   at most once per two minutes. It firing means 1–3 regressed.

## Measurements

Cold scan (`--no-cache`, build and tests off, tree prebuilt) of the downstream
solution: 24 projects, 2,177 files, 7 tiers. 12-core Apple Silicon, 36 GB. The box was
shared, and the load average of each run is given. These are single runs, and peak
footprint (`footprint`'s `phys_footprint_peak`, whole GB) varies by a few GB between
repeats.

| build | GC | scan | gc-pause (avg / max) | max heap | peak footprint | max threads | load |
|---|---|---|---|---|---|---|---|
| before | workstation | 13 min | 69% / 92% | 10.0 GB | 16 GB | 192 | 21 |
| before | workstation | 48 min | 59% / 100% | 15.9 GB | 17 GB | 202 | 65 |
| before | server + DATAS | 3 min | 20% / 32% | 9.2 GB | 20 GB | 158 | 19 |
| after, bound 12 | workstation | 25 min | 64% / 75% | 16.2 GB | 17 GB | 209 | 8 |
| **after, bound 12** | **server + DATAS** | **7 min** | **28% / 50%** | **6.7 GB** | **13 GB** | **146** | **19** |
| **after, bound 12** | **server + DATAS** | **6 min** | **24% / 43%** | **7.2 GB** | **10 GB** | **143** | **26** |
| after, bound 48 | server + DATAS | 5 min | 24% / 33% | 7.5 GB | 17 GB | 167 | 16 |

Every "after" scan built 24 project snapshots, one per project, and peaked at exactly
its bound of concurrent checks. Before, each of the 2,177 checks rebuilt its project's
whole upstream tree.

What the table says:

- **The GC policy was most of the pause.** Under workstation GC the scan spends two
  thirds of its time paused, fixed or not. The remaining churn is FCS's own
  type-checking (`CombineModuleOrNamespaceTypes`, per file), which no daemon-side
  change removes. DATAS sizes the heap count and the gen0 budget to live data. It cut
  the pause to a quarter and the scan by 2–4×.
- **The bound is what keeps the peak down under DATAS.** Unbounded, the same scan
  peaked at 20 GB. Bounded at the core count it peaked at 10–13 GB. A looser bound
  (4× cores) gave most of the peak back (17 GB) for a minute of scan time. The cost of
  the bound is wall time: 3 min unbounded against 6–7 bounded, still half the
  shipped workstation baseline.
- The thread count left is not the storm. At the core-count bound, 48 of ~130 threads
  in a dump were idle MSBuild in-process nodes from project evaluation. The rest were
  FCS type-check workers and the daemon's own.

## Rejected

- **Leaving workstation GC and relying on the bound alone.** It is measured above: no
  better than before on pause or peak.
- **`GCHeapHardLimit` / `GCHeapHardLimitPercent` as a ceiling.** ADR-003 measured a
  death spiral near a hard limit (scan transients need far more headroom than the live
  set). The heap valve gives a ceiling's diagnostic value without its failure mode.
- **Memoizing snapshots per (project, model generation) alone.** Sources change
  between model generations, so an entry must be validated against content, not just
  a generation number. It is keyed by the options object (which is per model) and
  checked against its inputs.
- **Reusing the memoized snapshot for the checked file too.** That would serve the file
  from disk rather than the text the check read. Its version is the same, but a file
  that moves between the read and FCS's request would fail the check instead of being
  checked as read. The top project's snapshot is re-created around the read text; its
  upstreams, and its closure, are shared.
