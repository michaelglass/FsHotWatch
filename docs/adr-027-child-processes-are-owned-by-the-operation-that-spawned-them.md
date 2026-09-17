# ADR-027: Child processes are owned by the operation that spawned them

Status: Accepted (2026-09-17)

## Context

`ProcessRegistry` is how fshw reaps the processes its plugins start. Four defects in it
were reproduced as failing tests before this change, on the code as it stood:

1. **Shutdown did not close admission.** `KillAll` iterated the live set and then
   cleared it. A `Track` that landed during or after the iteration was either dropped by
   the clear or kept by a registry nobody would ask again. A child registering after
   shutdown stayed alive (reproduced with `sleep 30`: still running 5 s after `Track`).
   A launch through a shut-down scope ran its target in full.
2. **A deadline did not reach the child.** `runWithCancellableTimeoutTracked` cancels a
   token. A callback that ignores the token and waits on a child kept that child running
   for its whole natural life (reproduced: alive more than 7 s after the deadline).
3. **A disposed handle counted as an exited child.** `HasExited` on a disposed `Process`
   throws `InvalidOperationException`. Both `Snapshot` and `KillAll` treated that as
   "not alive", so a caller that tracked a live child and disposed its handle retired
   successfully while the child ran on, and shutdown recorded nothing.
4. **Output pumps inherited the caller's scheduler.** `Task.Factory.StartNew` without a
   scheduler uses `TaskScheduler.Current`. A caller running on a scheduler it blocks
   (it is parked in the watchdog loop) never started the pumps. A healthy child came back
   as `Failed(91, DrainTimedOut "")`.

## Decision

- **Admission and the shutdown snapshot are one transition.** `Registry` keeps a closed
  flag and the live set under one lock. `KillAll` closes and takes the snapshot in that
  lock. A later admission is refused, and the refusing registry reaps that exact handle.
  `ProcessHelper.runProcessTo` checks for a closed scope before spawning, so a refused
  target has no side effects. It checks again after the spawn, for a shutdown that lands
  in between, and raises `OperationCanceledException` rather than reporting the reaped
  child's exit as the target's outcome.
- **Termination is bounded and needs evidence.** `KillAll` kills its snapshot side by
  side and waits one budget for all of them, not one per child. Only a handle that
  positively reports exit establishes termination. A kill that fails, a child still
  running after the budget, or a handle that can no longer be observed is recorded in the
  leak ledger with the pid captured at admission. The registry never resolves that pid
  again, because the OS may have given it to someone else.
- **Each deadline-bounded operation gets its own child scope.** `withChildScope` installs
  a registry that forwards admission, untracking and leaks to its parent, so daemon
  shutdown still sees every child. Cancelling the operation tears down the scope's
  children only, never a sibling's, and refuses later launches from the same work
  without closing the parent. When the work ends, anything it left tracked is torn down
  before it retires. `runWithCancellableTimeoutTracked` runs its work in such a scope.
- **Uncertain termination refuses success.** If the scope recorded a leak, work that
  would otherwise complete raises `InvalidOperationException` naming the pids and
  reasons. An exception the work raised itself is preserved unchanged.
- **Setup is protected.** Between admission and the watchdog's decision, a failure
  (a pump that cannot start, an observation that throws) kills the child before
  propagating. Pumps run on `TaskScheduler.Default`.

## Not decided here: descendants of an exited child

A fifth defect reproduced as well: `runProcessTo` untracks a shell that exited while a
backgrounded descendant, with its streams redirected away from our pipes, keeps running.
`KillAll` cannot reach it, because the descendant was reparented when the shell exited
and no tree walk from a dead parent finds it. In-process F# cannot close this. It needs
containment set up at spawn time: a process group or session on Unix, a kill-on-close job
object on Windows. That means a helper process that owns the group, and it is deferred.
The need has only been shown with a constructed fixture, never in a real daemon log.

The fixture, kept here rather than as a skipped or permanently red test, so whoever
reopens this starts from the reproduction:

```fsharp
let registry = ProcessRegistry.Registry()
use _ = ProcessRegistry.install registry

// The shell prints its backgrounded child's pid and exits 0 at once.
let outcome =
    runProcessTo (Some sink) "/bin/sh" "-c \"sleep 30 </dev/null >/dev/null 2>&1 & echo $!; exit 0\"" dir []
        (ProcessBounds.silent (TimeSpan.FromSeconds 15.0))

// `sink` resolves the announced pid into an observer handle while that child is alive.
registry.KillAll()
Assert.True(descendant.WaitForExit(5000))   // fails: the descendant is still running
```

Observed on the code before and after this decision: `outcome` is `Succeeded`, the
registry's snapshot is empty, and the assertion fails with the descendant still running.
The fixture must reap its observer handle in `finally`.
Reopen this when an orphaned descendant shows up in a daemon log, or when the runtime
offers the containment directly.

## Consequences

- A registry that has shut down stays shut: a daemon's spawns after `Dispose` raise
  `OperationCanceledException`.
- A daemon disposed while a scan is in flight still never answers that scan's
  `ScanAll` reply. This was observed while writing the shutdown test, is unchanged by
  this decision, and the test does not depend on it.
- The public API is unchanged. `Registry()`, `Track`, `Untrack`, `Snapshot`, `KillAll`,
  `ReportLeak`, `Leaks` and the module functions keep their signatures. The parent
  constructor, admission and the scope are internal.
