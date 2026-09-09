# ownership design — current-source constraints

This document records the shared design obligations before the next production change. The RPC synchronous-prefix correction has targeted green evidence; neither full ticket is complete. The source specification is the current Plane descriptions, with the leading correction withdrawing the net-negative-LOC criterion.

## Required final ownership

One host work owner publishes an immutable aggregate containing admitted work and each plugin's committed evidence. A plugin owner publishes one immutable PluginCore containing its domain state, typed phase, and exclusive slots. Readers observe published references, never a mailbox query. UI status is not gate evidence.

A transition that finishes work must publish its result and admit all consequent work before retiring the last predecessor obligation. Empty ledger cannot be visible between a completed test process, its result fold, and a required successor run. The existing implementation releases inflight before recurring with nextState; adding a snapshot after that release would preserve a false-rest interval.

The host owns aggregate admission/finish transitions. Per-plugin snapshots alone cannot prove global rest: reading separate snapshots at separate instants can miss a cross-plugin handoff. Host publication must atomically represent retiring the predecessor and admitting all recipients. It must consume evidence objects, not rediscover evidence from UI terminal statuses.

## Commands are two different contracts

A read-only command consumes a published snapshot. A command that selects or starts work posts an intent to the owner and receives that exact operation's acknowledgement/result. A read is never an implicit completion barrier.

The current production hazard is `run-tests --only-failed` in TestPrunePlugin: its command callback reads LastResults/OutstandingFailures before posting RunTestsRequested. Snapshot conversion must move the state-dependent choice inside the owner request. Otherwise a completed run already accepted by the owner could be absent from the snapshot used to choose failed projects, or the command could incorrectly report no previous results.

Tests that currently query a command to drain the mailbox must await the actual event acknowledgement. CompletedDispatches can witness controlled single-event tests only when no unrelated Custom completion can satisfy the count; it is not a universal substitute. The independent audit is retained at /private/tmp/command-barrier-audit.md when complete.

## Acceptance and publication boundaries

An admitted operation has a private capability identity. Completion retires that identity exactly once and records its terminal outcome. Admission is an acknowledged write, not a read. A caller whose event has merely been queued for admission has not yet received an acceptance witness. File/input identity still has to be captured and checked so that an unaccepted edit cannot earn current-tree green from an older tree.

Exclusive slots retain the approved shape Idle / Running current / RunningQueued(current,next). A queued successor is part of owned state, not PendingRerun plus a separate busy flag. Manual test runs obtain the same exclusive capability as automatic runs; no IsRunning-check followed by an unowned executeTests call survives.

The complete result fold holds launch identity, actual outcomes, pending obligations, and coverage evidence together. It is the only mint site for test verdict evidence. The existing Events.RunVerdict private record is not sufficient: public RunVerdict.create accepts only summary/elapsed. Keep reportable activity data separate from a constructor that validates actual outcomes and coverage.

Coverage evidence includes the model on which obligations were counted. the completed discovery generation must travel with the scan/model observation and resulting coverage. Unobserved, rediscovering, unavailable, or mismatched model generations cannot mean healthy empty impact. Unrunnable obligations cannot be silently dropped. Preserve selection soundness and coordinate exact-tree receipt reuse; the tracked issue remains NeedsApproval and is not implemented by inference.

## Nonblocking owners and the outer supervision boundary

Owners process transitions and schedule effects; they do not await scans, preprocessors, builds or tests inline. Existing asynchronous PluginHandler.Update implementations are migration input, not proof they are nonblocking owners. State-dependent work selection belongs at the transition; expensive work returns a completion message identifying the operation and launch state.

A supervised operation retains its obligation through completion processing and resource cleanup. Timeout of a caller does not prove an in-process callback stopped. For noncooperative FCS or plugin work, preserve the bounded daemon recovery boundary; do not report empty work merely because the awaiting RPC timed out. Process shutdown must account for descendants and preserve failure evidence.

A single bounded spawn abstraction must cover scans, change batches, exclusive runs and preprocessor passes. Preserve deadlines, cancellation, synchronous startup failure, worker failure, completion-classifier failure, fair shared-resource handoff, and crash/wedge diagnostics. SharedRunScheduler must not release ownership into an observable gap while handing it to the oldest waiter.

## Replacement checklist for the integrated change

The final integration removes separate inflight/run-slot/status ownership checks, WorkCycleGenerations, the fixed200ms quiescence window, requireVerdict, activeVerdictWaits, and PendingRerun where the new ledger subsumes them. It removes convergence/retry mechanisms used to reconstruct truth, while retaining independently justified finite recovery of external work. A new ledger beside all the old authorities is not acceptance.

