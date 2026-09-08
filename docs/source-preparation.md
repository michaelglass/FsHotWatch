# source review — 2026-09-08

Source-only observations, not red evidence or implementation. At the initial inspection, workspace a104-a106-work-ledger was empty jj46607ce3 over main082954c7 (alpha45); the interrupted agent had left no saved plan/tests. A regression and this mapping were subsequently prepared. No daemon/build/test was started during this review.

The approved outcome is one immutable single-writer PluginCore and one WorkLedger with typed phase/slots, published snapshot reads, and evidence-bearing verdicts distinct from reportable status. A compatibility layer retaining all old authorities is not completion.

Verified current authorities:
- PluginFramework.fs409–420: mutable Dictionary<string,bool> runSlots and its own lock; duplicate exclusive calls return SlotBusy for caller policy.
- PluginFramework.fs432 onward: a separate statusLock closes terminal-report vs run-claim races. Preserve that semantic atomically in the new owner, rather than simply removing synchronization.
- PluginFramework.fs492–512: inflightCount spans both posted events and run claim through completion post; completedDispatches supports progress-sensitive stall detection. The completion handoff must never publish an empty ledger between work finishing and its result being handled.
- PluginFramework.fs607–639: RunExclusive claims slot, publishes Running, takes work token, then starts background work. Actual expensive work must remain outside the owner loop.
- PluginFramework.fs649–723: RunExclusiveShared combines local slot and host FIFO, handles synchronous start exceptions and failed result classifiers. Preserve FIFO transfer, exactly-once releases, and failure evidence.
- PluginHost.fs65–100: lastActivityAtTicks and per-plugin generation dictionary; independent checkedFiles tracks full-check coverage and invalidates on edits. Coverage obligations cannot disappear when moving activity bookkeeping.
- Daemon.fs1184–1263: 200ms quiescence fallback, five-minute stalled-busy threshold, generations, shutdown cancellation, and requireVerdict prevent cold vacuous green. Replace completion inference with ledger evidence without weakening bounded failure/shutdown semantics.
- TestPrunePlugin.fs1337 onward: LastResults/LastRunId/LastSeeds, PendingRerun, BootScanDebtDuringFullRun, PendingForceRunProjects are separate state. A receipt must retain exact launch identity and coverage evidence, and pending work arriving during a run must survive.

Suggested regression obligations (not yet executable or observed red):
1. Event->run->completion handoffs never expose rest while any admitted obligation remains; snapshot reads respond while injected background work is blocked.
2. Repeated invalidations coalesce only equivalent work; one changed identity while running produces one queued successor, no dropped late change and no debt-free extra run.
3. A stale run completion cannot earn current-tree green or erase later work; exact same-tree baseline reuse preserves receipt identity/scope.
4. Missing baseline, incomplete scan, skipped/unrunnable project obligations, failed coverage ingestion, or unknown coverage debt must prevent constructing green. Reported UI terminal alone carries no proof.
5. Worker throw/start throw/classifier throw, mailbox fault, cancellation and wedge release owned resources and produce bounded explicit failure. Shared FIFO direct handoff remains busy throughout.
6. Host WaitForComplete consumes the published evidence snapshot and all configured plugin/project obligations; no fixed quiet-time substitute for accounting.

Integration order must be chosen after reading existing framework tests and the full current bodies. Start with a real current-API failing regression where possible; type/API sketches are not assertion-red evidence. the completed finite 5-run and 3-run traces are performance/queue evidence, not demonstrated infinite loop or false green. Preserve its durable Plane records. The tracked issue remains NeedsApproval; don't implement its separate scope by inference.

## Concrete current-API first regression and migration hazard

PluginFramework.fs1305 registers commands by PostAndAsyncReply(Choice2Of2). A blocking Update prevents even a pure read command from returning. A deterministic proposed regression can register Init=42/read-count, dispatch an Update that signals entry then waits on a test-owned completion source, and require read-count to return the last committed 42 while Update is blocked. Release the Update in finally, then prove the eventual committed43 through explicit event completion. Use a bounded task wait for the response contract and cleanup; no machine sleeps or test-process hangs. This is source-supported expected failure, not yet run.

Tests currently exploit the OLD query barrier: PluginFrameworkTests.fs350–376 explicitly says running a command queues behind the dispatched message and proves processing. Published snapshot semantics requires replacing this incidental barrier with explicit completion/progress synchronization in affected tests and callers. Do not keep the mailbox query just to satisfy these tests, and do not blindly update expected state to old data without proving post-dispatch completion.

Existing preservation tests worth extending rather than duplicating: PluginFrameworkTests shared FIFO58, start-failure177, faulted-owner211, factory-throw259, classifier-throw302; WaitWedgeTests dispatch fault104, second event129, live-exclusive145, progressing backlog260, dead-loop295; PluginHostTests cold-no-verdict799, subscriber reentrancy831/877, downstream handoff1060/1143 and cascade1228, shutdown1395. These are source references only; current test execution has not been performed.

The snapshot regression is now prepared in PluginFrameworkTests.fs, uncompiled and unexecuted. No production implementation has been changed.

The bounded regression execution plan is `docs/superpowers/plans/2026-09-08-a104-a106-snapshot-regression.md`. It does not replace the remaining shared implementation scope.


