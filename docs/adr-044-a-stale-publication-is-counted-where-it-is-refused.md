# ADR-044: A stale publication is counted where it is refused

Status: Accepted (2026-10-27). Builds on ADR-032. AUTOMATION-681 slice 2.

## Context

ADR-032 made every result a scan or change batch publishes go through
`DiscoveryCoordinator.WithCurrent(captured, write)`. The write happens only while the model
the work captured is still the completed one and no newer attempt is pending. Otherwise
`ModelSupersededException` is raised, and the scan fails or the change batch retries. So
old work that finishes after new work publishes nothing.

Nothing counted this. A refusal was visible only as a `debug` log line or a scan fault. The
rollout of the repository host (AUTOMATION-939's gate, 681's stress run) needs a number
it can read: how often old work was kept out, and whether any of it got in.

## Decision

- **Count at the guard.** `DiscoveryCoordinator` keeps `ProjectModel.Publications`
  (`Accepted`, `Refused`) under the same lock as the check. `WithCurrent` adds one to
  `Refused` before it raises, and one to `Accepted` after `write` returns. A `write`
  that throws is not counted as accepted, because nothing was published.
- **Per daemon, so per session.** `Daemon.Publications()` reads its coordinator. A
  repository host's sessions each wrap their own daemon, so `status --repository` reports
  each session's counts (`publications` on each session), and they never mix.
- **Surfaced in `status`.** The `GetDiagnostics` reply carries a `publications` object
  (`{"accepted": n, "refused": m}`). `fshw status` prints
  `publications: n accepted, m refused as stale (…)`. A reply without the object (an older
  daemon) prints nothing, and a malformed or negative count reads as absent, not as zero.
- **"Accepted stale" is an oracle, not a counter.** The guard's check and its write share
  one lock, so a counter of stale publications accepted at the guard would be zero by
  construction and would prove nothing. The claim is tested from outside the guard instead.
  `DaemonTests` "old work finishing after new work is counted as refused, and no stale
  publication is accepted" holds a change batch that captured model 1 inside its
  preprocessing, settles model 2, then releases it. It asserts `Refused >= 1` and that
  every `FileChecked` and `BatchChecked` a consumer received is stamped model 2.

## Rejected

- **An `acceptedStale` field in the status JSON.** See above: it would always read 0 and
  look like evidence.
- **Counting in consumers (TestPrune's "ignoring FileChecked from model …").** A result
  queued before a rediscovery is legitimately stale by the time a plugin reads it.
  That count measures queueing, not the guard.

## Not in this change

- TestPrune refusing a superseded launch's discharge (ADR-032, "a launch captures its
  model") is not counted. It lives in the plugin's state, and would be surfaced through
  its plugin status.
- The two-session host stress run with saves (681's plan, "stale accepted = 0" under load)
  has not been run. This change gives that run something to read.
- `SnapshotEpoch` and CAS on (incarnation, epoch, generation) (slices 3 and 4) will
  generalize the guard. The counter moves with it.
