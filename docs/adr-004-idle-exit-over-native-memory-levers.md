# ADR-004: Idle-exit ships; remaining native-memory levers rejected as neutral

Status: Accepted (2026-06-07)

## Context

ADR-003 established that the daemon's multi-GB footprint is ~85% native FCS
memory (managed live-set ~440 MB regardless of configuration) and shipped
`System.GC.ConserveMemory=9`, landing the steady footprint at ~2.8–3.1 GB
post-GC against a 32-project / ~775-file solution. The target was to get an
idle daemon **below 1 GB** — users run one daemon per jj workspace, so idle
workspace daemons multiply the cost.

A second benchmark campaign (14 valid sessions, two reps per experiment,
interleaved baselines, validity gate `Checked N files … unchecked 0` +
diagnostics-parity gate against baseline; same harness lineage as ADR-003)
tested every remaining lever at once, each built in its own jj workspace.

## Decision

1. **Ship `idleExitMin`** — the daemon quits gracefully after a configurable
   idle period. AUTO semantics: key absent → enabled at 30 min **only when the
   repo root contains a `/.workspaces/` path segment** (non-default jj
   workspaces); the default/main checkout never auto-quits. `0`/`false`
   disables anywhere; explicit `N` enables anywhere. Quitting reclaims 100% of
   the daemon's memory; the CLI already auto-starts a dead daemon on the next
   command (`decideDaemonAction` → `StartFresh`), and the file-backed check
   cache survives restarts, so the return cost is one auto-start with a
   mostly-cache-hit scan (~220 s observed end-to-end on the reference
   solution).
2. **Reject (memory-neutral, measured):** mmap metadata snapshots, ReadyToRun,
   DATAS, `keepAllBackgroundSymbolUses=false`.
3. **Keep unmerged but bookmarked:** the two working experiments worth
   revisiting — `exp/mmap-metadata-snapshots` (commit `5a950e72`) and
   `exp/idle-trim` (commit `2edb8a48`), both pushed to origin. Do not delete
   these refs.

## Results (post-GC footprint via forced `dotnet-gcdump collect`, MB)

| experiment | rep 1 | rep 2 | verdict |
|---|---|---|---|
| baseline (main + ConserveMemory=9) | 2,848 | 3,051 | reference band |
| idle-trim (`exp/idle-trim`) | **390** (1,054 settled idle) | **389** (1,072) | works; superseded by idle-exit |
| scope: exclude `tests/` | 1,759 | 1,792 | real −1.1 GB; loses test-file checking |
| mmap snapshots (fixed) | (rep 1 invalid, see below) | 2,804 | functionally correct, memory-neutral |
| DATAS (`DOTNET_GCDynamicAdaptationMode=1`) | 2,731 | 3,080 | neutral (ConserveMemory already active) |
| ReadyToRun (osx-arm64 composite) | 3,234 | 2,796 | neutral on footprint |
| `keepAllBackgroundSymbolUses=false` | 2,932 | 2,856 | no-op |

All scored sessions passed validity (774/46 files, 0 unchecked) and exact
diagnostics parity with baseline.

## Why idle-EXIT over idle-TRIM

Idle-trim (`checker.ClearLanguageServiceRootCachesAndCollectAndFinalizeAllTransients()`
after idle) was the measurement breakthrough: it proved the ~2.9 GB "native
floor" is **FCS-cache-rooted native memory**, reclaimable when the roots drop
(390 MB post-GC, reproducible to the megabyte across reps, parity-clean,
cheap re-warm). But as a shipping feature, quitting dominates it:

- memory at idle: 0 vs ~1 GB settled
- code surface: reuses the two battle-tested paths (graceful `cts.Cancel()`
  shutdown + cold start) instead of introducing a new mid-life
  "caches-gone-but-alive" state
