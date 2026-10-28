# ADR-057: Two diverging worktrees are checked apart by one checker

Status: Accepted (2026-10-28). AUTOMATION-683 slice 4. Covers 683's first two acceptance
criteria, which the code already met but no test stated.

## Context

AUTOMATION-683 requires two things of a repository host serving several worktrees:

- "Two divergent worktrees can check the same relative file concurrently with correct,
  isolated diagnostics."
- "A project/config/global-to-session change in A invalidates only A's project
  configurations; B retains a demonstrable warm state."

The sweep in ADR-054 found both delivered in code. A session's daemon is its own.
`Daemon.dropForRediscovery` advances generations only for the projects of the session it
runs in. Nothing calls `InvalidateAll` or clears the language service's caches. But
`RepositoryHostIsolationTests` checked only that an edit in A leaves B's observable state
(T1) and its reload (T2) unchanged. Neither test made the two worktrees diverge on the
same file, and neither showed that B's checker state stayed warm.

## Criteria (fixed before the tests)

- **(a) Same file, two contents, one checker, at once.**
  - The setup: A's `src/Lib/Lib.fs` holds a type error, and B's holds an unbound name.
  - Both sessions scan at the same time, through the one shared checker.
  - A's diagnostics report the type error and nothing about the unbound name.
  - B's report the unbound name and nothing about the type error.
- **(b) A's rediscovery is A's alone.**
  - The trigger: A's `Lib.fsproj` changes, and A rescans.
  - A's project moves to a new generation (`ProjectSnapshots.generationOf`). This is the
    positive control.
  - B's project keeps its generation, so every entry the checker holds for B is still
    the one B's next check reads.
  - B's observable state is unchanged (`assertBUntouched`).
- **(c) The usual bar.**
  - These tests describe delivered behaviour, so there is no product RED. Each
    criterion's positive control is shown failing, so the test can fail.
  - `mise run ci` exits 0.
  - It is merged with GitHub CI green, and reverted if that goes red.

## Decision

- **T5 and T6 are added to `RepositoryHostIsolationTests`**, beside T0–T4. They run in
  the same world: two real sessions in one host, sharing one checker partition.
- **The isolation world now records each session's watcher callback**, by root. T6 needs
  it to deliver A's project change as A's watcher would.

  This was learned by measurement. A forced scan over a changed `.fsproj` rediscovers the
  project, but it does not advance the checker's generation for it. The scan's snapshots
  carry the new options in their keys, so it needs no new generation. Only the
  change-batch path calls `dropForRediscovery`. T6's first version used a forced scan,
  and its positive control failed. That failure is the evidence that the control can
  fail.

## Measured

- **T5:** passed. A mutant in which B holds A's content failed, because B then reported
  the type error. So the test detects diagnostics that are not isolated.
- **T6:** passed. A's generation moved and B's did not, and B's observable state was
  unchanged. Its positive control failed under a forced scan, as described above.
- **The `RepositoryHostIsolationTests` class:** 7 of 7, in each of 3 runs.
- **`mise run ci` exit 0:**
  - test-direct: 6499 passed, 0 failed, 2 skipped;
  - coverage floors passed;
  - lint-project: 123/123;
  - analyzers: 0 findings; test-prune: 2/2;
  - integration: 115/115.

  The gate's format step rewrote one file of the new test, and that formatting is
  included in the commit.
