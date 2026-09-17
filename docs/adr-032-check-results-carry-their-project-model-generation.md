# ADR-032: Check results carry the project-model generation they were captured against

Status: Accepted (2026-09-17). Builds on ADR-029, ADR-030 and ADR-031.

## Context

The daemon's `DiscoveryCoordinator` serializes every clear/load/map/register attempt and
reports an `Observation` (`Rediscovering`, `Available`, `Unavailable`, `Unobserved`). A
verdict read refuses green unless the model is `Available`. Nothing tied a check result,
or the cohort that produced it, to the model it was computed against:

- A scan skips its own discovery when the `.fsproj` fingerprint is unchanged. It then read
  the live graph and pipeline. A scan admitted while another writer was between clear and
  completion saw zero projects and completed at once, as a clean scan.
- A scan read its membership, then ran preprocessors and waited for the build. A
  rediscovery that cleared the model in that window did not stop it. The scan went on to
  publish "Checked 0 files (0 tiers)" against the replaced model.
- A change batch captured nothing. After a rediscovery it checked files from whichever
  model was live and sealed them with no model identity. Retrying it would have asked the
  content tracker again. The tracker answers "changed" only once per content, so the retry
  would have dropped the source the batch had admitted.
- TestPrune folded every `FileChecked` it received. A result queued under one model could
  enter pending analysis after the model was replaced. A `BuildCompleted` of the new model
  then flushed that analysis into the symbol index.
- A test run launched under one model could complete after a rediscovery. It still
  discharged debt, recovered unknown debt and earned a baseline and receipt for the model
  that replaced it.
- A full-suite recovery from unknown debt set the runtime coverage obligations to empty. An
  obligation raised while the run was executing was lost, even though the run never built it.

Each of these was reproduced as a failing test before this change: 2 daemon tests (4
cases), 2 analysis tests, 1 run-scope test (2 cases) and 1 coordinator ordering test.
Two further gaps in the first design also got failing tests first:

- TestPrune refused only a result stamped with another model, so an unstamped result
  still folded.
- A change batch superseded on every attempt retried without end.

## Decision

- **Results and seals name their model.** `FileCheckResult.ModelGeneration` and
  `BatchChecked.ModelGeneration` are `int64 option`. `CheckPipeline` produces `None`. The
  daemon stamps the generation it captured when it publishes. `None` means no completed
  model was captured.
- **A cohort captures its model.** `DiscoveryCoordinator.Capture` waits until no attempt is
  pending, then reads under the admission lease that every attempt's work holds. If an
  attempt was requested before the lease was taken, it releases and waits again. A scan
  captures membership, dependency tiers and project options together and checks only that
  copy. A change batch captures the epoch at its start and again after its own rediscovery.
- **Publication is guarded.** `DiscoveryCoordinator.WithCurrent(epoch, write)` runs `write`
  under the coordinator's state lock, and only while no attempt is pending and the
  completed attempt is still the captured one. Otherwise it raises
  `ModelSupersededException`. Every `FileChanged`, `FileChecked` and `BatchChecked` a scan
  or change batch emits goes through it, and so does a scan's final seal, even an empty one.
  A later attempt with identical counts is still a different model. `write` must be a
  short publication that never waits on the coordinator.
- **A superseded scan refuses. A superseded change batch retries, a bounded number of
  times.** A scan fails with the exception, and its supervisor retains the `scan`
  operation fault (ADR-030). A change batch runs the same changes again against the
  current model, inside the same supervised request. The retry answers "has this content
  changed?" from the first attempt's record, so the admitted input survives. A superseded
  attempt may have published some results, but never its seal.
- **A model that keeps changing fails the batch by name, and its changes stay owed.**
  After `changeBatchAttemptLimit` (5) superseded attempts the batch fails with
  `ModelKeptChangingException` ("The project model kept changing during the batch").
  The `changes` operation fault stays visible until a later batch succeeds. The change
  worker keeps the failed batch's changes and the paths it admitted, and runs them ahead
  of the next request. The same bytes arriving again therefore still run, even though the
  content tracker has already seen them. Nothing starts that next batch on its own: a
  retry the batch schedules for itself is the unbounded loop again.
- **The host publishes the model.** `HostSnapshot.ProjectModel` and `ProjectModelFiles`
  come from `Store.PublishProjectModel` and `PublishProjectModelWithFiles`. A non-available
  observation retires the previous checkable membership in the same publication. The
  daemon passes the coordinator a publish callback that writes to the store. The settling
  attempt announces while it still holds admission, so no cohort can capture a model
  before plugins can observe it. Announcements are published outside the state lock, so
  each takes its place in line under the lock. One that arrives after a later announcement
  was published is dropped. A delayed `Rediscovering` therefore cannot overwrite the
  `Available` that followed it.