- the trim experiment exposed exactly the kind of corner case that state
  invites: its re-fire latch was non-atomic and fired 7× in 41 ms under
  concurrent timer ticks. (Idle-exit's latch is `Interlocked.CompareExchange`,
  verified fire-once under a 10,000-thread hammer test.)
- missed file events while dead are harmless by construction — restart always
  cold-scans
- workspace daemons are consumed by agents running the `fshw` CLI, which
  auto-starts; humans saving files in a dead workspace get nothing until a CLI
  call, which is acceptable for non-default workspaces and is why the default
  workspace is exempt

The trim approach remains the right shape if a future use-case needs
"low memory but instant-on" (e.g. an editor-integrated host); hence the
bookmark instead of deletion.

## The mmap experiment: correct, instructive, and useless here

`FSharpChecker.Create`'s `tryGetMetadataSnapshot` lets the host hand FCS
pointers into memory-mapped assembly images (the VS-host pattern), converting
private metadata copies into file-backed shared pages.

- **First attempt failed silently and instructively**: the provider returned
  the whole mmapped PE image. FCS expects the snapshot to be the **CLI
  metadata block** (`BSJB` root) at offset 0 — `openPEMetadataOnly` →
  `openMetadataReader` reads magic `0x5342` at `metadataPhysLoc = 0` and the
  VS provider returns `MetadataReader.MetadataPointer/MetadataLength`. Given
  the image, FCS threw `bad metadata magic number: 23117` (`MZ`) per
  reference and returned `FSharpCheckFileAnswer.Aborted` for essentially every
  file. The daemon's scan still reported "774 checked / 0 failed" (Aborted →
  ParseOnly is counted), produced **zero** diagnostics, and looked like a
  spectacular 756 MB-peak win. Only the diagnostics-parity gate caught it.
  Lesson re-learned from ADR-003: **a memory variant can "win" by silently
  not doing the work; parity gates are not optional.**
- **Fixed** (PEReader.GetMetadata() over the mapped view, pointing into the
  same shared pages; FCS-level repro: identical answers + diagnostics to the
  no-hook checker, 285 hook calls): functionally flawless, scan-time parity
  (133 s vs 137 s baseline), and **2,804 MB post-GC — inside the baseline
  band**. Conclusion: on this workload FCS's resident native memory is
  dominated by typechecker-internal state (TcImports etc.), not by raw
  metadata bytes the snapshot path can share. Implementation preserved at
  `exp/mmap-metadata-snapshots` (`5a950e72`) with unit + integration
  regression tests, should a metadata-heavy workload ever motivate retesting.

## Scope exclusion (config recipe, not a default)

Adding `"tests/"` to `.fshw.json` `exclude` cut the floor to ~1.8 GB
(−1.1 GB: test projects' share of FCS state) at the cost of all FCS checking
of test files — including one of the two reference diagnostics, which lives in
a test file. Wrong as a default for a tool whose job includes checking tests;
documented here as an option for memory-constrained secondary workspaces.

## Also rejected

- **ReadyToRun**: composite R2R images (FSharp.Compiler.Service.dll 19.4 →
  44.5 MB) produced no footprint change; both reps inside the baseline band.
  Startup latency was not the campaign's concern.
- **DATAS**: redundant with `ConserveMemory=9`; both reps inside the band.
- **`keepAllBackgroundSymbolUses=false`**: the last untested retention flag;
  no-op, consistent with ADR-003's finding that managed-side knobs are capped
  by the ~440 MB managed live-set.

## Benchmark provenance

2026-06-06/07, same harness lineage as ADR-003 (build-plugin disabled to
avoid the scan-truncation race fixed on main; `footprint`/`vmmap` +
`dotnet-gcdump`; load recorded at every checkpoint). 14 sessions, all valid.
The intelligence solution had grown to 774 checkable files (gate re-discovered
by the first baseline rather than assumed). One harness artifact (a multiline
zero from `grep -c … || echo 0` corrupting two CSV rows) was fixed mid-campaign
and the affected rows reconstructed from logs.
