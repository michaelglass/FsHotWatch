# ADR-028: Plugin work has one owner, and the host has one publication

Status: Accepted (2026-09-17). The vocabulary has landed; nothing is wired to it yet.

## Context

Whether a plugin is busy, and whether the daemon is at rest, is answered today by several
mechanisms that change independently:

- `PluginFramework` keeps `runSlots` under its own lock, a `statusLock`,
  `reportUnlessRunOwns`, `inflightCount` and `anyRunSlotBusy`.
- `PluginHost` keeps `WorkCycleGenerations` and `CompletedDispatches`.
- `setStatus` is fire-and-forget.

Commands are `PostAndAsyncReply`, so a read waits behind a running `Update`. A throwing
`Update` still acknowledges its event as committed. The cache is written before the state
it describes is published. Preprocessors run outside any accounting. A reader that
samples these one at a time can see a gap between a finished run and the successor it
launched, and report rest that never happened.

## Decision

`src/FsHotWatch/PluginWorkOwner.fs` defines one owner per plugin and one publication for
the host. The next change moves `PluginFramework` and `PluginHost` onto it and deletes the
mechanisms listed above. This change adds the vocabulary and its tests only.

- **One writer, one immutable publication.** `Store` owns a single mailbox and publishes
  one `HostSnapshot` per accepted change. The snapshot holds every plugin's row and every
  named host operation: preprocessor passes, dispatch fan-out, scans. Readers take the
  published reference and never go through the mailbox. A fan-out keeps its own operation
  until every recipient is admitted, so no reader sees rest between two admissions.
- **Identities cannot be forged.** `WorkId` has a private constructor. Only a store mints
  one, from its own identity and the publication serial. Another owner's work, or work
  already retired, is refused, and a refused transition publishes nothing.
- **Transitions are pure.** Each takes a `Snapshot<'State>` and returns the next snapshot,
  a result, at most one intent to deliver, and receipts to settle. The store publishes
  first; the owner then runs the effects on the calling thread. A receipt therefore never
  settles before the state it acknowledges is visible. A queued successor is owned in the
  same publication that retires its predecessor, before the predecessor's receipt
  completes.
- **The phase is typed.** `Phase` is `Resting | Working`, and `Working` is never empty.
  Each exclusive key has one holder: either a live `Worker`, or a run of uncommitted folds
  followed by at most one successor worker. A key cannot have two workers. Commands
  waiting for a key sit in its `ExclusiveSlot` (`Running | RunningQueued`), oldest first,
  and a coalescing command replaces the payload of its queued twin while keeping that
  twin's place and receipt.
- **A failure is recorded with the publication that recorded it.** Success clears a
  failure only when the successful work was admitted after it, and only for a kind that
  success disproves:
  - any later event clears an update failure;
  - a later prepared commit clears a commit failure;
  - a later worker's own result clears a run failure.

  A missed run deadline outranks an ordinary update failure. An executor failure is kept
  as first reported. `FailEvent` accepts only `EventFailure` (update or commit), so an
  executor failure cannot be passed to it.
- **An executor failure keeps live workers owned.** Every event, fold and queued intent is
  retired, and its receipt fails with the executor's cause. Live workers stay in the
  snapshot until they actually finish; the fault does not prove they stopped. New
  admission is refused. A worker that finishes afterwards retires without a result fold.
  An intent whose delivery throws stops the executor, and the receipt of the predecessor
  that freed its key still settles.

## Rejected

- **A volatile state reference beside the existing mailboxes.** It would add one more
  authority and keep all the old ones. It would also silently change what commands that
  choose work see.
- **Per-plugin snapshots read one after another.** Reading separate snapshots at separate
  instants can miss a cross-plugin handoff. Rest has to be one publication.
- **A flat map of obligations, each carrying its own queue and origin.** An earlier
  prototype did this. It could represent two workers for one key, needed a kind check at
  every use, and split one key's FIFO across two entries that had to be merged on commit.
- **Clearing failures by kind alone.** A worker's result that folds after its successor
  has failed would erase the successor's failure.

## Not in this vocabulary

Earned test evidence, the project-model generation a result was counted against, and
client observation leases are left to the changes that need them. The store's row API
(`Register`, `Change`, `Read`) is already general enough for the scan and change-batch
supervisors to publish into the same snapshot.
