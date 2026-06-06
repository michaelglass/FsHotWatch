# ADR-003: Daemon memory — GC ConserveMemory default; FCS checker knobs rejected

Status: Accepted (2026-06-06)

## Context

Running the daemon against a large multi-project solution (thellma/intelligence:
32 projects, ~745 checkable files, deep dependency tiers) showed a heavy
per-instance memory footprint — ~4 GB observed under machine load, and up to
**14 GB peak** unconstrained on a quiet machine. Users running several daemons
concurrently (one per jj workspace) pay that multiplied.

The obvious suspects were the `FSharpChecker.Create` arguments in `Daemon.fs`:

```fsharp
FSharpChecker.Create(
    projectCacheSize = 200,
    keepAssemblyContents = true,
    keepAllBackgroundResolutions = true,
    parallelReferenceResolution = true,
    useTransparentCompiler = true
)
```

Static analysis suggested big wins: nothing in FsHotWatch (or the TestPrune
plugin) consumes typed implementation trees (`ImplementationFile` /
`AssemblyContents`) or calls `GetBackgroundCheckResultsForFileInProject`, so
both `keep*` retention flags looked like pure waste, and FCS docs describe
`keepAllBackgroundResolutions = false` as "reduces memory usage".

We benchmarked instead of trusting the static analysis. Three rounds, 27
measured daemon sessions, one variant binary per jj workspace, strictly
sequential runs against a dedicated intelligence workspace, diagnostics-parity
gate on every session. That benchmarking is why this ADR's decision is the
opposite of the static analysis's prediction.

## Decision

1. **Ship `System.GC.ConserveMemory=9`** as a `RuntimeHostConfigurationOption`
   in `FsHotWatch.Cli.fsproj` (override per-process with the
   `DOTNET_GCConserveMemory` env var). Benchmarked ~25–40% footprint
   reduction at no measurable cost.
2. **Remove `projectCacheSize = 200`** — dead config; the TransparentCompiler
   path never reads it.
3. **Reject** flipping `keepAssemblyContents` / `keepAllBackgroundResolutions`,
   shrinking `transparentCompilerCacheSizes`, and any `GCHeapHardLimit`.

## Why: the footprint is ~85% native, not managed-cache

The decisive measurement was forced-GC live-set via `dotnet-gcdump` (24 dumps
across all variants):

- **Managed GC heap: ~425–450 MB in every variant** — baseline and all
  five FCS-knob variants within ~6% of each other.
- Post-GC process footprint: ~2.9–3.3 GB in every variant.
- The ~2.5 GB between those numbers is **native** memory (FCS unmanaged
  metadata readers / `ILBinaryReader` buffers, mmapped assemblies, JIT code),
  invisible to any `FSharpChecker.Create` argument.
- Everything *above* ~3 GB (up to 14 GB peak) is **collectable managed churn**
  the default GC policy has no pressure to collect on a big-RAM machine.

So the FCS knobs move managed caches that are a rounding error of the
footprint, while the multi-GB overage is a GC-policy problem — which is exactly
what `ConserveMemory=9` addresses.

## What worked

| Approach | Result | Shipped? |
|---|---|---|
| `DOTNET_GCConserveMemory=9` (unmodified binary) | Settled ~3.0 GB vs 3.9–4.4 GB; peak 5.0 vs 5.9–7.8 GB; scan time within baseline noise; full diagnostic parity, valid 745-file scans | **Yes** (as runtimeconfig default) |
| Removing `projectCacheSize` | No behavior change (TransparentCompiler ignores it); removes misleading config | **Yes** |
| `transparentCompilerCacheSizes = CacheSizes.Create 40` | Parity-safe, modest peak trim (~5.4–5.7 GB) | No — redundant: combined cache-40 + ConserveMemory=9 measured no better than ConserveMemory=9 alone |

## What didn't work

