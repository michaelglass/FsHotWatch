# ADR-031: TestPrune and Build decide from committed owner state

Status: Accepted (2026-09-17). Builds on ADR-026, ADR-029 and ADR-030.

## Context

ADR-029 gives every plugin one owner. A cache key receives the committed state, commands
observe a published snapshot, and `PrepareCommit` runs before an event is acknowledged.
TestPrune and Build still decided from closure-local copies of their state, which ADR-029
and ADR-030 list as not yet done:

- TestPrune's cache key read `outstandingFailuresRef`, `sessionCoverageRef` and
  `changedSymbolsRef`. It ignored the state it was given. A completion wrote those cells
  before its fold reported status, so a completion whose fold then failed could still make
  a red plugin cacheable.
- Verification debt lived in cells beside the state: the pending queue, symbol revisions,
  unknown-debt recovery, the full-suite baseline and runtime coverage obligations.
  `Update` discharged it and wrote the sidecars itself. A failed fold could therefore
  discharge debt on disk, and a retained snapshot could see a later discharge or a later
  baseline.
- A completion discharged every symbol it launched, even one edited again while it ran.
- `test-scope` and `check-reach` read `completedRunsRef` and `checkReachRef`, so reading an
  older snapshot showed a newer completion.
- `set-scope` wrote `fullSuiteScopeRef` from the IPC thread and replied before any fold had
  applied it.
- Owed runs were a `PendingRerun` flag and a `QueuedCommandRuns` list beside the "tests"
  key, not work the owner held.
- `ArtifactsUnavailable` and `TestHostUnavailable` resolved the `run-tests` reply inside
  `Update`, before the outcome was published.
- Build's `force-rebuild` set a closure flag and replied at once. Every snapshot's cache
  key could see that flag, and any completed build cleared it. The cache key read
  `activeTestRunsForCache`, a copy of `state.ActiveTestRuns`.

Each of these was reproduced as a failing test before this change (Build: 3 tests;
TestPrune: 7). So were two further gaps:

