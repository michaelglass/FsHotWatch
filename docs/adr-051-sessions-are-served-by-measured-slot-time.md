# ADR-051: Sessions are served by measured slot-time

Status: Accepted (2026-10-28). AUTOMATION-682 slice 5. Builds on ADR-049 and ADR-050.

## Context

`WorkScheduler` served waiting sessions in turn, one grant each. Turns are fair by
count, not by cost. A session whose checks take 10 s got the same number of grants as a
session whose checks take 1 s, and so ten times the slot-time. AUTOMATION-1299's declared
costs would fix that for work someone has declared, but they are parked. The scheduler
already measures what each session holds (`LaneStatus.Held`), so the fix needs no
declarations.

## Decision

- **Service is slot-time held.** Each lane's service is the slot-time it has held, plus
  any catch-up (below). `Lane.Service` is the single point where a slot is charged. A
  declared cost per job (1299) would scale the charge there. There is no weight field
  until something declares one.
- **Least service first, with a quantum.** Among the lanes whose next waiter ranks
  highest, a freed slot goes to the first lane in turn whose service is within
  `FairnessQuantum` (100 ms) of the least any of them has had. Sessions of similar cost
  still take turns, because differences below the quantum leave turn order unchanged. A
  session that has pulled ahead waits until the others catch up.
- **Ranks: aged bulk, then interactive, then bulk.** ADR-049 ranked an aged bulk job
  equal to an interactive one. It now ranks above, and aged jobs are served in turn,
  ignoring service. Aging is the starvation bound, and no accounting may delay it.
- **An idle session comes back level, not ahead.** When a lane goes from idle to holding
  or waiting, its service is raised to the least service of the lanes that kept working.
  Without this, a session that was away would return with a backlog and take every slot
  until its service caught up.

## Measured

Unit tests (injected clock):

- **Fairness.** Two sessions with continuous 1 s and 10 s jobs on one slot, over 2000 s,
  hold slot-times within 20% of each other. The 1 s session gets more than five times the
  grants.
- **Idle return.** A session idle while another ran 50 s comes back with no more than two
  grants in a row. Without the catch-up it took its whole backlog at once: the test fails
  against that variant.
- **Aging.** An aged bulk job is served even when its session has held 100 s more.

Real two-session hosted run. This is the ADR-049 harness: 240 chained files, cache off,
one session saving every 0.5 s while the other forces rescans. Each slice is one run, and
the end-of-run status was read with 12 slots held and 12 waiting.

| build | session | interactive max wait | interactive grants | bulk max wait | bulk grants | held |
|---|---|---|---|---|---|---|
| slice 3 (turns) | r | 16 ms | 6 | 1.17 s | 1303 | 163.5 s |
| slice 3 (turns) | b | 659 ms | 425 | 1.81 s | 264 | 133.3 s |
| slice 5 (slot-time) | r | 298 ms | 6 | 1.64 s | 1574 | 195.0 s |
| slice 5 (slot-time) | b | 238 ms | 466 | 5.13 s | 269 | 152.9 s |

- No interactive wait exceeded 0.3 s, against slice 3's 0.66 s. The acceptance bar was
  1.32 s.
- One bulk job in `b` waited 5.13 s. That is the aging bound plus one hand-over, so aging
  decided at least one grant in this run. Under slice 3 it never engaged.

## Rejected

- **Strict least-service, no quantum.** Sub-millisecond differences would decide every
  hand-over, so sessions of equal cost would stop taking turns. Service order would then
  be an accident of timing.
- **A per-job weight field now.** Nothing would set it. The seam is `Lane.Service`, for
  when 1299's declarations exist.
- **Decaying banked credit instead of catching up.** That needs a decay rate. Catching up
  to the active minimum needs no parameter, and is the standard fair-queueing answer.