- **Plugins observe the model.** `ProjectGraphAccessor.ObserveModel` is the model a plugin
  observes. A `PluginHost` always supplies its own work store's model, whoever installed
  the graph, so a graph accessor cannot claim a model the host did not publish. A host no
  daemon drives publishes no model and observes `Unobserved`. `ProjectGraphAccessor.none`
  returns `Unobserved`.
- **TestPrune folds only the current model.** It ignores a `FileChecked` or `BatchChecked`
  unless the event is stamped with the generation of the available model the host
  publishes. A result from a replaced model has a cohort that is retried against the
  current model or refused. An unstamped result was captured against no model, so it
  cannot describe this one. `TestPruneState.AnalysisModelGeneration` records the model
  pending analysis was accepted under. When the observed model changes while analysis is
  pending, the analysis is retired before any flush. Its changed symbols are already in
  the durable queue and stay owed. The index can no longer vouch for what covers them,
  so every configured test project owes a run. This is a deliberately conservative
  choice. Working out which projects the retired analysis could have selected would mean
  trusting an index that the replaced model's analysis never updated.
- **A launch captures its model.** `TestRunLaunch.ModelGeneration` is the model observed
  at launch. A completion observed under another model discharges nothing, like an abort:
  no symbol, obligation, recovery or baseline. Its receipt is revoked.
- **Recovery keeps newer obligations.** Recovering unknown debt no longer clears the runtime
  coverage obligations. The launch retirement has already removed the obligations the run
  built, and any left are newer than the run.

## Rejected

- **Reading the model at publication time instead of capturing it.** A cohort that read a
  cleared graph would publish an empty result that is consistent with the model current at
  publication. The defect is the mismatch between what was checked and what is current.
- **Retrying a superseded scan inside the supervisor.** A scan answers a client that asked
  about the tree at request time. Its failure is visible and a new scan recovers. A change
  batch is internal owed work with no client to tell, so it retries, and when it gives up
  its changes stay owed.
- **Publishing the announcement under the state lock.** A subscriber that reads the
  coordinator back would deadlock against the lock it was published under.
- **Waking discovery waiters after the announcement.** Verdict admission reads the
  coordinator, not the store. Ordering comes from the admission lease.
- **Accepting unstamped results.** Every consumer would accept a result that names no model,
  which is the hole the stamp exists to close.
- **A distinct stamped type on what plugins receive.** It would make an unstamped result
  unrepresentable. But it changes the `PluginEvent` payload that every plugin matches
  on, and the check pipeline's result type. The refusal gives TestPrune the same guarantee
  with added fields only.
- **Retrying a superseded batch until its supervised deadline.** A watcher batch has no
  client deadline, so the bound would be the ambient default. That is far longer than
  a storm worth waiting out, and it spends FCS checks on every attempt.

## Consequences

- A scan admitted during a rediscovery waits for it rather than reporting an empty model.
- A scan whose model is replaced before it publishes fails with "The captured project model
  generation N was invalidated before scan publication." The next scan runs normally.
- A change batch whose model is replaced reruns its preprocessors and checks against the
  new model.
- A test run that completes after a rediscovery earns nothing. The owed work runs again.
- A bare `PluginHost` with no published model gives TestPrune no analysis. Test fixtures
  publish one (`TestHelpers.createModelHost`) and stamp their results (`stampFixture`),
  as the daemon does.
- A change batch whose model changes on each of 5 attempts fails, and the verdict sees
  the `changes` fault. Its changes run with the next batch.
- Rediscovery while TestPrune holds unflushed analysis forces one run of every configured
  test project. That is the cost of the conservative choice above: a project-file edit
  whose rediscovery lands between a cohort's `FileChecked` events and its seal runs the
  full suite once. A rediscovery that finds no unflushed analysis, the common case since
  every seal flushes, forces nothing.
- Plugin API changes (core): `FileCheckResult` and `BatchChecked` gain `ModelGeneration`.
  `ProjectGraphAccessor` gains `ObserveModel`. Code that constructs these records must
  set the new fields.
- Plugin API changes (TestPrune): `TestPruneState` gains `AnalysisModelGeneration`.
  `TestRunLaunch` gains `ModelGeneration`.

## Not in this change

- Analyzers, Lint and the other `FileChecked` consumers still fold stale-model results. A
  retried cohort republishes them against the current model.
- A green is not yet minted from an evidence record that joins completion, discharged debt,
  report, input identity and model generation. `requireVerdict`, the quiescence window
  and `activeVerdictWaits` remain.
- An analysis-only daemon has no analysis evidence. The checkable membership published in
  `ProjectModelFiles` is not yet read.