The snapshot regression remains an actual assertion-red receipt until its implementation and command migrations pass. Required deterministic traces include stale terminal replay during a live run, completion-to-successor handoff, stale launch identity, skipped/unrunnable coverage, duplicate terminal delivery, startup/worker/cleanup failure, and scan-status during blocked work. Full FsHotWatch CI and integration tests, followed by current-tree evidence review, are required before merge or Plane QA.

## Rejected next step

Do not replace PostAndAsyncReply with a volatile state reference alone and call the tickets finished. Apart from retaining the old authorities, that would silently change write-command selection and tests' FIFO acknowledgement contract. The next implementation boundary must explicitly distinguish observational reads from owner-resolved commands and publish committed state before acknowledging an event.

## Executor-fault transition (current-source follow-up)

Current `PluginFramework.registerHandler` constructs each tracked event's completion source outside `PluginWorkOwner`, posts it alongside the admitted identity, and completes it only after `CommitEvent`. Its `agent.Error` handler records a separate mutable fault without resolving accepted receipts. The prepared `ExecutorFault` control injects an Update failure plus a throwing status transport, positively observes the stopped executor, and requires the exact accepted receipt to report failure instead of timing out. This control has not run yet.

Move tracked receipt ownership into the same owner transition that admits the event. An Event obligation may carry its asynchronous completion source; receipt continuations must use RunContinuationsAsynchronously. Successful CommitEvent publishes domain state and retires that exact identity before its receipt is completed. The owner's published snapshot must also retain the executor fault, replacing the separate mutable agentFault authority.

FaultExecutor must atomically publish the fault and retire only event obligations that the stopped executor can no longer process. It returns their receipt completion effects for failure delivery after publication. Existing exclusive obligations remain visible until their workers and resource cleanup actually settle: failure of an event waiter is not cancellation proof for those workers. New event admission after the fault must fail immediately rather than enqueue to the dead executor. A late event completion cannot publish state after the fault or retire an unrelated run. Shared and ordinary workers completing after executor failure must take an explicit failed-result path rather than transfer to an event that can never fold.

Required controls beyond the prepared original-API red are a queued second receipt, admission after the observed fault, an exclusive worker held through fault and released afterward, and an immutable pre-fault snapshot. Do not add a second pending-receipt dictionary beside the owner ledger or clear every obligation on executor failure. This is still one part of the full host-level lifecycle and bounded supervision acceptance, not a substitute for it.

## Shared host owner migration boundary

The next prepared current-API controls block a registered preprocessor and assert host busy plus its owning name for both successful and refused outcomes. Today `AnyPluginBusy` and `BusyPluginNames` inspect only registered plugin owners, while `RunPreprocessors` reports UI status around an unaccounted callback. These controls are uncompiled/unrun.

Do not fix this by adding a preprocessor busy flag or an independent counter beside the plugin snapshots. The host must supply the common mutation owner to each typed plugin owner. A typed Owner remains the capability/transition interface for its plugin state; its published snapshot is a row in the shared immutable host publication. The host owns one mailbox and publishes that aggregate once per transition, so busy/fault/progress reads cannot combine independently changing rows. Standalone framework fixtures may construct a private host owner through the existing default constructor path.

Host fan-out must first admit its dispatch obligation, admit recipients under the same owner before delivery, and retire that dispatch obligation only after recipient admission. Each plugin result transition retires its local identity against the same aggregate that owns descendants. Preprocessor passes, scans and batches require ordinary owned identities in that aggregate too. Preserve existing exact receipt completion after publication; asynchronous work and plugin callbacks remain outside the mutation owner. A registry that merely samples live per-plugin IsBusy callbacks would preserve the original race and is not this migration.

## Shared host publication verified checkpoint (2026-09-08)

The host-preprocessor controls reproduced false rest on both successful and refused blocked passes (2/2 assertion failures). The shared Store now owns the sole publication mailbox for host plugin domains plus named host operations. Typed plugin owners are capabilities into that publication. Dispatch retains a parent through all recipient admissions; preprocessing retains batch and individual identities through outcome classification. Host busy/progress/fault accessors read a pinned aggregate rather than polling independently changing plugins.

Formatted compile passed, framework58/58 and host61/61 passed, and automatic broader unit selection passed3133/3133. Evidence: `docs/evidence/shared-host-owner.json`. No full CI/integration or ticket-completion claim. Remaining scans, batches, supervision, lifecycle, evidence minting and heuristic removal retain the full contract. Earlier formatter deferral was exit3/no verification; daemon disappearance cause remains unproven.

