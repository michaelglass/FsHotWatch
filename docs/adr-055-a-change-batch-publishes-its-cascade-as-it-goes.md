# ADR-055: A change batch publishes its cascade as each check returns

Status: Accepted (2026-10-28). AUTOMATION-683 slice 2. Builds on ADR-054.

## Context

ADR-054 bounded the results a daemon's plugins hold at once. It named a second
retention: the change batch.

The batch checks its cascade tier by tier: the later files of a changed file's project,
then every dependent project. It kept every result until the last tier had finished, and
only then published them. A signature change near the root of a large repository
re-checks most of it. The batch then held one FCS result per cascade file, however
tight the live-result bound, because those results were not yet published.

The changed files themselves must still be held. The batch compares their declarations
after their checks, to decide the cascade, and it announces the files it will check
(`FileChanged`) before it publishes any result. The changed files are the edit, so
they are few. The cascade is the large part.

## Criteria (fixed before code)

- **(a) The cascade is published as it goes.** The test uses a bound of 1, and a plugin
  that holds every result until released. A signature edit to `Early` cascades to
  `Middle` and `Late` in Lib's tier, and to `Use` in App's tier. While the plugin holds,
  the batch starts exactly 3 checks: `Early`, then Lib's tier. It waits for a place, and
  never starts `Use`. A batch that kept its cascade starts all 4 before it publishes
  anything.
- **(b) Nothing is lost.** Once the plugin lets the results go, the batch seals. It has
  delivered all 4 files, after 4 checks in total, and the live peak stayed at 1.
- **(c) No regression.**
  - The existing change-batch tests pass unchanged (`FileCheckedDeliveryTests`).
  - `mise run ci` exits 0, and no coverage floor is lowered.
  - It is merged with GitHub CI green, and reverted if that goes red.

## Decision

- `checkCohort` takes an optional `publish`.
  - The changed cohort passes `None` and keeps its results, as before.
  - The cascade passes `emitResults` for one result. Each tier job checks its file, then
    publishes the result before it finishes.
  - A job waiting for a place among the live results still counts against the tier's
    `parallelWithin` bound. The next tier starts only when the current tier's jobs
    finish. So a held result holds back the rest of the cascade.
- Cascade results are now published concurrently. Every write `emitResults` makes is
  inside `publishCurrent`, as the scan's already are.
- The tier barrier stays. Tiers as explicit job dependencies are a later slice.

## Measured

- RED: (a) failed against the old batch. It started 4 checks, not 3.
- GREEN: `FileCheckedDeliveryTests` passed 10 of 10, in each of 3 runs.
- Wider: the `DaemonTests`, `FileCheckedDeliveryTests`, `LiveResultsTests`,
  `CheckPipelineConcurrencyTests` and `LockedEmitTests` classes passed 247 of 249. The two
  that failed are `DaemonTests` cases, and both failed with `BadImageFormatException:
  Index not found`, thrown at the first line of the test body. Both passed on a rerun, 2 of
  2. The likely cause is the workspace's fshw daemon: `mise run format` started it, and it
  rebuilt `bin/` while the run was loading assemblies. This is an artefact of the run, not
  of the code.
- Two-session harness (`LINT=1`, 30 s, two runs):
  - The interactive longest wait was 58 / 0 ms and then 222 / 210 ms. The bar is 600 ms.
  - The live-result peak was 24, the bound, in every session.
  - The check grants matched slice 1's, within run-to-run noise.

  The harness's root edits mostly reach `r` through forced scans: only 6 interactive
  grants in each run. So the harness does not stress a cascade, and (a) is the evidence
  for this slice.
- (c) `mise run ci` exit 0 on the merge with main ba3b1ba1:
  - test-direct: 6493 passed, 0 failed, 2 skipped;
  - coverage floors passed;
  - lint-project: 123/123;
  - analyzers: 0 findings; format: rewrote 0 of 430; test-prune: 2/2;
  - integration: 115/115.

  The first ci run reported a partial coverage report, which coverage-check refused as
  undeterminable. The cause: the watching daemon that `mise run format` had started in
  the workspace was rebuilding and testing alongside the run. Stopping that daemon fixed
  it.
