# ADR-052: Builds hold a process slot

Status: Accepted (2026-10-28). AUTOMATION-682 slice 6. Builds on ADR-050 and ADR-051.

## Context

ADR-050 put test hosts under one process bound per repository host: `PluginCtx.Processes`,
a lane of the host's process scheduler, admitted through `Lane.Run`. The Build plugin's
`dotnet build` still spawned outside it. With two sessions gating, both sessions' builds
ran on top of however many test hosts the bound had already admitted, so the bound held
for test hosts but not for the box.

## Criteria (fixed before code)

- **(a) Builds hold slots.** Two sessions' Build plugin runs each hold a process slot
  while building, and are queued when none is free. Tested deterministically: the build
  is a gated child that waits on a release file, and the test waits on scheduler state,
  never on a sleep.
- **(b) Builds and test hosts share the bound.** A build and a test host, in different
  sessions, never run beyond the bound together: with one slot, each waits for the other.
- **(c) Cancellation leaks no slot.** A build cancelled while queued never starts and
  leaves no waiter; a build cancelled, failed or crashed while running gives its slot back.
- **(d) Per-worktree daemons are unchanged.** A daemon with no repository host
  (`PluginCtx.Processes = None`) builds exactly as before: the build's work is run as
  given, with no admission step.
- **(e) Real workload.** The two-session harness of ADR-049/051 is rebuilt and kept in
  the repository (`bench/two-session/`), not in `/tmp`. Interactive and bulk waits are
  recorded, and the process lane's build and test-host waits beside them. No interactive
  wait may exceed 0.6 s, twice slice 5's 0.3 s.
- **(f) The usual bar.** RED first, then a full `mise run ci`; merged with GitHub CI
  green, reverted if it goes red; no coverage floor lowered.

## Decision

- **A build holds one process slot for as long as it runs.** The Build plugin runs its
  build subtask through `BuildPlugin.admitted ctx.Processes`, which is
  `Lane.Run(JobSpec.interactive, work)`. The grant, the release in a `finally` and the
  give-up while queued are `Lane.Run`'s (ADR-050), so a build gets the same guarantees as
  a test group.
- **A template build holds one slot for all its roots.** Its roots run one after
  another, so it is one heavy child at a time. A slot per root would add hand-overs
  between roots and buy no extra bound, the same reasoning as a test group in ADR-050.
- **Lock order is the shared lease, then the slot.** A build claims the
  `build-artifacts` lease first and then waits for a slot, the same order TestPrune
  takes for test groups. Nothing that holds a process slot waits for that lease, so the
  two cannot wait on each other.
- **Builds are interactive.** A build is set off by an edit, and holds a slot for one
  child. A later slice may mark a scan-triggered build bulk, once something measures
  that it matters.
- **A per-worktree daemon has no lane.** `admitted None work` is `work` itself, so a
  standalone daemon's builds run exactly as they did.
- **Status names both.** `status --repository` prints `processes (test hosts, builds): R
  of N slot(s) held, Q queued`.
- **Queueing does not spend the build's timeout.** The timeout is armed at the spawn
  (`ProcessBounds.armedWhen`), which happens after the slot is granted. While it waits
  for a slot, the build plugin reports `Running`, as it does while it waits for the lease.

## Measured

Unit tests (`BuildProcessAdmissionTests`, no sleeps: gated shell builds and scheduler counts):

- (a) Two sessions' builds on one slot: one runs and one is queued; their start and end
  lines never interleave, and each lane is granted once.
- (b) A test group (`GroupSchedule.runAdmitted`) waits for another session's build, and a
  build waits for another session's test group.
- (c) A build cancelled while queued never starts and leaves no waiter; one cancelled while
  running, one that throws, a failed build (`false`) and a crashed one (missing command)
  each give the slot back. A template build of two roots takes one grant.
- (d) `admitted None work` is reference-equal to `work`.
- RED: 9 of the 38 tests in the two affected classes failed against a stub that ignored
  the lane (8 new tests and the relabelled status line).

Real workload: `bench/two-session/run.sh`, rebuilt from ADR-049's description. 240
chained files, check cache off, a 12-slot host, `b` saving every 0.5 s for 30 s while `r`
edits the chain root and forces a rescan every 5 s. The Build plugin is enabled. Each
build ran twice, alternating, on the same box. "base" is main at 44c20768 (slice 5 plus
AUTOMATION-1353, which touches only test hosts; the harness runs none).

| run | session | interactive max wait | interactive grants | bulk max wait | bulk grants | build grants | build max wait |
|---|---|---|---|---|---|---|---|
| base-1 | b | 65 ms | 58 | 920 ms | 240 | 0 | n/a |
| base-1 | r | 0 ms | 6 | 517 ms | 1663 | 0 | n/a |
| base-2 | b | 62 ms | 57 | 450 ms | 240 | 0 | n/a |
| base-2 | r | 0 ms | 6 | 980 ms | 1645 | 0 | n/a |
| slice 6-1 | b | 63 ms | 56 | 491 ms | 240 | 15 | 0 ms |
| slice 6-1 | r | 8 ms | 6 | 969 ms | 1602 | 13 | 0 ms |
| slice 6-2 | b | 47 ms | 56 | 928 ms | 240 | 17 | 0 ms |
| slice 6-2 | r | 0 ms | 6 | 382 ms | 1582 | 13 | 0 ms |

- The longest interactive wait was 65 ms, against a bar of 0.6 s. Slice 6 is not worse
  than its baseline on this harness.
- On base, the process lane granted nothing: builds ran outside the bound. On slice 6,
  every build was granted a slot (13 to 17 per session).
- No build waited for a slot. The harness has no test project, and two sessions' builds
  never come near 12 slots, so this run proves the wiring, not contention. Contention is
  what the unit tests in (a)–(c) prove.
- This harness is a reconstruction. Its interactive waits (about 65 ms) are lower than
  ADR-051's (298 ms), so the two sets of numbers are not comparable; compare runs of the
  same harness only.
