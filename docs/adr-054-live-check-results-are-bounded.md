# ADR-054: Live check results are bounded, and their producers wait

Status: Accepted (2026-10-28). AUTOMATION-683 slice 1. Builds on ADR-038 (bounded checks),
ADR-050 (lanes outlive their callers safely) and ADR-051.

## Context

AUTOMATION-683's acceptance criteria include: "File result production is backpressured so
queued plugin consumers stay within a configured live-result bound."

A check's `FileCheckResult` holds FCS's parse and check results for the file: the typed
tree, its symbol uses and its diagnostics. It stays reachable until every plugin that
subscribes to `FileChecked` has committed it. ADR-038 bounded how many checks run at once.
Nothing bounded what happens after a check returns. The scan and the change batch dispatch
each result to every subscriber's mailbox and move on. When the consumers (lint, analyzers,
TestPrune) are slower than the checks, which is the normal shape of a cold scan, their
mailboxes hold an unbounded number of results. At the end of a scan of `N` files, up to `N`
results can be live at once, however small the check bound is.

## Sweep (main@origin 1e5f7132, 2026-10-28)

Here is what 683 asks for, and what is already on main:

- **Two divergent worktrees check the same relative file in isolation.** Delivered by the
  repository host's sessions and per-session daemons (ADR-045 to ADR-047), and
  `RepositoryHostParityTests`.
- **A rediscovery in A invalidates only A's projects.** Delivered:
  `Daemon.dropForRediscovery` advances generations for the projects it is handed. Nothing
  calls `InvalidateAll` or clears the language service's caches.
- **Incompatible toolchains get separate checker state.** Delivered by refusal: an attach
  with a different toolchain is refused (`AttachRefusal.ToolchainMismatch`), so it runs its
  own daemon. Checkers are partitioned by cache configuration (`CheckerPartitions`).
- **Tiers and cold-scan retries respect the scheduler bounds.** Delivered by AUTOMATION-937
  and AUTOMATION-682: `parallelWithin` plus the session's lane, and scan checks run as bulk
  work. **Not delivered:** tiers as explicit job dependencies. Each tier is still a barrier,
  in both the scan and the change batch.
- **Backpressure on live results. Not delivered.** That is this slice.
- **The change batch holds every result of a cohort** until all of the cohort's tiers have
  finished, and only then publishes them. That retention is separate from the mailboxes. It
  is the next slice.

## Criteria (fixed before code)

- **(a) The bound holds.** With a subscriber that does not commit, a daemon's host never
  has more than `bound` `FileChecked` results that were dispatched and are not yet committed
  by every subscriber. A producer that finds the bound reached waits. When one result is
  committed, exactly one waiting producer is admitted. This is tested with a blocked plugin,
  with no sleeps.
- **(b) Every path releases its place.** A place is released when:
  - every subscriber commits the result;
  - a subscriber fails it;
  - no plugin subscribes (released at once);
  - the producer decides not to publish (an ADR-046 refusal);
  - the producer's publication throws.

  A producer that gives up while waiting (its token is cancelled) never takes a place.
  Each of these has a test.
- **(c) The daemon's producers wait, and wait outside the publication lock.** A scan over
  more files than the bound, with a subscriber that blocks, has exactly `bound` results
  dispatched until the subscriber is released. Then it completes with every file checked.
  The change batch publishes through the same bound.
- **(d) It is configured and observable.**
  - `DaemonOptions.LiveResultBound`; the default is twice the processor count.
  - `FSHW_LIVE_RESULTS` in the daemon's environment overrides it; a value that is not a
    positive integer is ignored, with a warning.
  - `status --repository` carries a per-session `liveResults` object: bound, live, peak,
    admitted, waited, and the longest wait.
- **(e) Real workload.** On the two-session harness with lint on, measured on the slice and
  on its base:
  - the peak of live results stays at or below the bound;
  - every file is checked, with the same check grants for the same work (no memory win
    from doing less work);
  - the interactive longest wait stays under the slice-6 bar of 0.6 s.
- **(f) The usual bar.**
  - RED first.
  - A full `mise run ci` exits 0, and no coverage floor is lowered.
  - Merged with GitHub CI green, and reverted if it goes red.

## Decision

- **A place per live result, taken before publication.** `LiveResults.Bound` has
  `capacity` places. They are the slots of a one-lane `WorkScheduler.Scheduler`, so a wait
  is FIFO and measured, and a give-up leaves the queue. `Bound.Hold(publish)` waits for a
  place and then runs `publish` with it. The grant is recorded under the scheduler's lock,
  as `Lane.Run` does (ADR-050), so a cancellation between the grant and `publish` cannot
  strand the place.
- **The publication holds the place, not the producer.** `PluginHost.EmitFileChecked(result,
  place)` dispatches through `DispatchTracked`, collects every subscriber's receipt, and
  gives the place back when all of them have completed, whether they committed or failed.
  With no subscriber, the place is given back at once. A place that `publish` did not hand
  over (an ADR-046 refusal, or a throw) is given back when `Hold` returns. A place is given
  back once, however many paths reach it.
