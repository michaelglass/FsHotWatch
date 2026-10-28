# ADR-043: A file check is leased by its callers, and never cancelled when they leave

Status: Accepted (2026-10-26)

## Context

`CheckPipeline` runs one FCS check per (file, project, generation, snapshot), and every
caller asking the identical question joins it. A caller whose token was cancelled still
waited for that check to finish before it returned. A cancelled scan, or a stopping
session, was held for as long as the slowest check it had started. Plugin work already
has consumer leases (a dropped client withdraws a queued run, or cancels a running run
declared `PluginWork.cooperativeSafe`). File checks had none.

## Decision

- A running check is a producer that its callers lease. The caller that starts it holds
  the first lease, and each caller that joins it takes another.
- A caller whose token is cancelled gives its lease back and returns at once, with no
  answer. The check runs on for every caller still holding a lease, and a later identical
  request can still join it.
- **Nobody leaving cancels the check, not even the last caller.** FCS is not handed the
  check's token, because cancelling an FCS check mid-way is not cooperative-safe. ADR-003
  records an FCS internal-error storm (`Object reference not set …`, 5,038 diagnostics
  across 189 files) under cancellation churn. A superseding check or a rediscovery still
  cancels it, and that only drops the answer.
- A check finishes the same way with or without callers. It then leaves the registry, so
  the answer lives only as long as a caller holds it. When no caller was left, it logs
  `check of <file> (…) finished with no caller waiting for it`.

## Rejected

- **Cancel the check when its last lease leaves.** This is the plugin-work rule, and it
  applies only to cooperative-safe work. An FCS check is not cooperative-safe. Cancelling
  it would also lose the work that a successor joins today: a superseded scan's checks
  are joined by the next scan of the same model.
  Revisit only with evidence on both sides:
  - a count of the "no caller waiting" lines, which is the FCS time spent for nobody;
  - a demonstration that cancelling FCS 43.x mid-check no longer corrupts its answers.

## Consequences

- A cancelled caller is no longer bounded by FCS. It is bounded by scheduling.
- The check keeps its slot until FCS returns, as before. So the work a departed caller
  leaves behind still counts against the bound.
