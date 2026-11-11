# ADR-066: A check with FCS errors stamps its file dirty

Status: Accepted (2026-11-11)

Reverses the trade-off `FileFreshness.markUnverified` recorded for checks that report FCS
errors. AUTOMATION-1560.

## Context

The freshness sidecar (`.fshw/test-prune/file-freshness.json`) says whether a file's stored
symbol rows came from an FCS-clean check. The `detectChanges` call site trusts them only when
they did: `Clean, RowsFromPriorRun` diffs a new extraction against the stored rows, and `Dirty`
widens instead (`EverySymbolIsNew`: every symbol the file holds is new to the index).

A check with FCS errors drops the file's changes (`FileUnverified`) but still queues its
extraction, and the next flush writes it as the file's stored rows. Until now, its sidecar
stamp was `markUnverified`, which keeps an earlier Clean record. The stated reason was
**cold-start reliability**: a cold FCS sometimes reports spurious errors ("expected type X but
here has type X") for files that compile cleanly once warm. Downgrading those files to Dirty
would make each one widen when it recovers, so the stale Clean record was kept "until the next
genuine clean check refreshes it".

Kept over rows that a dirty check wrote, that Clean record makes the recovering clean check
diff against the DIRTY extraction. Consider one save that changes the body of `answer` and also
introduces a type error elsewhere in the file. The fix removes the error and keeps the change.
Measured in `TestPruneFreshnessGateTests` ("a change made in a check FCS reported errors for is
still selected once the file checks clean"), the recovery's log line reads
`stored=Clean, rows=RowsFromPriorRun, trust=DiffAgainstStored … 1 changes, 3 diffed against`.
The one change is the removed error; `answer`, changed in the broken edit, compares equal to
itself and is never queued. No test covering it runs, and the check is green.

## Decision

`FileFreshness.stamp` takes a `CheckStamp` (`FcsErrors | CleanBeforeBuild | VerifiedClean`) in
place of a bool. `FcsErrors` stamps the file Dirty (`markDirty`), so the recovering clean check
takes the existing `Dirty → EverySymbolIsNew` widening for that one file. `markUnverified` stays
for the case it fits: an FCS-clean check before the session's first build.

## Cost

A cold boot whose FCS reports spurious errors for N files now widens each of those files once,
on its first clean check: it selects what that file's symbols reach, not the whole suite, and
the clean check stamps it Clean again. Before, those N files cost nothing and a real change made
under an error was silently dropped. One widening per file is the price of not dropping it.

## Rejected

- **Not queueing a dirty check's extraction** (keep the last clean rows as the baseline). That
  also closes the hole, but the rows are persisted on purpose: cold-scan rows must reach the
  index, and other files' impact queries read them. It changes more than the stamp does.
- **Keeping `markUnverified` and documenting the hole.** Its warning ("the next FCS-clean check
  of this file widens it back in") was true only when the sidecar was already Dirty or absent.