- **The wait is outside the publication lock.** The scan's `emitChecked` and the change
  batch's `emitResults` call `Hold` before `publishCurrent`. So a waiting producer holds no
  lock that a plugin's commit could need.
- **The scan's checks go at the plugins' pace.** `runChecksWithRetry`'s `emit` is now
  asynchronous. A check whose result is waiting for a place still counts against
  `maxInFlight`, so no further check of the tier starts until a place is given back. The
  check slot itself was given back when the check returned, so a waiting producer never
  holds a check slot that a sibling session needs.
- **One bound per daemon.** A hosted session has its own: its plugins and mailboxes are its
  own. `DaemonOptions.LiveResultBound` defaults to `2 × ProcessorCount`, which lets the
  plugins fall one round of checks behind before a check waits. `FSHW_LIVE_RESULTS`
  overrides it, read once where the CLI builds the daemon's options, like
  `FSHW_CHECK_CACHE`.
- **A stuck plugin stalls its producer; it does not grow memory.** A plugin that never
  commits is already the wedge detector's business (`PluginWedge`). The detector shuts the
  daemon down, which cancels the waiting producer. No separate timeout was added.
- **The old `EmitFileChecked(result)` is unchanged and unbounded.** The daemon no longer
  calls it. It stays for embedders and tests that publish directly.

## Rejected

- **Waiting before the check, not after it.** That would make a place cover the check in
  flight as well as the queued result. But the change batch keeps every result of its
  changed cohort until it has computed the cascade, so a cohort larger than the bound would
  deadlock against itself. Streaming the batch's results comes first; see the next slice.
- **A host-wide bound across sessions.** Memory is the host's. But the mailboxes are each
  session's, and one bound per session is enough to stop the growth this slice targets.
  If measurement shows sessions crowding each other, it becomes a lane per session of one
  host scheduler, as ADR-050 did for processes.
- **A `.fshw.json` key.** The bound is a property of the machine, not of the repository,
  so it is an environment override like `FSHW_CHECK_CACHE` and `FSHW_IDLE_EXIT_MIN`.

## Measured

Unit tests, with every wait on a completion the test controls:

- **RED.** 14 of the 22 `LiveResultsTests` and the daemon test failed against a stub that
  neither waits nor counts. The other 8 pass against the stub as well: the
  release-on-throw, release-with-no-completions, no-subscriber and failing-subscriber cases
  (the stub counts nothing, so `Live` is always 0), the blank override, and the default.
  They pin behaviour the real implementation must keep.
- **GREEN.**
  - 22 of 22 `LiveResultsTests`.
  - The daemon test: three files, bound 1, and a subscriber that holds its first result.
    `Live` is 1 with one producer queued until the subscriber is released. Then the scan
    completes, with 3 admitted and a peak of 1.
  - The repository status test.
  - `LiveResultsTests`, `FileCheckedDeliveryTests` and `PluginHostTests` passed 5 runs out
    of 5 (101 tests each run). The daemon, repository host, check pipeline, hosting,
    cold-scan and locked-emit classes passed 285 of 285.

Two-session harness (`bench/two-session/run.sh`, new `LINT=1`), on main 1e5f7132 (base)
and on this slice, on a 12-processor machine, so the bound is 24:

| run | session | check grants (interactive / bulk) | interactive longest wait | live peak | live admitted |
|---|---|---|---|---|---|
| cold, base | r / b | 0/240, 0/240 | — | not measured | — |
| cold, slice | r / b | 0/240, 0/240 | — | 24 / 24 | 240 / 240 |
| 30 s, base | r / b | 6/1573, 57/240 | 147 / 196 ms | — | — |
| 30 s, slice | r / b | 6/1606, 57/240 | 0 / 88 ms | 24 / 24 | 1598 / 297 |
| 30 s, base #2 | r / b | 6/1632, 57/240 | 0 / 148 ms | — | — |
| 30 s, slice #2 | r / b | 6/1680, 58/240 | 0 / 46 ms | 24 / 24 | 1682 / 298 |

- (e) is met.
  - The peak equals the bound and never exceeds it.
  - Every file of each cold scan is checked and admitted: 240 of 240.
  - The 30 s runs do the same work within run-to-run noise.
  - The interactive longest wait is at most 88 ms, against the bar of 600 ms.
- The cold scan took about the same wall time: 13 s for the whole harness on the slice, and
  14 s on the base.
- The live-result waits sum to about 30 s per session over 240 to 1,600 results. So with
  lint on, the plugins, not the checks, set the pace of a scan. Before this slice, that
  difference sat in their mailboxes.
- (f) `mise run ci` exit 0:
  - test-direct: 6489 passed, 0 failed, 2 skipped;
  - coverage floors passed, with a new floor of 100/100 for `LiveResults.fs`;
  - lint-project: 123/123; sync-docs: 10/10;
  - analyzers: 0 findings; format: rewrote 0 of 429; test-prune: 2/2 unfiltered;
  - integration: 115/115.

  The first run was red on `CoverageFloorCompletenessTests`, because the new file had no
  floor. Its coverage also showed an uncovered compare-and-swap retry in the peak counter.
  That retry was not needed: the peak is written only under the scheduler's lock, so it is
  now a plain write.
