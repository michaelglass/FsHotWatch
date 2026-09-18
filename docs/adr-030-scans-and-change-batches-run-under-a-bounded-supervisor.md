# ADR-030: Scans and change batches run under a bounded supervisor, never inside a mailbox

Status: Accepted (2026-09-17). Builds on ADR-027, ADR-028 and ADR-029.

## Context

After ADR-029, plugins and host operations publish through one `Store`, but the daemon's
own long work still ran inside two mailboxes:

- The scan agent awaited `performScan` while handling `RequestScan`, and answered
  `GetState` and `GetGeneration` through the same mailbox. `scan-status` therefore
  blocked for the length of a scan, including a discovery stuck in MSBuild. The scan was
  not host work, so rest readers saw it as idle. The `Scanning(x/y)` status arm could
  never render: nobody could read the state while it was true.
- `changeAgent` received watcher events with no obligation, spent the debounce window
  outside any accounting, and awaited `processBatch` inline. A hung rediscovery looked
  idle and stopped every later change.
- A daemon disposed during or before a scan never answered `ScanAll`: the cancelled
  mailbox dropped the reply (noted in ADR-027).

Each of these was reproduced as a failing daemon test before this change.

## Decision

- **One supervisor for external work.** `SupervisedWork.Queue` is a row in the host
  `Store`. The row publishes a typed phase (`Resting`, `Running`, `Finishing`), the domain
  state, the queued requests, admission closure, completed count and last failure in one
  value. Callbacks run on a worker task in the execution context the queue was built in,
  never in the store's writer. Readers take the published row.
- **Every request runs under `SupervisedWork.execute`.** It arms a finite deadline before
  invoking the callback, runs the callback in its own child-process scope (ADR-027), and
  tears the scope down before the result is delivered. The daemon uses the ambient RPC
  deadline (`FSHW_VERDICT_DEADLINE_SEC`, else 60 minutes). An unbounded or non-positive
  deadline is refused before anything is admitted.
- **A deadline records; only the callback's return retires.** Expiry records a
  `TimeoutException` on the active request and requests cancellation. The request stays
  owned until its callback actually returns. A caller that stops waiting, whether through
  `Submit`'s admission bound or its own cancellation, withdraws nothing: admission, launch,
  receipt and cancellation effects are attached to the publication.
- **Completion is published before it is announced.** The callback's result is published
  in `Finishing`; the completion notification and the deadline's release still run under
  the deadline; then retirement and the successor's admission are one publication, and
  only after it does the predecessor's receipt settle. A failing notification or deadline
  release fails the request.
- **Close keeps live work owned.** `Close` refuses further admission, settles queued
  requests with `ObjectDisposedException`, and requests cancellation of the active one.
  Cancellation callbacks run off the closing thread, so one that blocks or throws cannot
  stall shutdown.
- **A worker fault settles what it accepted.** A worker task can only fault inside a
  writer transition before its request retires (a throwing `failed`, or a successor's
  `beginWork`). Its observer settles that request and everything queued behind it with the
  failure and closes admission.
- **Watcher input is owned from the callback.** `DebouncedWork.Queue` publishes pending
  input and the ordered cohorts awaiting supervisor admission in the same `Store`. `Post`
  returns after its input is published. A cohort leaves the admission list only once the
  supervisor owns it, and admissions run one at a time taking the list's head, so cohorts
  keep their order whichever thread scheduled them. A zero delay flushes pending input as
  a barrier. A debounce timer acts only on the cohort it was armed for: an obsolete timer,
  whether it fired or failed, changes nothing.
- **Daemon wiring.**
  - Scans run on a `"scan"` queue. `GetScanState`, `GetScanGeneration` and
    `FormatScanStatus` read its row, and `performScan` publishes `Scanning(total, done)`
    as it checks files. `scanSignal` fires from the completion notification, after the
    completed generation is published.
  - Change batches run on a `"changes"` queue whose state is the preprocessor
    suppression set, fed by a `"changes"` debounce queue. `fshw format` posts a
    zero-delay cohort, so it runs after the changes that preceded it.
  - `Dispose` closes change input and its worker, then the scan queue, before the
    process registry reaps.
  - `ScanAll` raises `ObjectDisposedException` after disposal.
- **Scan waiters see failures.** `ScanSignal.ObserveScan` binds waiters to the admitted
  scan request. A waiter bound to a failing request receives its failure, including one
  that registers after the failure, and a recovery scan queued behind it cannot resolve
  it. The `Scan` RPC replies only after its request is admitted and bound, so the
  `WaitForScan` a client sends next can never be answered by an earlier failure. The IPC
  `WaitForScan` handler now awaits the waiter it raced against shutdown; it used to return
  success for a faulted waiter.