- A scan's build replayed a cached green over dependency fanout still owed by a launch
  that could not run (2 tests, one through the host's task cache).
- A build dropped a change whose claim met a finished build's result fold (2 tests). Under
  CI load it also failed a host test.

Four more tests were written after the state types changed. Each fails when the guard it
pins is removed.

## Decision

- **Debt is state.** `TestPruneState.Debt: VerificationDebt` holds the pending queue, the
  revision of each queued symbol, unknown-debt recovery, the baseline and the runtime
  obligations. `CompletedRuns`, `CheckReach` and `FullSuiteRequested` are also state. Every
  helper that used a closure cell takes the debt it decides from. The cells are gone. Only
  `freshnessRef` and the `pendingAgeRef` diagnostic remain.
- **Cache keys read the supplied state.** Neither plugin's `CacheKey` reads anything but
  the state and event it is given.
- **Durable debt follows publication.** `Update` proposes and writes no discharge.
  `PrepareCommit` compares the candidate with the committed state:
  - When the queue, failures, obligations, baseline or recovery changed, it writes
    `owner-publication-pending`, then only the sidecars that changed.
  - When a previous publication left that marker, it rewrites every sidecar.
  - `Finalize` removes the marker after publication, then resolves the replies the
    candidate owes.
  - A restart that finds the marker loads the debt as unknown, which widens every run to
    the full suite.
  - While debt is unknown nothing is written, so the unreadable record stays the honest
    one.
  - One write still happens before publication: the flush writes the queue before the
    analysis snapshot advances. It only adds to what is on disk.
- **A launch captures revisions.** `TestRunLaunch.SymbolRevisions` records the revision of
  each launched symbol. A completion retires a symbol only while its revision is unchanged.
  `TestRunLaunch.ChangedFiles` records the files the launch consumed. A completion clears
  only those files, so a file changed during the run selects the next run.
- **Owed runs are intents.** A trigger that finds the "tests" key held queues
  `ImpactRunRequested` with coalescing key `impact`. It comes from `BatchChecked`, from
  `BuildCompleted`, or from a claim that loses to a fold. The intent is delivered after the
  holder's result is committed. Its fold flushes, launches only if something is still owed,
  and launches with the pending fanout. `run-tests` is an intent on the same key. Its
  command reports a refused intent instead of waiting for a run that cannot come. A
  completion launches nothing itself.
- **A launch that cannot run hands its work back.** `ArtifactsUnavailable` and
  `TestHostUnavailable` carry the fanout their launch consumed, and it is owed again. They
  queue no run: the next build or cohort seal is what can make artifacts or a host
  available. Every scan runs a build, and its `BuildCompleted` launches with the owed
  fanout. While fanout is owed, TestPrune does not take part in the task cache for
  `BuildCompleted` or a completion, the same rule as a non-empty queue. Otherwise that
  build could replay a cached green over it: the fanout is not a key input.
- **Replies follow publication.** The fold of a `run-tests` completion
  (`CommandTestsFinished`) and of an unavailable launch lists its reply in
  `TestPruneState.Replies`, which `Finalize` resolves. `set-scope` and `force-rebuild`
  await their intent's receipt. Each uses its own key (`scope`, `force-rebuild`), so neither
  waits behind a running suite or build.
- **Build's force-rebuild is state.** `BuildState.ForceRebuild` is set by the
  `ForceRebuildRequested` fold and cleared by the fold of a build that actually ran. The
  cache key reads it and `ActiveTestRuns` from the state it is given.
- **A refused build claim keeps its input.** `IsRunning "build"` is true only for a live
  build. After a build finishes, its result fold still holds the key. A test completion
  or change folded before that result reads the key as free, and its claim is refused.
  The refusal used to drop `PendingFiles`, so a change that arrived while a test host held
  the artifacts was never built. Now `startBuild` and `startTemplateBuild` keep the input
  they were launched for. The holder's own `BuildDone` fold builds it, since it runs
  `launchPending` and may claim the key it holds.
- **Coverage ingest failure reaches the owner.** The run still writes the durable recovery
  marker before it returns. It then posts `RuntimeCoverageFailed`, whose fold makes the
  debt unknown. Only a state that recovers from unknown debt removes that marker.

## Rejected

- **Writing sidecars from `Update` and rolling back on failure.** A crash between the write
  and the rollback leaves exactly the discharged-but-unpublished record this replaces.
- **Keeping `PendingRerun` beside an intent.** Two queues for one key is the defect, whatever
  name the second one has.
- **Adding the owed fanout to the cache key.** An entry keyed by it would still be a
  green that never ran those projects; replaying it is the defect.
- **Queuing a retry after an unavailable launch.** With artifacts that stay invalid, that is
  a loop with no new input. The owed fanout waits for the event that can change the outcome.
- **One marker per sidecar.** Several sidecars change together on a completion. A restart
  needs to know whether that set was published, not whether each file was written.
- **`force-rebuild` and `set-scope` on the "build" and "tests" keys.** The reply would wait
  for a whole build or suite, and `confirm` sends both before its scan.

## Consequences

- A scan's build no longer replays a cached green while dependency fanout is owed.
- A restart after a crash during publication runs one full suite before impact filtering
  resumes.
- A `run-tests` reply and a `set-scope` reply arrive after their state is published, so a
  read sent next sees them.
- Owed runs and queued `run-tests` requests run in the order they were queued. Queued
  `run-tests` requests used to run before the impact rerun.
- The cache entry a completion writes is keyed from the state that completion folds into.
  A test that asks what the next lookup computes must read the committed state
  (`TestPrunePluginTestSupport.registerWithStateObserver`), not `Init`.
- Plugin API changes (TestPrune): `TestPruneState` gains `Debt`, `FullSuiteRequested`,
  `CompletedRuns`, `CheckReach` and `Replies`, and loses `PendingRerun` and
  `QueuedCommandRuns`. `TestRunInputs` gains `Debt` and `FullSuiteRequested`.
  `TestRunLaunch` gains `SymbolRevisions` and `ChangedFiles`. `TestPruneMsg` gains
  `CommandTestsFinished`, `ImpactRunRequested`, `ScopeRequested` and
  `RuntimeCoverageFailed`. `ArtifactsUnavailable` and `TestHostUnavailable` gain `owed`.
- Plugin API changes (Build): `BuildState` gains `ForceRebuild`, and `BuildMsg` gains
  `ForceRebuildRequested`.

## Not in this change

- `IsRunning` still excludes result folds, and other readers still act on it:
  - TestPrune's `test-results` and `test-scope` report the previous committed result, not
    `running`, while a finished run's fold is pending.
  - `CoveragePlugin` skips a `TestRunCompleted` whose `coverage-check` claim meets a
    finished check's fold, so that trigger is dropped.

- `freshnessRef` (the file-freshness sidecar) is still a closure cell that only `Update`
  reads and writes.
- A green is not yet minted from one evidence value bound to the project model generation.
- `requireVerdict`, the quiescence window and `activeVerdictWaits` remain.