## Scan ownership and observation boundary (prepared, not executed)

Current scanMailbox awaits performScan inside RequestScan and serves GetState/GetGeneration through that same mailbox. Discovery can therefore block observational commands; ScanActivity leases are outside the shared host work publication. Two current-API ScanObservation controls hold the real injected loader during ScanAll, requiring host scan ownership and a readable active-state/generation snapshot before loader release. The existing post-reply ScanComplete/generation control remains required. These new controls are source-only, uncompiled and unrun.

The correction must admit a scan identity before scheduling discovery, publish active state without waiting on external work, and apply completion to the same identity before replying. A caller deadline must not clear a still-live scan. Queued requests must retain ownership across completion/start handoffs; state-setting requests need exact acknowledgement rather than an observational read barrier. The common bounded supervisor remains the required destination for scan, batch, preprocessor and exclusive work; a detached scan worker plus another mutable busy flag is not acceptance.

The prepared ScanObservation group also requires post-disposal ScanAll admission to reject with ObjectDisposedException rather than leaving a receipt on the canceled scan mailbox. Its caller is independently canceled/drained in fixture cleanup so the old behavior cannot leak a waiter. This is source-only, not an executed red. Closing admission and settling already accepted obligations are separate transitions; cancellation of a caller cannot erase a running operation.

## Mixed test-result completeness finding (2026-09-08)

The sibling corrected readiness invocation printed Database1/1 passed, Integration errored/no report, then Tests passed. This is not complete readiness evidence. Current IpcOutput classifies only failed/timed-out as failure and accepts Ran coverage whenever any project executed, allowing that mixed failure through. Three prepared MixedOutcome controls require nonzero for passed plus errored, deferred, or unknown status; a positive control preserves passing filtered selections with unrelated zero-match siblings. These current-API controls are uncompiled/unrun. No production classification change yet. This completeness correction must not claim full completion or substitute a footer for actual outcome evidence.

## Executed checkpoint: mixed-result correction and scan reds

MixedOutcome controls ran: three incorrectexit0 assertion failures and one positivepass. Corrected CLI classification compiled and passed the entire157-test CliTests class. Evidence: `docs/evidence/mixed-result-classification.json`. The subsequent three ScanObservation controls also ran: blocked snapshotread, absent scanownership, and abandoned post-disposal receipt all failed exactly at their intended assertions. Evidence: `docs/evidence/scan-observation-red.json`. Production scan correction remains next; the workspace is not globally green and full CI/integration are not complete. Earlier source-only labels above describe preparation stages, not the current execution status.

## Scan supervisor source implementation — not yet compiled

The three current-API scan reds now have a source correction. SupervisedWork.Queue stores a typed Resting/Running/Finishing phase, domain state, queued request identities, admission closure, progress and failure in the existing host Store publication. External async callbacks execute on a worker task, never in the mutation writer. Active state and FCS progress are published while scan-status/generation reads access the immutable row. RequestScan no longer awaits discovery inside a mailbox.

A scan request retains ownership through its completion notification; the final transition admits its queued successor before releasing the prior receipt. ScanSignal notification moved after completed state publication and is independent of whether the original caller still awaits its receipt. Shutdown closes admission, rejects queued requests, requests cancellation of the active callback and retains that callback until its real completion. The deadline uses the existing ambient RPC budget, records failure and requests cancellation without claiming noncooperative work stopped. Four prepared queue controls cover handoff, close, noncooperative deadline, and a blocked completion notification under deadline.

This source has NOT been compiled or tested. The next checks must include all three actual ScanObservation reds, the four new queue controls, full Daemon/Framework/Host/IPC classes and relevant integration lifetime tests. Remaining work includes migration of change batches, preprocessors and exclusive workers onto the shared supervisor, restart/on-kill policy and shutdown qualification, plus the full evidence/heuristic removal contract. No per-plugin UI status is newly accepted as verdict evidence. Full CI/integration and consumer qualification remain mandatory.

Source review added a fifth queue control for a throwing successor-admission transition. The worker task now has an explicit fault/cancellation observer: it records failure, closes admission, settles accepted receipts for the failed/queued work and preserves any unrelated live callback until actual cleanup. Deadline-callback publication errors are logged and request cancellation rather than escaping as an unhandled timer exception. Synchronous startup-transition failures use the same observer. These changes remain uncompiled/unrun; they do not replace the required verification or remaining supervisor migrations.