- **Idle exit claims its latch once.** An eligible tick makes one atomic claim instead
  of reading the latch and then claiming it; an ineligible tick still reports a fired
  latch. The losing claim is now reachable without a thread race.

## Rejected

- **A busy counter around `processBatch`.** It misses the debounce window and adds a
  second execution authority beside the store.
- **A detached scan task plus a busy flag.** Also a second authority, and nothing would
  own the scan's receipt across a queued successor.
- **Retiring work when its deadline expires.** A timeout does not prove a callback
  stopped. Reporting rest while it still runs is the defect this replaces.
- **Scheduling each flushed cohort straight into the supervisor.** Continuations from
  concurrent posts can run in any order, so cohorts could enter out of order.
- **Refusing a success whose queue closed during its completion notification.** The
  work finished and its state was published; reporting cancellation would be false.

## Consequences

- An in-flight scan or change batch is host work. `AnyPluginBusy`, `BusyPluginNames`
  (`scan`, `changes`), idle exit, the heartbeat and `WaitForComplete` all see it.
- A failed or cancelled scan leaves the scan state `idle`, where the mailbox used to
  keep the previous state.
- Scans and change batches now have a deadline. One that exceeds it is recorded as an
  operation failure (`PluginHost.FailedOperations`) and cancelled.
- Cancelling a `ScanAll` caller cancels that request's scan.
- `WaitForComplete`'s stall detector reads owned work with no committed progress and
  nothing `Running` as a wedge. A scan spending more than its stall threshold in
  discovery, before any file is checked, now meets that description.

## Not in this change

- Exclusive plugin runs and preprocessor passes still have no supervisor deadline.
- `requireVerdict`, the quiescence window and `activeVerdictWaits` remain, as does
  `ScanActivity`'s lease set.
- Scan and batch results are not yet bound to the project-model generation they were
  captured against.
- TestPrune and Build still keep closure-local mirrors of their state.

## Amendment: the wait belts come off, and the stall detector learns what a deadline means

This record's "Not in this change" listed `requireVerdict`, the quiescence window and
`activeVerdictWaits` as surviving; its Consequences admitted that a scan spending more than
the stall threshold in discovery "now meets the description" of a wedge. All four are
resolved here, and the last one was a live defect: a cold discovery owns work for minutes
while no plugin event completes anywhere, which is byte-for-byte the signature the detector
fires on.

- **The 200 ms quiescence window is deleted.** It covered work the daemon had not yet handed
  to the host. Nothing needs covering: `EmitFileChanged` opens the owner's dispatch
  operation and returns only once every plugin has admitted the event, so a hand-off cannot
  be read as rest between two snapshots. One publication answers rest.
- **`requireVerdict` is deleted; the wait asks for evidence.** It required that some plugin
  had reached a terminal state, which is a report, not a verification. `WaitForComplete`
  now rests when the host owns no work AND something has earned evidence for the model it
  would be answering about (ADR-033): a test receipt, an analysis receipt, or a completed
  build failure. A host that observes no model resolves rather than blocking — nothing can
  ever earn evidence for a model that does not exist, and a green over one is refused by
  `CheckVerdict` (`ModelUnavailable`, exit 2) and by `Verdict.create`. The wait stopped
  carrying a duty two other guards hold.
- **A completed build failure is evidence.** A red build runs no tests and analyses nothing,
  so it minted neither receipt and an evidence wait had nothing to end on over a tree whose
  answer was already printed. `BuildPlugin` mints `CompletedBuildFailure` in the fold that
  records the outcome. A build that PASSED mints none: a green build is not evidence of
  itself, and the runs it enables earn that.
- **`activeVerdictWaits` is replaced by a client-observation lease.** The counter lived
  beside the publication: the daemon incremented it around the RPC while idle-exit read the
  host's work from somewhere else, and two readings of one fact can disagree. An in-flight
  verdict wait now takes a lease published WITH the work it waits on, so "a client is
  waiting" and "the host owns nothing" come from one snapshot. A watcher is not work: the
  lease inhibits idle exit and never makes the host busy. Taken inside the wait, so every
  exit releases it — verdict, timeout and shutdown cancellation alike.
- **The stall detector is bounded by the deadline, not by the silence.** Live work whose row
  is `Supervised` — run under a finite deadline, which `SupervisedWork` sets and nothing
  else does — counts as progress while it is inside that deadline. Past it, the deadline
  records its own failure, the row stops counting, and the wedge fires exactly as before.
  An ordinary event fold is never `Supervised`: nothing will ever time it out, so a handler
  that never returns is still named, which is the case this detector exists for.
