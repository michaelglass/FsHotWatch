# Coverage repair after the VerifiedNothing outcome change

Full unit runs during AUTOMATION-564 validation exposed two distinct problems:
branch-count floors still described the compiler output before AUTOMATION-339,
and several tests depended on a timer or thread race to execute a branch.
A passing test count alone did not satisfy the coverage gate.

## Removed instrumented branches

Adding `VerifiedNothing` to `RunOutcome` changed F# union-match emission. The
following branch sites disappeared from the reports; retaining their old count
floors requires branches that are no longer emitted.

| File | Old covered/total | Current covered/total | Removed site |
| --- | --- | --- | --- |
| Ipc.fs | 41/52 | 38/48 | outcomePayload: 3/4 |
| PluginActivity.fs | 40/56 | 36/52 | runRecordBytes: 4/4 |
| Verdict.fs | 449/540 | 443/534 | timedOutLastRun: 2/2; idle outcome match: 4/4 |

The Verdict comparison groups generated classes after normalizing embedded
source line numbers. Retained branch-coverage sequences agree. The saved
baseline XML identifies source paths, but does not prove an exact source commit;
these are report comparisons, not an inferred revision from a workspace name.

Evidence digests (SHA-256):

- Baseline Verdict 449/540 report:
  `4efe5a9895c070b25cc1ca72d8f117188a021e705a35c9338e25a13397e43a86`
- Full 3,116-test candidate report:
  `eaec61aaf44a73b7dd7a86ec03ed5788b0e01ddee88db6835f305a4fa5adbf0e`

`coverageratchet baseline-lines` generated the replacement counts from the full
run. Only the three macOS covered-branch values and their reasons are retained
from that output. Covered-line floors, percentage floors, and Linux entries stay
unchanged. A blanket loosen was rejected because it would also accept actual
coverage instability.

## Deterministic coverage

- Discovery explicitly throws from an injected workspace loader. The test checks
  the completed failure snapshot and verdict refusal before ordinary waiting.
- SafeWalk removes an empty child directory after the parent's first file is
  yielded and before lazy descent. The walker must report that unseen subtree.
  This exercises IOException without a concurrent filesystem mutation.
- Watchdog waits for a heartbeat naming the young operation before moving its
  clock, then observes later heartbeats for the wedged clock state. A fixed sleep
  did not prove another timer tick ran.
- IdleExit sends eligible ticks directly to the atomic latch claim. Its former
  separate read made the lost-claim return depend on a narrow scheduling race.
  Noneligible ticks retain the already-fired veto. Tests cover pre-fired latches
  in all eligibility states and exactly one winner among concurrent ticks.

These changes need the complete repository gate and integration suite before
merge; the intermediate unit-run evidence above is not that gate.
