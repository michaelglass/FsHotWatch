# ADR-005: Memory pressure shortens idle-exit (trim-in-place rejected)

Status: Accepted (2026-06-07)

## Context

ADR-004 shipped idle-exit (a quiet `/.workspaces/` daemon quits gracefully
after 30 min idle; the default/main checkout never auto-quits) and shipped
`ConserveMemory=9` (ADR-003) to lower the warm floor. Both target the daemon's
multi-GB footprint, which is ~85% native FCS memory.

The remaining case is **memory pressure with idle-but-not-yet-expired
daemons**: several `/.workspaces/` daemons warm at once on a tight machine, each
holding ~2.8–3.1 GB, none idle for the full 30-min window yet. Idle-exit will
eventually reclaim them, but on its default schedule that is far too slow when
the machine is actually starved.

An earlier iteration **in this same session** addressed this by shipping
`pressureTrimPct`: a 30s timer that, under GC-reported high load, called
`ClearLanguageServiceRootCachesAndCollectAndFinalizeAllTransients()` to release
the FCS root caches **in place**, keeping the process alive and re-warming on
the next check. That feature was reversed before release. This ADR records why,
and the replacement.

## Decision

Make memory pressure an **input to the existing idle-exit quit mechanism**, not
a new in-place action.

- Pressure signal: `GC.GetGCMemoryInfo()` → pressure is true when
  `MemoryLoadBytes >= HighMemoryLoadThresholdBytes` (the GC's own high-load
  mark). No percentage knob — we reuse the GC's threshold directly. Injected as
  a function (`IdleExit.readGcPressure`) so the scheduler is unit-testable
  without a real GC.
- When a daemon is **already idle-exit-eligible** (`resolveThreshold` returns
  `Some N` — a `/.workspaces/` checkout in AUTO, or an explicit `idleExitMin N`)
  AND pressure is currently true, the effective idle window becomes
  `min(N, pressureFloorMin)`. Default `pressureFloorMin = 2`. So a `/.workspaces/`
  daemon that would normally wait 30 min quits after 2 min idle when RAM is tight.
- The **default/main workspace stays EXEMPT** under pressure. If
  `resolveThreshold` returns `None` (no `/.workspaces/` segment, no explicit
  opt-in), pressure does **not** make it eligible. Pressure only *shortens* an
  already-applicable window; it never *creates* one. This is the explicit
  product decision.
- Pressure is **re-evaluated every 30s tick** (a current-state read, not
  latched): if pressure subsides before the daemon has been idle long enough,
  the full window is restored.
- Config: `idleExitMin` is unchanged. The floor is config `pressureIdleFloorMin`
  (mirrors the `idleExitMin` parse shape): absent → 2; `0`/`false` → disable
  pressure-shortening; positive `N` → `N`.

The decision is a pure function:
`effectiveThreshold (baseThreshold: int option) (pressure: bool) (pressureFloor: int option) : int option`
— composed with the existing pure `shouldFire` in `runTick`.

## Why quit dominates trim-in-place

The trim kept ~400 MB of managed/native state plus the **whole process**
resident. Crucially, it still paid the **same dominant cost as a restart on the
next edit**: a cold FCS rebuild. The reason is that the file-backed
`CheckCache` survives **both** a trim and a full quit/restart equally — so after
either, the next `fshw check` re-warms FCS from the same on-disk cache. The trim
bought a marginally-warmer process at the cost of keeping gigabytes (and the
process) resident, while a quit reclaims **100%** of the footprint for the same
return cost. Quitting therefore strictly dominates trimming.

That collapses the design: there is no reason to add a second, weaker memory
action (trim) when the existing strong one (quit) is both cheaper to keep and
better at reclaiming. Folding pressure into idle-exit's threshold resolution —
rather than adding a parallel trim timer with its own cooldown, latch,
busy-guard, and scan-in-progress flag — is also far less machinery.

## Consequences

- One memory-pressure response, not two. The pressure-trim module
  (`PressureTrim.fs`), its tests, its `pressureTrimPct` config + parse + wiring,
  and the `ScanInProgress` cold-scan busy flag it required are all deleted.
  Idle-exit never needed `ScanInProgress` (its `LastActivityAt` is bumped on
  every checked file during a scan, so it already cannot fire mid-scan).
- Default-on at `pressureIdleFloorMin = 2` is safe: it only shortens daemons
  that are *already* eligible to quit, and only while the GC itself reports high
  load. The default/main workspace is untouched.
- The trade is the same accepted idle-exit trade — a slower first check after a
  quit (cache-assisted cold rebuild, ~auto-start + mostly-cache-hit scan) — but
  now triggered faster under pressure, which is strictly better than the OS
  paging or OOM-killing daemons.
- Honest note: an earlier same-session iteration shipped the in-place
  `pressureTrimPct` trim (with its E2E "3,388 MB → 857 MB" validation). It was
  reversed before release once the trim-vs-quit analysis above made clear the
  trim was dominated. The mechanism origin (`exp/idle-trim`) and that the
  file-backed CheckCache survives a restart both remain documented in ADR-004.
