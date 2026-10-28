# ADR-049: Bulk work gives way to interactive work until it ages

Status: Accepted (2026-10-28). AUTOMATION-682 slice 2, AUTOMATION-681 slice 7. Builds on
ADR-038 (checks are bounded).

## Context

`WorkScheduler` bounds checks across a repository host's sessions and serves waiting
sessions in turn. Every waiter ranked the same, so a session's save queued behind its own
scan's tiers, and in its turn competed with the scan tiers of every other session. The
ticket asks for two things that pull against each other. Bulk work in one session must
not starve another session's foreground waiter. A full confirm, which is a scan, must
still make progress under a save storm.

## Decision

- **Every acquisition carries a `JobSpec`.** For now it holds only a class, `Interactive`
  or `Bulk`. Later slices add weight, exclusivity and dependencies to the same record.
  `Lane.Acquire(ct)` stays interactive, so an unmarked caller keeps the old order.
- **The class comes from the caller.** `CheckCaller.withinBulk "scan"` marks a scan's
  checks as bulk. `CheckCaller.within` (change batches, and everything unnamed) marks them
  as interactive. `CheckPipeline` acquires its slot with the ambient caller's job.
- **Urgent first, then turns.** A freed slot goes to the first lane in turn whose next
  waiter is urgent: interactive, or bulk that has waited at least the aging bound. If no
  lane has an urgent waiter, it goes to the first lane in turn. Within a lane, the oldest
  urgent waiter goes first, and otherwise the oldest waiter.
- **The aging bound is 5 s** (`DefaultAgingBound`). This is the longest a scan tier
  waits behind saves before it counts as one of them. After that, the lanes alternate
  again, so a storm costs a scan at most the bound plus one turn per slot.
- **Counted per class.** `LaneStatus.Interactive` and `LaneStatus.Bulk` (granted,
  abandoned, total and longest wait) reach `status --repository -a` as
  `admission.interactive` and `admission.bulk`.

## Rejected

- **Strict priority with no aging.** A continuous run of saves would starve a scan, and
  so `confirm`, for as long as the saves continue.
- **Priority within a lane only.** A session's save would still wait its turn behind
  every other session's scan tier, which is the cross-session starvation the ticket names.
- **Measuring the bound before choosing it.** No consumer measurement exists yet. The
  value is a constant with one definition, and `status` now shows the bulk waits it
  produces, so changing it later is a one-line change backed by data.

## Observed (2026-10-28, AUTOMATION-682 slice 3): no bulk wait reached the bound

This is one run on a real two-session repository host, built from the slice 3 tree. The
host had 12 check slots and the check cache was off (`FSHW_CHECK_CACHE=off`), so every
check took a slot. The repository was synthetic: one project of 240 chained files, and
worktree `b` was a jj workspace of `r`.

The run:

- Both sessions cold-scanned.
- For 30 s, `b` saved one file every 0.5 s. Those were interactive change batches.
- Every 5 s, `r` changed the file at the root of the chain and forced a rescan. Those
  were bulk.

`status --repository -a` was read at the end, with 12 slots held and 12 waiting:

| session | class | granted | total wait | mean wait | longest wait |
|---|---|---|---|---|---|
| r | bulk | 1303 | 97.2 s | 75 ms | 1.17 s |
| r | interactive | 6 | 0.02 s | 3 ms | 16 ms |
| b | bulk | 264 | 79.5 s | 301 ms | 1.81 s |
| b | interactive | 425 | 16.2 s | 38 ms | 659 ms |

No bulk wait came near the 5 s bound, so aging never decided a hand-over in this run.
The bound is a backstop against starvation here, not a regular cost. Interactive waits
stayed under 0.7 s, while bulk waits on the same slots ran up to 1.8 s. That gap is the
priority at work.

This is one synthetic run and is not grounds for tuning. A measurement on a real
downstream tree under a real save storm is the input for changing the bound.