| Approach | Result |
|---|---|
| `keepAssemblyContents = false` | No measurable footprint or live-set change (live set 2,961–3,073 MB vs baseline 2,883–3,162 MB — within noise). The typed trees are not the resident mass. |
| `keepAllBackgroundResolutions = false` | No gain; one rep measured *higher* (7.4 GB peak in round 1, 3,357 MB live in round 2.5 rep 1 — churn noise, but never a win). Under the TransparentCompiler this flag selects a different retention branch (`tcState.TcEnvFromImpls`), not a smaller one. |
| `CacheSizes.Create 20` | **Harmful.** Cache thrash made per-file checks slow enough to lose the scan-vs-batch cancellation race; the cold scan truncated to 103/742 files and the daemon under-reported diagnostics while exiting 0 (see ADR scope note below — this exposed a pre-existing bug, fixed separately). |
| `DOTNET_GCHeapHardLimit = 2 GiB` | **Death spiral.** Scan transients need far more headroom than 4× the post-GC live set; near the limit the GC thrashes and the scan never completes (every phase timed out, checks exited 1). Do not ship any heap hard limit. |

## Measurement lessons (why early rounds were misleading)

- **`fshw check --agent` wall time is not a latency metric** — once the daemon
  settles it answers from cached state in ~0.25 s. Time the daemon's own log
  markers (`[scan] … Checked N files`, `[fcs] checked` bursts) instead.
- **Footprint on a quiet machine doesn't discriminate variants.** With no
  memory pressure the GC never compacts, so every variant converges to its
  high-water churn (round-2 baselines: 7.8 / 10.9 / 14.1 GB for the *same
  binary*). Use forced-GC footprint (`dotnet-gcdump collect`) as the live-set
  metric, and treat peaks as load-dependent.
- **Machine load swamps everything.** Load average swung 33→273 during round 1;
  the identical baseline binary measured 6.1 GB vs 4.2 GB peak in two runs.
  Interleave baseline sessions between variant sessions and record load at
  every checkpoint.
- **A validity gate is mandatory.** Sessions must prove the scan completed
  (`Checked 745 …, skipped 46` matching registration) and diagnostics-parity
  against baseline, or a variant can "win" by silently doing less work —
  which is precisely how `CacheSizes.Create 20` first looked like the best
  variant.
- **Wrong-binary trap:** `dotnet <dll> start` doesn't write
  `.fshw/config.hash`; the first `check` then triggers a config-hash-mismatch
  restart via `dotnet tool run fshw` — the *installed* tool, not the binary
  under test. Benchmark harnesses must seed `config.hash` after start and
  verify the measured PID's cmdline.
- **macOS:** `footprint <pid>` / `vmmap --summary <pid>` work sandboxed
  (`ps -o rss` does not); `footprint` switches units from "NNNN MB" to
  "NN GB" at ≥10 GB — parse both.

## Side findings (fixed or tracked separately)

- **Silent scan truncation race** (pre-existing, exposed by the cache-20
  variant, then observed on unmodified baseline in 5/9 sessions one evening):
  build output touching `obj/**/ref/*.dll` fires the watcher batch, whose
  `CancelPreviousCheck` cancels in-flight cold-scan checks; cancelled checks
  surfaced as `None` and were silently dropped, leaving a green-looking but
  incomplete ErrorLedger. Fixed on main alongside this ADR: bounded retry of
  cancelled scan checks + `ScanComplete` carries an `unchecked` count rendered
  as `incomplete: N checked, M unchecked` (non-ok) in status.
- **Non-deterministic FCS internal-error diagnostics** under cancellation
  churn: `error internal error: Object reference not set …` (FCS NRE) — 5,038
  diagnostics across 189 files in one session; sibling rep clean; also seen on
  unmodified baseline. Upstream FCS fragility under aggressive cancellation;
  not addressed here.
- **Follow-up:** `check`'s exit code does not yet reflect the `unchecked`
  count (needs an IPC `DiagnosticsResponse` extension); status and the scan
  log line do.

## Benchmark provenance

2026-06-05/06, Apple Silicon (12 cores), dotnet 10.0.203, FCS 43.12.204,
FsHotWatch 0.8.0-alpha.20 baseline. Round 1: 6 sessions under heavy co-tenant
load (directional only). Round 2: 9 sessions, interleaved baselines, validity
gate (most invalidated by the truncation race — data that motivated the fix).
Round 2.5: 12 sessions, build plugin disabled (race-free), gcdump live-sets,
12/12 valid, 11/12 exact diagnostic parity (1 FCS-NRE outlier). Round 3: GC
knobs (conserve9 valid/parity-clean; hardlimit2g rejected; cache40+conserve9
no better than conserve9).
