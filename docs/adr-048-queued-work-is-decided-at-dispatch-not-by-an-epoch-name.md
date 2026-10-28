# ADR-048: Queued work is decided at dispatch against the tree, not by an epoch's name

Status: Accepted (2026-10-28). AUTOMATION-681 slice 5, second half. Builds on ADR-030,
ADR-031, ADR-045 and ADR-047.

## Context

681's slice plan asked for "declarative debt materialized against a named epoch at
dispatch, replacing queue-time captured state". The test it named was that coalesced
scan/build debt keeps one waiter and runs against the newest epoch.

I surveyed every kind of work a session queues behind running work:

- **Scan requests** (`ScanRequest.RunScan`). A request queued behind a scan is decided when
  it is dispatched, by `answeredByLastScan`. A request admitted before the running scan read
  the tree is answered by that scan, and so is a request admitted while it read, provided
  the tree on disk is still the one it checked. "The same tree" means the same project
  fingerprint, no restore, the same model, and every source and in-repository reference
  re-read and compared with the scan's stamp. Otherwise the request runs a real scan.
  Several waiters share one answer.
- **Change cohorts.** They run one at a time on one queue. A failed cohort's debt is the
  changes it still owes, together with the paths it already admitted; the next cohort
  admits and checks them against the model current at that moment.
- **TestPrune's queued runs** (`ImpactRunRequested`, ADR-031). Each is decided "against the
  state it is delivered into". It flushes first, launches only if something is still
  owed, and is dropped as stale only when nothing is owed and the evidence receipt covers
  the tree on disk.

So, inside one session, queued work is already materialized at dispatch. It is decided
against the current tree, not against state captured when it was queued.

## Decision

- **Do not replace these dispatch-time checks with an epoch comparison.** An epoch counts
  the cohorts the session has admitted (ADR-045). An edit still waiting out its debounce,
  or one a watcher never delivered, moves the disk without moving the epoch. "The request's
  epoch ≤ the last scan's epoch" would therefore answer a request that the disk shows is
  owed. DaemonTests "a scan requested mid-scan is a real scan when an input changed after
  the scan read it" is exactly that case: a project file edited with no watcher event must
  still run a real scan. Today's stamp re-read refuses it; an epoch-only rule would not.
- **The epoch stays a publication guard** (ADR-046, ADR-047). It decides which answers may
  be published, and it never decides whether work is owed.

## Not in this change, and where it goes

- **Answering one session's debt from another session's snapshot** (the host-level half:
  a sibling has already checked the same manifest). That needs a complete epoch-0
  manifest, which ADR-045 leaves implicit, and attaching from the nearest manifest. Both
  belong to AUTOMATION-938's "attach from the nearest session's manifest".
