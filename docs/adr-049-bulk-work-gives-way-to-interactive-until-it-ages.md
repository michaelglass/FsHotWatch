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