The outer shutdown audit found ProcessRegistry.KillAll explicitly allowing concurrent Track additions to escape its snapshot/clear. A source-only LateProcessAdmission control shuts down an empty registry and then registers a fresh owned sleep fixture, requiring that late child to be reaped. This control is uncompiled/unrun and ProcessRegistry behavior is unchanged. It is required follow-through for canceled callbacks that can still reach process launch during teardown; caller cancellation alone does not close the process boundary. No live daemon is signaled by this control.

## Executed scan/supervisor checkpoint and next batch boundary

The scan correction now passes all three original ScanObservation regressions; the supervisor passes seven controls, including late Store publication after the caller's five-second timeout and preservation of the owner's execution context. The late process-registration control produced an actual assertion failure, then passed with the entire112-test ProcessHelper class after atomic admission closure and bounded termination. Exact runs and source hashes are in `docs/evidence/scan-supervision.json`, `supervisor-controls.json`, and `process-admission.json`. Earlier preparation paragraphs are historical. These results do not establish full CI, integration, or consumer qualification.

The next current-API counterexample is watcher batch admission. `changeAgent` receives events without a host obligation, spends the debounce interval outside the ledger, and awaits `processBatch` inline. A blocked project rediscovery can therefore appear idle to host readers. Two new ChangeBatchOwnership controls compose the existing injected loader and inert watcher factory: one checks ownership immediately after delivering the watcher callback, the other positively observes blocked rediscovery first. The fixture releases the loader and observes completion of its sole daemon.check phase before disposing the daemon. No native watcher, shared process or external timing probe is used. These two controls are uncompiled and unrun; production batch behavior is unchanged.

The correction must admit ownership before the watcher callback returns, retain it through debounce/coalescing, and transfer it into the common bounded supervisor before retiring the incoming obligation. The change inbox must continue admitting/coalescing while an external batch runs. Suppression state and format-all ordering belong to the serialized worker's committed state; callers receive their actual format result or failure, not an observational barrier. Shutdown must close both input and execution admission and settle pending receipts without clearing a still-running batch. Do not merely add a busy counter around processBatch: that misses debounce and leaves a second execution authority.

## Production launcher lifetime control prepared

The daemon launcher still executes the existing nohup shell command; its process-group behavior has not changed. That exact launch block is now an internal function called by defaultIpcOps.LaunchDaemon, allowing a regression to supply a short-lived owned shell fixture without launching another full daemon. LauncherLifetimeTests requires the fixture to announce its own PID and remain alive, then compares its actual process group with the caller's using ps. Finally it reaps only that announced child; the script also has a ten-second natural lifetime. This is source-only, uncompiled and unrun.

The control establishes group independence, not the entire lifecycle claim. A complete launcher correction still needs actual red evidence, preservation of executable/argument/path handling, and an isolated outer-fixture termination experiment proving the child survives its original caller group's end. Repeated shared-daemon disappearances after client completion are observations, not proof of a particular signal or sender. No live daemon or shared process group may be used as a sacrificial fixture.

## Change batch source correction — verification pending

DebouncedWork.Queue now publishes pending input and an ordered list of cohorts awaiting supervisor admission in the shared Store. Post acknowledges that publication before returning to the watcher; delayed wakeups take only their matching cohort identity. Zero-delay format input atomically flushes its preceding pending cohort. Only the head of the admission list enters SupervisedWork.Queue, so continuation scheduling cannot reorder cohorts. The supervisor owns each cohort before its input identity retires; callbacks and receipt waits run outside the Store writer.

Daemon changeAgent was replaced by this input owner and the common supervised worker. Suppression state is the worker's committed domain; processBatch gets the worker cancellation token. Format requests await both the cohort receipt and their own result. Dispose closes change input and its worker before process-registry shutdown. SupervisedWork exposes actual asynchronous admission in addition to the existing five-second synchronous caller bound. Two new DebouncedWork tests cover coalescing/barrier order and shutdown retaining a live callback. All changes in this paragraph are source-only, uncompiled and unrun; they must pass the two existing batch reds, supervisor controls, new debounce controls, and broader daemon/IPC regression coverage before any success claim.

### Debounce scheduler failure and launcher API research

A fifth source-only debounce control injects a synchronous delay-scheduler failure. The matching pending cohort now retires with that exact failure and shared snapshot fault; an obsolete timer cannot reject a superseding cohort. Zero-delay flush can recover without using the failed scheduler. This correction and control remain uncompiled/unrun.