## Scan and RPC boundaries observed during control

Candidate source confirms Program.fs783 calls Scan before WaitForScan. Ipc.fs311
Scan first reads GetScanGeneration; Daemon.fs760 uses scan-agent PostAndReply,
which queues behind the RequestScan handler's entire performScan. A cold initial
scan can therefore delay the untracked Scan request while watchdog is idle.
This is a source-supported path, not an observed stack for the active control.

Ipc.fs221 trackedTask invokes its callback before constructing the deadline.
A callback that blocks before returning a Task is outside that timer. The existing
IpcTests deadline test supplies an already-returned never-completing task, so it
does not cover this synchronous prefix. A second current-API regression is now
prepared with a blocking callback and mandatory release/drain in finally. It is
uncompiled and unexecuted, and no production code has changed.

The eventual scan-owner refactor must preserve DaemonTests' completed-generation
and ScanComplete-after-ScanAll contract using publication-before-acknowledgement,
not the old follow-up mailbox-read barrier. Keep shutdown and finite deadlines
for both asynchronous work and synchronous callback prefixes.

## Actual bounded assertion-red phase — 2026-09-08

Root admitted this phase after the tracked issue released capacity (headroom28199 terminal0:75% mean idle,14.6GB available,0checks,10GB two-agent reservation+4GB floor; other agent source-only). No production implementation, Plane mutation, merge or push occurred.

- `mise run compile`: session41993, exit0; 0warnings/0errors;29.54s. Log `/private/tmp/a104-a106-compile.log`.
- `mise exec -- dotnet run --project src/FsHotWatch.Cli --no-build -- test-rerun --project FsHotWatch.Tests --filter-class FsHotWatch.Tests.PluginFrameworkTests`: session42396, exit1. Run64fd2a1fabce4619bad978fa48dba7bc,42total/41passed/1failed/0skipped. The only failure is `read command returns committed snapshot while an update is blocked`; exact assertion: `read command waited behind the blocked update instead of reading a snapshot` (2057ms). Entry was successful and finally released the blocked update; no cleanup failure was reported. The post-finally success assertions are not reached on the expected red and are not claimed observed.
- Sequential `mise exec -- dotnet run --project src/FsHotWatch.Cli --no-build -- test-rerun --project FsHotWatch.Tests --filter-class FsHotWatch.Tests.IpcTests`: session62714, exit1. Runf0b805a6b3de4e5a99031ae703e819fe,51total/50passed/1failed/0skipped. The only failure is `RPC deadline includes a synchronous callback before its task is returned`; exact assertion: `RPC deadline did not cover the synchronous callback before it returned a task` (2008ms). No entry, callbackExited or drain assertion failed; the owned release ran in finally. Log `/private/tmp/rpc-prefix-red.log`.

The runner's configured beforeRun performed its own build; the cold scan formatted the two prepared test files. Those are test-only format changes, not production repairs. The first daemon disappeared before the second request; the second launcher reported stale daemon.pid and started a new daemon. No daemon stop or direct signal was issued by this agent. The cold scan is separate background work, not evidence that either targeted test missed execution. Both CTRF reports name the exact prepared tests and their assertion failures.

Retained compact receipt extraction: `docs/evidence/assertion-red.json`; full reports remain in each named `.fshw/test-runs/<runId>/FsHotWatch.Tests.ctrf.json` with output logs. Both commands are expected red, not a green gate. No exact whole-tree verdict is claimed by these class-run receipts. Base remains082954c7; post-format source SHA256 values: PluginFrameworkTests.fs5691c1624e23669ffe3e960387855b4a40edc2213e9d411916a7253b4460ed2f; IpcTests.fs84c75cf8b2ba80fa929dcc95bb1d285f041c4eb48091fa9e0a850f79fe924318.

Migration audit confirmed two explicit FIFO-query barriers in PluginFrameworkTests: around372 dispatch then read `was-called` expecting processed state, and around654 dispatch failing update then query `noop` before checking status sequence. Replace those with explicit processed-event/completed-dispatch witnesses when snapshot reads are introduced. Do not weaken assertions to accept old state. Audit the neighboring `commands query agent state` and all dispatch-then-command patterns before deleting the mailbox-read path.

Stop point: actual red evidence acquired; production changes remain with root's next coherent PluginCore/WorkLedger design phase. A pair of isolated snapshot/timeout fixes would not satisfy the full scope.

## outer RPC deadline correction — targeted green

The timer now starts before Task.Run dispatches and unwraps the callback, so a synchronous prefix cannot pin the RPC caller outside its deadline. This preserves the existing timeout and watchdog bracket; it does not kill in-process work or complete the shared ledger/supervision design.

Compile35097 exit0, zero warnings/errors27.52s. Supported IpcTests client56655 exit0, run4c5e444713354db0a46f4543453060b7:51passed/0failed/0skipped; synchronous-prefix regression passed100ms. Exact extraction: docs/evidence/rpc-prefix-green.json. Original assertion-red receipt remains unchanged. Last daemon.pid4802 was absent in a fresh ps observation; no signal issued.

snapshot regression remains unresolved. No whole-tree green, full CI, merge, release or Plane QA claim. Next shared implementation must replace old state authorities rather than treating this independently useful outer-guard correction as ticket completion.
