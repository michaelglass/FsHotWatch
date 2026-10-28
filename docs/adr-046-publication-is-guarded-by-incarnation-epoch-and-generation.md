# ADR-046: A publication is guarded by incarnation, epoch and generation

Status: Accepted (2026-10-27). Builds on ADR-032, ADR-044 and ADR-045. AUTOMATION-681 slice 4.

## Context

ADR-032 refuses a publication whose captured project-model generation is no longer
current. It says nothing about CONTENT. A scan and a change batch run on separate queues.
A scan that captured the model and read `Late.fs`'s old bytes published its result after
the change batch of a save had published the new one (reproduced first: DaemonTests "a
scan's result about a file a later save re-checked is refused…" saw two results for
`Late.fs`, the scan's last). Old work that finished after new work was accepted.

Across incarnations, the repository host's `CanonicalProjects` registry was keyed by
worktree root. A recreated session's predecessor can still be finishing a check after it
was released. The frame choice that check makes claimed the project again under the
root, a ghost hold nothing would release, and a sibling with other content was refused
the virtual root because of it.

## Decision

- **The epoch is taken before any result is published.** ADR-045 advanced the ledger
  after the batch completed. It now advances after the cohort's preprocessors and before
  its first publication; a retried attempt takes it again, and an unchanged manifest
  keeps its epoch. The ledger records the epoch at which each path last changed.
- **CAS on the epoch and the bytes, per file.** A scan captures `Snapshots.Current.Epoch`
  together with its model (`DiscoveryCoordinator.Capture`). At each `FileChecked`
  publication, inside `WithCurrent` (so the model generation is checked too, under the
  same lock), it asks `Ledger.Accepts(captured, file, hash of the source it checked)`. It
  is refused when a later epoch changed the file AND the manifest now holds other
  content: the scan read bytes the snapshot replaced. A refusal is counted
  (`SnapshotStatus.Refused`) and stays out of the scan's seal; the cohort publishes the
  current answer. Ordering is safe because both publish under the discovery state lock
  and the cohort's epoch precedes its publication. A scan result that read the CURRENT
  bytes is accepted even if a cohort also re-checked the file.
- **CAS on the incarnation, for host-shared claims.** `CanonicalProjects.Lease`, one per
  session incarnation (`Registry.Open`), opened with the session's resources and released
  when it ends. `SessionFrames.choiceFor` claims through it. A claim through a released
  lease is refused, holds nothing and is counted (`RefusedRetired`), and a late release
  never ends a newer lease's hold. The bare-name `Claim`/`Release`/`choice` keep their
  old meaning.
- **Surfaced.** `GetDiagnostics` carries `snapshot: {epoch, manifest, refused}`.
  `fshw status` prints `snapshot: epoch N (manifest …), M stale publication(s) refused`.
  `status --repository` carries `snapshot` per session and names it on each session's
  line. A daemon or host that sends none prints as before.

## Rejected

- **Refusing every scan result about a file a later cohort re-checked (whatever bytes it
  read).** Measured, and rejected: 4 IntegrationTests (ColdCheckCommandTests) changed
  their test selection, because TestPrune reads a scan's cohort and seal as the files
  that generation dealt with, and ColdScanCeilingTests' newer-edit scan published fewer
  files. A result of the current bytes is not stale, so it is kept.- **Refusing a scan's whole publication on any newer epoch.** One save anywhere would
  withhold every scan result, and the cohort re-checks only what the save can affect.
- **Invalidating dependents (later files, dependent projects) too.** Under the rule above a
  dependent's own bytes never differ, so its invalidation would refuse only by refusing
  current-bytes results, which is the rejected rule. The gap is named below.
- **Guarding change batches by epoch.** Batches run one at a time on one queue, and a batch
  never refuses itself, so no batch can finish after a later one.
- **An incarnation field on every result.** A session's daemon is replaced whole with its
  incarnation (ADR-032 per daemon), so results inside a daemon cannot cross incarnations.
  The only path across them is host-shared state, and the canonical registry is that path
  today.

## Not in this change

- **A dependent typed against superseded upstream content is still accepted.** A scan
  that checked `C.fs` (unchanged) against `B.fs`'s old declarations and lands after the
  cohort that re-checked `C.fs` is published. Guarding it needs the scan's per-file
  upstream fingerprint compared with the cohort's, which is slice 5's debt
  materialized against a named epoch.- Diagnostics and plugin debt are not separately guarded. They follow `FileChecked`,
  which is.
- The check-result cache is per daemon. When it moves up to the host (938), it needs the
  same lease.
