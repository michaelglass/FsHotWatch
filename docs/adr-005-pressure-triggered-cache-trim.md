# ADR-005: Memory-pressure-triggered FCS cache trim

Status: Accepted (2026-06-07)

## Context

ADR-004 shipped idle-exit (quit a quiet workspace daemon outright) and
*rejected* idle-TRIM as a shipping feature — but explicitly bookmarked its
mechanism (`exp/idle-trim`, `ClearLanguageServiceRootCachesAndCollectAndFinalizeAllTransients()`)
as "the right shape if a future use-case needs low-memory-but-instant-on."

That use-case is the one idle-exit cannot cover: **multiple daemons all
actively in use while the machine is under genuine memory pressure** (e.g.
several `.workspaces/` daemons hot at once alongside a Playwright fleet). Idle
daemons quit; busy daemons stay, and the user's original report was exactly
this multi-daemon, machine-starved scenario. Quitting a busy daemon is wrong
(it's doing work); holding ~2.9 GB each is what hurts. Trimming in place —
shedding the FCS root caches and re-warming on the next check — is the fit.

## Decision

Ship `pressureTrimPct` (`.fshw.json`): a 30 s timer reads
`GC.GetGCMemoryInfo()` and, when `MemoryLoadBytes` reaches the configured
percentage of `HighMemoryLoadThresholdBytes`, calls the idle-trim mechanism to
release the FCS root caches in place. Absent → enabled at `100`% (only acts
under genuine GC-recognised high load); `0`/`false` → disabled; positive `N` →
`N`% of the GC high-load threshold (lower = trims earlier).

Guards, learned from the idle-exit/idle-trim experiments:
- **5-minute re-arm cooldown**, atomic (`Interlocked.CompareExchange` on a
  "last fired" timestamp) — sustained pressure cannot trim-storm.
- **Busy-deferral**, and crucially the busy signal includes **cold-scan-in-progress**,
  not just `AnyPluginBusy()` (see below). Deferral does not consume the cooldown.

This is the GC-knob analogue of ADR-003's `ConserveMemory=9`: a host-level
response to memory pressure, not an FCS-internal change. It composes with the
two prior memory features — `ConserveMemory=9` (always, ADR-003) lowers the
warm floor; pressure-trim (active daemons, here) sheds it under load;
idle-exit (quiet workspace daemons, ADR-004) eliminates it entirely.

## A bug the E2E validation caught (and the deeper-altitude fix)

First validation against the intelligence solution showed the trim firing at
**t+35 s while the cold scan was still running** (footprint only 257 MB, pre-warm),
wiping caches the scan was actively building — which the scan then rebuilt.
Root cause: the busy-guard used `host.AnyPluginBusy()`, but the daemon's cold
scan (`performScan`) drives FCS directly, **not through a plugin**, so it was
invisible to the guard. This is the same class of altitude bug ADR-004's mmap
post-mortem warned about — a guard that covers the obvious path but not the
direct one.

Fix: a `Volatile`-backed `ScanInProgress` flag bracketed around `performScan`
(set on entry, cleared on every exit path incl. cancellation), composed into
the busy signal as `AnyPluginBusy() || isScanning()`. The scan-state held in
the scan `MailboxProcessor` was unusable here — reading it is a `PostAndReply`
that blocks precisely while the scan is busy — so a non-blocking flag was the
correct seam. Idle-exit was left as-is: its `LastActivityAt` is bumped on every
checked file during a scan, so it already cannot fire mid-scan.

## Validation (intelligence, 774-file solution, build+analyzers off for fast settle)

End-to-end with `pressureTrimPct=1` (forces fire on first eligible tick),
sampling footprint continuously, no `check` to re-warm:

- daemon warm and **settled at 3,388 MB for ~2 min** — trims=0 (deferral held
  through the entire scan; no premature mid-scan fire after the bug fix)
- trim fires: `memory load 15073MB >= 294MB — released FCS root caches`
- footprint **3,388 MB → 857 MB instantly**, stable for 60 s (no re-warm)
- forced-GC live-set **396 MB** — a **2,992 MB release (−88%)**, matching the
  ADR-004 idle-trim figure (390 MB) to the megabyte
- exactly **one** trim (cooldown held); diagnostics parity preserved across the
  re-warm (an earlier run confirmed fcs 2→2 after a post-trim `check`)

Unit coverage: 32 PressureTrim tests — pct semantics (80/100/120 vs injected
load/threshold), cooldown boundaries, busy-defer-without-consuming-cooldown,
the scan-aware composed predicate, and concurrency (10 k-thread latch + 5 k-thread
runTick hammers proving exactly-one-fire).

## Consequences

- Default-on at 100% is safe: it acts only when the GC itself reports the system
  high-loaded, costs one ~µs timer read per 30 s otherwise, and a trimmed daemon
  re-warms transparently on the next check.
- The trade is a slower first check after a pressure event (cache rebuild) — the
  same accepted trade as idle behaviour, and strictly better than the OS paging
  or OOM-killing daemons under the pressure this responds to.
- `exp/idle-trim` (`2edb8a48`) remains the documented origin of the mechanism;
  this ADR is its productionisation under a pressure trigger rather than an idle
  one.
