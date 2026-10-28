# ADR-047: Old reads that land after a newer answer are refused, dependents included

Status: Accepted (2026-10-28). Builds on ADR-046. AUTOMATION-681 slice 5.

## Context

ADR-046 refuses a scan's `FileChecked` when a later epoch changed that file and the
snapshot now holds other bytes. It named a gap. Suppose a save changes `Early.fs`'s
declarations, and its change cohort re-checks and publishes `Late.fs`, whose bytes did not
change. A scan that had already checked `Late.fs` against `Early.fs`'s old declarations then
lands after the cohort's answer, and is still published. This was reproduced first, as a
failing test: DaemonTests "a scan's answers read before a save's cohort, landing after it, are
refused, dependents included" holds the scan's answers between their read and their
publication, and saw two results for `Late.fs`.

## Decision

- **Every answer says when it read, and what it typed against.** The new
  `FileCheckResult.Upstream : CheckedAgainst option` carries two things:
  - `ReadAt` comes from the check pipeline's read sequence. It is taken in
    `CheckFileCached` before the lookup reads the file or its upstream. It is monotonic per
    pipeline, which means per session, so of two answers about one file the higher number
    read later.
  - `Fingerprint` is the file's source-only upstream (`UpstreamFingerprints.ForSnapshot`,
    over `CheckCache.sourceUpstreamFingerprints`): the files before it and the sources of
    the F# projects it references, with no assembly or other build output. It is `None`
    when the inputs moved during the check.
- **A cohort's publications are recorded.** At each change-batch `FileChecked`, inside
  `WithCurrent`, the session's ledger records the epoch, the checked bytes and the
  `CheckedAgainst` (`Ledger.RecordPublished`).
- **Old work is judged against the newer answer.** A scan answer captured at epoch *c* is
  refused, and counted, when both of these hold:
  - a cohort published an answer about the same file at an epoch after *c*, and that answer
    READ LATER than this one did;
  - this one answers another question: other bytes, or (when both are known) another
    fingerprint.

  ADR-046's bytes rule still applies. Both run under the discovery state lock, so whichever
  publication lands last has been compared with the other.
- **Test seam.** `Daemon.HoldScanPublications` (internal) is called with each answer a scan
  is about to publish, after its read and before its publication. Only this seam makes the
  real race (the scan reads before the cohort and publishes after it) deterministic.

## Rejected

- **Refusing on any differing answer, without read order.** This was built first, measured,
  and rejected. A different answer is not necessarily an older one: a scan can read NEWER
  inputs than the cohort that published before it, for example after a build rewrote a
  generated source. IntegrationTests ColdCheckCommandTests "an edit no symbol diff sees
  outside compiled source still runs every project in full" failed 3/3 with that rule.
  Baselines eadfd46d and 8a3eba74 passed 2/2, and this design passes 3/3.
- **A fingerprint that includes assemblies.** A build that rewrites an output would move it
  although the snapshot did not change. Whether a dependent saw older declarations is a
  question about sources.
- **Re-computing the upstream fresh from disk at publication.** That costs a closure hash
  under the publication lock, and disk can be ahead of every admitted cohort.

## Not in this change

- Debt materialized against a named epoch (the dispatch half of slice 5 in the slice plan).
