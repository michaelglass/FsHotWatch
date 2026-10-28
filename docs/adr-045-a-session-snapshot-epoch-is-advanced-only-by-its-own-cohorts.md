# ADR-045: A session's snapshot epoch is advanced only by its own accepted cohorts

Status: Accepted (2026-10-27). Builds on ADR-032 and ADR-044. AUTOMATION-681 slice 3
(the "snapshot epochs" item AUTOMATION-938 also lists; 681 carries it).

## Context

A repository host serves several worktrees, one session each. ADR-032 guards publication by
the project-model generation, and ADR-044 counts what that guard refuses. Neither names the
CONTENT a session's work was done against. The later slices need that name: publication CAS
on (incarnation, epoch, generation) (slice 4), debt materialized against a named epoch at
dispatch (slice 5), and attaching a new session from the nearest sibling's manifest (938).

## Decision

- **`SnapshotEpoch` per session.** A monotonic counter, starting at 0, held by a
  `SnapshotEpoch.Ledger` that each `Daemon` owns. A host's sessions each wrap their own
  daemon, so each has its own ledger, and a save in one worktree never moves a sibling's
  epoch. `Daemon.Snapshot()` reads the current manifest, `Daemon.SnapshotHashed()` what the
  ledger has hashed.
- **An immutable manifest per epoch.** `Manifest = { Epoch; Entries; Id }`, where `Entries`
  maps a repository-relative path (forward slashes) to `Content id` (SHA-256 of its bytes),
  `Unreadable`, or `Removed`. `Id` hashes the entries, each path length-prefixed, in ordinal
  order, so equal manifests have equal ids whatever order a cohort named its paths in. A
  cohort produces a new value; the previous epoch's manifest is never mutated.
- **Advanced only by an accepted change cohort.** `processBatch` advances the ledger after
  its attempt completes against the current model, and never from an attempt that was
  superseded (`ModelSupersededException`) or that failed with its changes owed. A scan does
  not advance it.
- **The cohort and its closure are hashed, nothing else.** The paths are the ones whose
  content the cohort admitted (owed ones from a failed batch included) plus the files its
  preprocessors rewrote. A rewrite's watcher echo is suppressed, so without this it would
  never reach the manifest. Each distinct path is read once. Dependents and later files
  in compile order are re-checked, but their content did not change, so they are not read.
- **The epoch moves only when an entry changes.** A cohort whose paths all hash to what the
  manifest already records leaves the epoch where it was. What it hashed is still counted.
- **Paths are named against either spelling of the root.** A native watcher reports real
  paths (`/private/var/…` for a macOS `/var/…`), and a preprocessor may report either
  form. The ledger is given both, so both name the same entry.

## Rejected

- **Reusing `ContentDedup.Tracker`'s hashes.** It hashes a `project.assets.json` with its
  `project.restore` block removed, which is the right identity for "does a check need
  re-running" and the wrong one for "what bytes does this path hold". It also never sees
  a preprocessor's rewrite. The manifest hashes for itself, and its count is what the
  acceptance test measures.
- **Advancing the epoch inside `WithCurrent` with the seal.** An empty cohort, or one with
  only project inputs and no checkable file, seals nothing but did change content. The
  epoch names content, not model publications, so it advances where the cohort is accepted.
- **Advancing per cohort whether or not an entry changed.** Two epochs with one manifest id
  would name one snapshot twice.

## Not in this change

- **Epoch 0 is implicit.** The manifest is an overlay of every path the session's cohorts
  changed since its discovery loaded the tree, with removals kept as tombstones. Seeding
  epoch 0 from a scan, and attaching a session from the nearest sibling's manifest (938),
  come later.
- **No wire field.** The epoch and manifest id are not in `status` or `status --repository`
  yet. Slice 4 will stamp publications with them, and expose them then.
- **No CAS on the epoch.** Publication is still guarded by the model generation alone
  (ADR-032). Slice 4 generalizes `WithCurrent` to (incarnation, epoch, generation).
