# ADR-006: Suspend/resume rejected for daemon memory relief

Status: Accepted (2026-06-07)

## Context

After the memory campaign settled on three quit/trim-based mechanisms —
`ConserveMemory=9` (ADR-003), idle-exit (ADR-004), and pressure-shortened
idle-exit (ADR-005) — one alternative kept being raised and deserves a recorded
answer: **why not *suspend* an idle/under-pressure daemon instead of quitting
it?** i.e. `SIGSTOP` the process (or rely on macOS App Nap / the freezer) so it
stops consuming CPU and the OS can compress or page out its pages, then
`SIGCONT` to resume it instantly — keeping the process, JIT, and FCS caches warm
so the next request needs no rescan.

This was reasoned about but never benchmarked (unlike the levers in ADR-003/004,
which were). This ADR records why it was not pursued, so the question doesn't get
re-opened without new information.

## Decision

Do not implement suspend/resume. It is dominated by the quit mechanism we
already ship; its one theoretical advantage (instant warm resume) collapses
under exactly the conditions that would trigger it.

## Why

1. **Suspend doesn't free memory by itself.** A `SIGSTOP`ped process keeps all
   its resident pages. Relief comes only if the OS later *compresses* or pages
   them out — and only under pressure. macOS' compressor gets roughly 2:1 on
   dirty pages, so a ~3 GB daemon parks at best around ~1.5 GB compressed. That
   loses to idle-exit's **0**, loses to the (rejected) in-place trim's ~400 MB
   (ADR-005), and isn't even guaranteed (the OS compresses on its own schedule).
   On the memory axis — the whole point — suspend is the weakest option.

2. **A suspended daemon is functionally down anyway.** While stopped it cannot
   service its IPC pipe or process file-watch events. So it has quit's downside
   (unresponsive until something wakes it) *without* quit's upside (memory
   actually freed). Quit strictly dominates: same unresponsiveness, footprint
   goes to 0, and the file-backed check cache survives on disk for the restart.

3. **The "instant resume, no rescan" edge evaporates on resume.** Its only real
   advantage over quit is skipping the cold FCS rebuild. But:
   - Under memory pressure (when you'd suspend) the pages have been compressed or
     paged out, so resume pays decompression / page-in — not instant.
   - While suspended, the daemon misses file-watch events. On resume it cannot
     trust its warm state; it must detect what changed and re-check it — the same
     rescan cost quit pays. (And "what changed while I was stopped" is itself new
     machinery to build and get right.)
   So the no-rescan benefit holds only when *nothing changed and memory stayed
   resident* — i.e. precisely when you didn't need to suspend in the first place.

4. **Added orchestration with no home.** Something must detect a stopped daemon
   and `SIGCONT` it before each IPC call. We already have a proven, simpler path
   for "daemon not available": the CLI auto-starts a dead daemon
   (`decideDaemonAction → StartFresh`). Suspend would bolt a second, more
   fragile liveness protocol onto that.

5. **Portability.** `SIGSTOP`/`SIGCONT` is Unix-only; Windows process suspension
   needs `NtSuspendProcess` (undocumented) or per-thread suspension. A
   cross-platform maintenance burden for a niche that's already dominated.

The general principle from ADR-005 holds here too: **if you're going to stop
doing work to save memory, just quit** — the file-backed cache makes restart
cheap, and quit is the only option that actually reaches 0.

## The niche suspend seemed to fill is already covered

The one scenario suspend targets — "shed memory but stay instant-on" — is better
served by the **idle-trim** mechanism (release FCS root caches in place, keep the
process), which we measured at ~390 MB post-GC with transparent re-warm. We chose
not to ship trim either (ADR-005: quit dominates it for our workload), but the
mechanism is preserved at the `exp/idle-trim` bookmark precisely for a future
"low-memory-but-instant-on" need (e.g. an editor-integrated host). Suspend offers
nothing idle-trim doesn't, at higher cost and worse memory.

## Related road also not taken: one shared daemon for N workspaces

Briefly recorded for completeness (also un-benchmarked): instead of one daemon
per workspace, run a single daemon serving all workspaces so the BCL/package
metadata loads once instead of N times. Rejected as not worth the architectural
cost now — idle-exit already removes the multiplier for *idle* workspace daemons
(they quit), which was the bulk of the pain. If a future workload has many
*simultaneously-active* large-solution daemons, this is the lever to revisit; the
mmap-metadata-snapshot experiment (`exp/mmap-metadata-snapshots`, ADR-004) is the
natural companion, since shared file-backed metadata pages are where a shared
daemon's win would come from.

## Consequences

- No suspend/resume code; no second liveness protocol; the CLI's auto-start path
  remains the single "daemon not available" recovery.
- If suspend is ever reconsidered, the bar is new evidence that contradicts §3 —
  specifically, a measured case where resume is genuinely instant (memory stayed
  resident AND no files changed) often enough to beat quit's cheap cache-warm
  restart. Absent that, this stays closed.
