# ADR-002: `BatchChecked` event added; `RequireWarmStart` workaround removed

Status: Accepted (2026-05-01)

Supersedes the implicit "RequireWarmStart is the cold-start contract" note
that lived in `PluginFramework.fs` (around line 121–123 prior to this ADR).

## Context

Pre-ADR, the framework dispatched only per-file events (`FileChanged`,
`FileChecked`) plus per-result events (`BuildCompleted`, test lifecycle).
Two consumers — `TestPrunePlugin` and `BuildPlugin` — needed a *cohort-level*
signal but didn't have one, so each emulated it on top of per-file events:

- **`TestPrunePlugin.RequireWarmStart = true`** (and the same flag on
  `BuildPlugin`) suppressed cache replay until each plugin reached a terminal
  status once per session. The flag existed because the cache key was derived
  from state (TestPrune's `changedSymbolsRef`, BuildPlugin's
  `BuildInputsHasher`) that wasn't fully populated when the *very first*
  dispatch hit on cold boot. Without the gate, the cold-boot dispatch hit a
  half-formed key, replayed a stale cache entry, and the real run never
  happened.
- **`BuildPlugin.WaitingForFcsPhase`** tracked a `Set<AbsFilePath>` and
  drained it as `FileChecked` events arrived. Used exclusively for the
  test-only-changes branch ("don't rebuild; wait for FCS to confirm the N
  changed test files type-check, then emit `BuildSucceeded`"). The plugin
  was reconstructing batch-completion from a per-file event stream.

Both patterns wanted the same upstream affordance: *an event that fires once
after a defined cohort of `FileChecked` events has finished*.

## Decision

Add a single new event, `BatchChecked`, signal-shaped (no per-file results
in payload), with a `Trigger` discriminator distinguishing boot scan from
in-session debounce batches:

```fsharp
type BatchCheckedTrigger =
    | BootScan
    | InSessionBatch of originating: FileChangeKind list

type BatchChecked =
    { Trigger: BatchCheckedTrigger
      Files: AbsFilePath list
      Generation: int64
      StartedAt: System.DateTime
      CompletedAt: System.DateTime }
```

The event fires:

- After the last `FileChecked` of a boot scan, *before*
  `scanSignal.SignalGeneration` (so `WaitForScanGeneration` callers can
  assume `BatchChecked` has already been dispatched by the time
  `fshw scan --wait` returns).
- After the second `emitResults` of `processBatch`'s source-files path
  (so any rediscover-then-source mixed batch collapses into one cohort).
- **Not at all** for empty cohorts (every file filtered as content-unchanged,
  or no source edits in the batch).

## Plugin migrations

| Plugin | Subscriptions before | Subscriptions after | Notes |
|---|---|---|---|
| TestPrune | `FileChecked` + `BuildCompleted` | `FileChecked` + `BatchChecked` + `BuildCompleted` | FileChecked retained for per-file analysis fold (Item 1's "fold stays in the plugin"). BatchChecked is the cohort-complete seal that re-publishes `state.ChangedSymbols` into `changedSymbolsRef` before any subsequent `BuildCompleted` racing the same change. |
| BuildPlugin | `FileChanged` + `FileChecked` (drain `WaitingForFcsPhase`) | `FileChanged` + `BatchChecked` (replace with `WaitingForBatchPhase`) | Per-file `Set.remove` drain collapses into one `Set.isSubset expecting batchFiles` check on the cohort signal. |
| Lint, Analyzers, FormatCheck, FileCommand, Coverage | unchanged | unchanged | These plugins' work *is* per-file; no cohort signal needed. |

### TestPrune: Item 1 vs Item 3 contradiction in the design doc

The design doc (`docs/plans/2026-05-01-project-scan-event-design.md`) had an
internal inconsistency between Item 1's payload section ("payload deliberately
does not include per-file results … every plugin's per-file fold stays in the
plugin") and Item 3's subscription table line ("FileChecked dropped" for
TestPrune). Resolution: **Item 1 wins.** TestPrune retains both subscriptions.
Item 3's table has been corrected to match.

The cache-key-race fix that motivates removing `RequireWarmStart` doesn't
require dropping FileChecked — it requires that `changedSymbolsRef` be
fully populated by the time the next cacheable event (`BuildCompleted`)
hits. The agent's mailbox is FIFO and the daemon emits `BatchChecked`
strictly after the last `FileChecked` of the cohort, so processing
BatchChecked *as a seal* is sufficient.

### BuildPlugin: `WaitingForFcsPhase` → `WaitingForBatchPhase`

The phase rename + subset check follows design Item 7's recommendation:
late-arriving `SourceChanged` mid-wait merges into the `expecting` set; on
`BatchChecked`, if `expecting ⊆ batch.Files`, emit `BuildSucceeded`,
otherwise hold the wait (the next BatchChecked will cover the merged set).

## `RequireWarmStart` removal

With `BatchChecked` in place, the half-formed-key window the gate existed
to guard is closed. Specifically:

1. On daemon restart with no on-disk changes, `performScan` runs and
   `BatchChecked { Trigger = BootScan }` fires after every FCS check. No
   `BuildCompleted` follows (no source change) → no cache lookup against a
   stale key → tests don't run spuriously.
2. On daemon restart with a real change, the `BatchChecked` handler in
   TestPrune folds the change into `state.ChangedSymbols` (and the seal
   re-publishes to `changedSymbolsRef`). The subsequent `BuildCompleted`
   then computes a cache key from a *fully populated* `changedSymbolsRef`
   — either replays the matching prior-session entry or runs fresh. Either
   is correct.

Removed:

- `PluginHandler.RequireWarmStart` field.
- The `hasCompletedThisSession` flag and gate in `tryReplayCache`
  (`PluginFramework.fs`).
- All `RequireWarmStart = true|false` initializers across plugin
  definitions and tests.
- Tests that asserted plugins opt into the gate.
- The old "cold-start BuildCompleted bypasses task cache and runs tests"
  TestPrune regression — replaced with its inverse: cold-start with
  unchanged state replays the cached run.

## Consequences

- One new event in the framework's surface (`BatchChecked`,
  `SubscribeBatchChecked`, `DispatchBatchChecked`,
  `host.EmitBatchChecked`) plus a small payload type.
- `PluginHandler` shrinks by one field. No more cold-start gate to reason
  about; cache replay is the same on the very first dispatch as on the
  thousandth.
- `BuildPlugin`'s test-only-changes branch is structurally simpler: one
  `Set.isSubset` instead of per-file `Set.remove` drains.
- TestPrune adds one match arm and one explicit seal; its existing
  per-`FileChecked` accumulation is unchanged.
- The removed cold-start "always re-run once" symptom — which often
  surfaced as a redundant test run on every daemon restart — is gone for
  unchanged repos. Repos with real edits since the prior session still
  trigger a real run on the first matching `BuildCompleted`.

## Open question

Per-project `BatchChecked` for very large monorepos (100+ projects) was
considered and deferred (design Item 7's "lean: don't design for it now"
section). If a future plugin wants per-project granularity, it should be
introduced as a *second* event (`ProjectChecked`) rather than retrofitted
into `BatchChecked`. Most plugins will keep using `BatchChecked`.

## Related

- Design doc: `docs/plans/2026-05-01-project-scan-event-design.md`.
- Migration commits:
  - `framework: add BatchChecked event type and dispatcher`
  - `testprune: add BatchChecked subscription, flush via cohort-complete signal`
  - `buildplugin: replace WaitingForFcsPhase with WaitingForBatchPhase`
  - `framework: delete RequireWarmStart`