Launcher research rules out treating a similarly named .NET property as a Unix detachment fix: the .NET10 CreateNewProcessGroup API is Windows-only ([runtime issue44944](https://github.com/dotnet/runtime/issues/44944)). The newer StartDetached API is documented as part of .NET11 ([official process API announcement](https://devblogs.microsoft.com/dotnet/process-api-improvements-in-dotnet-11/)), while this repository targets .NET10. The [v10 Unix ProcessStartInfo implementation](https://raw.githubusercontent.com/dotnet/runtime/v10.0.0/src/libraries/System.Diagnostics.Process/src/System/Diagnostics/ProcessStartInfo.Unix.cs) and [native process-launch implementation](https://raw.githubusercontent.com/dotnet/runtime/v10.0.0/src/native/libs/System.Native/pal_process.c) are the relevant current-runtime references. Do not silently upgrade the tool runtime or use a Windows-only flag to make the Unix regression green.

A possible dependency-free route to investigate is a short-lived CLI child-entry helper which establishes its own Unix session before executing the existing launch command. This is a candidate, not an implemented/approved final design. It must use the same resolved CLI executable/assembly, preserve argument/path handling, fail on session-creation failure, and be covered by the actual production-launcher and outer-group survival fixtures. Never fork the multithreaded managed caller directly, race parent-side setpgid against exec, or guess opaque native structure sizes.

## Completion projections: actual snapshot counterexample

Two CompletionSnapshot controls retain the first completed TestPruneState, execute a second completion, positively observe changed run identity, then read the retained state again. Both actually failed in full unit3118503f4fe44288bda405481d4ada01: test-scope's session run list and check-reach's run identity came from the newer closure-local cells. The source correction removes completedRunsRef and checkReachRef; their values now live in the existing immutable state and are rebound once with the completion outcome before every successor branch. Observational commands consume that supplied state. Compilation and corrected execution remain pending. This is an incremental removal of competing truth cells, not earned-verdict acceptance; baseline, coverage, pending queue, cache-key mirrors and the final proof constructor still require migration.

## Cache decisions use committed owner state

The failed-completion control first commits a red run, positively observes cache refusal, then injects a terminal-report failure into a passing completion. The old closure mirrors enabled cached replay even though that completion never returned state. Full run ad241f3c125a4a6394420e1e6599f7f4 reproduced that exact assertion failure. PluginHandler.CacheKey now receives the same committed state supplied to Update. TestPrune reads OutstandingFailures and LastCoverage from it, and outstandingFailuresRef/sessionCoverageRef are deleted. Content-only plugins ignore the explicit state argument; live-host cache tests query committed state through an observational fixture command. The control passed in full run d0f84037352c49049b9396c2db48bd79. That run still failed the known private-verdict control and two timing-sensitive fixture assertions, whose explicit-lifetime corrections await verification. This does not finish the remaining mutable pending queue, baseline/model identity, proof constructor, supervisor adoption or heuristic removal.

## Remaining cache input mirrors removed

BuildCacheSnapshot reproduced a served cache key despite supplied state naming an active test run. ChangedSymbolsCacheSnapshot reproduced identical keys for distinct changed-symbol snapshots. BuildPlugin now uses state.ActiveTestRuns; TestPrune uses state.ChangedSymbols. The corresponding mutable cache cells and their writes are deleted. Both controls pass in full unit0a55e867af604d6d8dade6026ce12dc9:3166 total,3165 passed, only the deliberate private earned-verdict regression fails. The build-deferral fixture now positively observes the deferred file in committed state while holding its own actual test process; the analyzer finding on Thread.Sleep is cleared. Evidence is in docs/evidence/cache-snapshot-inputs.json. Full CI, updated integration and consumer qualification remain pending alongside the rest.


## Generated dependency input scheduling verified

The current watcher-to-batch GeneratedFanout control reproduced raw graph sources being scheduled against filtered FCS options. After supplying the synthetic projects with fresh restore metadata, the control reached actual transitive authored checking and failed its generated-input exclusion assertion. Project refresh and source dependency fanout now share pipeline-registered checkable membership, with generated-filtered graph fallback. Full unit report 5ec2afa1ec6a4e9eb7f1b6d98eb62566 is 3168 total, 3167 passed, one outstanding VerdictEvidence failure, zero skipped; the new control passes. Evidence is in docs/evidence/generated-input-fanout.json. This does not complete private earned verdicts, pending-debt ownership, full CI, or consumer qualification.
