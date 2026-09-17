# ADR-029: Plugins and the host publish through the work owner

Status: Accepted (2026-09-17). Builds on ADR-028.

## Context

ADR-028 added `PluginWorkOwner` and wired nothing to it. `PluginFramework` and
`PluginHost` still answered "is this plugin busy?" and "is the host at rest?" with their
own mechanisms: `runSlots` and its lock, `inflightCount`, `anyRunSlotBusy`,
`reportUnlessRunOwns` with a second copy for cache replay, a separate `agentFault`, and
the host's `WorkCycleGenerations`. Commands read state through `PostAndAsyncReply`, so a
read waited behind a running `Update`, and tests used that wait as a synchronisation
barrier.

## Decision

Every plugin is a row in its host's `Store`, owned by an `Owner<'State>`. The mechanisms
above are deleted. What replaces each:

- **Busy, progress and faults** are read from the owner snapshot. `RegisteredPlugin.IsBusy`,
  `CompletedDispatches` and `Fault` read the plugin's row. `PluginHost.AnyPluginBusy`,
  `BusyPluginNames`, `CompletedDispatches` and `FaultedPlugins` read one pinned
  `HostSnapshot`. `FailedWork` and `FailedOperations` are new.
- **An event is owned from admission until its state is committed.** Dispatch admits the
  event before posting it to the executor; `DispatchTracked` returns its receipt. The
  executor commits in a fixed order: publish the candidate, run `PrepareCommit`'s
  `Finalize`, write the cache entry, settle the receipt. A failed `Update` fails its
  receipt and publishes nothing. A failure after publication keeps the candidate. A
  failed preparation or finalization is a commit failure, which only a later successful
  preparation clears. A failed cache write without `PrepareCommit` is an update failure,
  which any later committed event clears.
- **The executor's state is the owner's state.** The loop reads `owner.Snapshot.State` for
  each event. A crashed loop calls `FaultExecutor`, which fails every admitted receipt and
  keeps live workers owned. Admission to a stopped executor raises the failure that
  stopped it.
- **Commands are two contracts.** `PluginCommand.Observe` reads the published snapshot and
  never waits behind work. `PluginCommand.Request` never sees state; it posts, or queues an
  intent with `EnqueueExclusiveIntent` and awaits that intent's committed state.
- **An exclusive run holds its key until its result is committed.** `RunExclusive` claims
  the key through the owner. The worker runs in the execution context the plugin was
  registered in, inside a child-process scope of its own, so its children are torn down
  before it can retire. A shared run classifies its result and releases the resource while
  it still holds the key; if either fails, the run fails and no success is folded. A shared
  starter that fails keeps its key until the scheduler has handed the resource on
  (`SharedRunStart.SharedStartFailed`).
- **One status funnel.** Every status a plugin reports, a cache replay reports, or a fault
  forces goes through one function. It drops a terminal while the owner snapshot holds a
  live exclusive run. The lock that remains, `statusLock`, only orders that decision and
  its report against a claim's `Running` report; ownership itself is read from the
  snapshot. A run's own failure is reported before the run retires.
- **The host owns its own work.** A dispatch fan-out is a host operation until every
  recipient has admitted the event. Each preprocessor pass, and each preprocessor inside it,
  is a host operation until its outcome is published; a refusal or a throw is recorded on
  that operation.
- **Rest for `WaitForComplete`** is one pinned snapshot with no owned work, plus the
  existing quiescence window and verdict guard. The per-plugin generation leg is gone: a
  reported `Running` is a status, not ownership, and no longer keeps a settling wait open.
- **Tests synchronise on completion witnesses.** A command is no longer a mailbox barrier.
  Tests wait on a dispatch receipt, on host rest, or on a plugin's committed-event count
  (`HostSnapshot.CompletedEventsOf`) when a gated run keeps the host busy.

## Rejected

- **Version-stamped status reports instead of `statusLock`.** Stamping each report with the
  publication it was decided against, and dropping older stamps at the host, would remove
  the lock. It changes `PluginHostServices.ReportStatus`, and every host that consumes it
  would have to apply the ordering rule itself.
- **`WorkCycleGenerations` as an accessor over committed-event counts.** A committed event
  is not a work cycle: a cache hit commits an event without running anything. Keeping the
  name would keep a leg of the wait that no longer means what it says.
- **A second pending-receipt table beside the owner.** The owner already holds each
  event's receipt.

## Not in this change

- Scans and change batches still run in the daemon's own agents, outside the store, and
  are not host operations. The quiescence window still covers the gaps they leave.
- Exclusive runs and preprocessor passes have no supervisor deadline. A callback that never
  returns stays owned.
- `requireVerdict`, the quiescence window and `activeVerdictWaits` remain.
- TestPrune and Build still keep closure-local mirrors of their state, and Build's
  force-rebuild is a flag rather than an owner intent.
