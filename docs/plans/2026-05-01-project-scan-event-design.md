# Project/scan-level event — design (deprecates `RequireWarmStart` workaround)

Date: 2026-05-01
Workspace: `.workspaces/scan-event-design`
Parented on: `zzmupsrt` (Merge: PluginHost — collapse status lock+mutable maps)
Status: research only — no code changes in this commit.

## Summary

Today FsHotWatch dispatches per-file events only (`FileChanged`, `FileChecked`).
Plugins that conceptually trigger at *project-completion* granularity —
TestPrunePlugin and BuildPlugin — emulate one of two project-level signals on
top of per-file events. Both emulations are observably awkward:

- **TestPrunePlugin.RequireWarmStart = true** (`TestPrunePlugin.fs:1368`) and
  **BuildPlugin.RequireWarmStart = true** (`BuildPlugin.fs:647`) suppress
  cache replay until each plugin reaches a terminal status once per session.
  The flag exists because the cache key for these plugins is computed from
  state (TestPrune's `changedSymbolsRef`, Build's `BuildInputsHasher`) that
  isn't fully populated at the moment of the very first dispatch on cold
  boot. Without the gate, the cold-boot dispatch hits the cache against a
  half-formed key, replays a stale result, and the real run never happens.
- **BuildPlugin.WaitingForFcsPhase** (`BuildPlugin.fs:39, 562–588`) tracks a
  `Set<AbsFilePath>` and decrements it as `FileChecked` events drain. Used
  exclusively for the test-only-changes branch ("don't rebuild; wait for FCS
  to confirm the N changed test files type-check, then emit
  `BuildSucceeded`"). The plugin is reconstructing batch-completion from a
  per-file event stream.

Both patterns are missing the same upstream affordance: an event that fires
once *after a defined cohort of `FileChecked` events has finished*. With
that event in hand, both plugins shed the workaround:

- TestPrune subscribes to the new event instead of `FileChecked` for the
  "decide what tests to run" trigger; the cache-key state is fully populated
  at the moment of subscription, so no warm-start gate is needed.
- BuildPlugin's test-only-changes branch waits on the new event for the cohort
  of files it queued, and `WaitingForFcsPhase` collapses into a single state
  branch with no per-file bookkeeping.

This document picks one event shape, names it, sequences the migration, and
flags one open question (cardinality on multi-project repos) at the end.

## Item 1 — Event shape and naming

### Proposed name: `BatchChecked`

Rejected alternatives:

- `ScanCompleted` — suggests the event only fires after the boot scan.
  BuildPlugin's test-only-changes branch is a *batch* triggered by
  `processBatch` after a debounce window; calling that "scan completed" is
  inaccurate and would mislead a future reader who tries to subscribe and
  finds the event firing under in-session file edits.
- `ProjectChecked` — the daemon's actual unit of work isn't one project; it's
  one tier in the project graph (parallel layer of mutually-independent
  projects), and the unit BuildPlugin/TestPrune care about is "all files for
  this trigger have been checked." Per-project granularity would force
  TestPrune to fold N project events back into one trigger, recreating the
  `WaitingForFcsPhase` pattern at a different layer.
- `ScanCompleted` for boot + a separate `BatchCompleted` for in-session —
  two events doubles the subscription surface and forces every consumer to
  match both. The two situations are structurally identical (a known
  cohort of files was scheduled for `pipeline.CheckFile`; all completions
  arrived); one event with a payload that distinguishes the *trigger* is
  cleaner than two parallel events that mean the same thing.

### Payload

```fsharp
type BatchCheckedTrigger =
    /// The boot-scan cohort over every registered file.
    | BootScan
    /// A debounce-batch cohort from processBatch — typically a small set
    /// after a save. `Originating` is what the watcher reported.
    | InSessionBatch of originating: FileChangeKind list

type BatchChecked =
    { Trigger: BatchCheckedTrigger
      /// Files actually dispatched into pipeline.CheckFile for this batch.
      /// May be smaller than the project graph (in-session batch) or equal
      /// to it (boot scan).
      Files: AbsFilePath list
      /// Monotonic generation counter — same as Daemon.GetScanGeneration
      /// for BootScan-triggered events; bumped per InSessionBatch as well
      /// so subscribers can identify "the latest cohort."
      Generation: int64
      /// Wall-clock start of the cohort (first CheckFile dispatched).
      StartedAt: DateTime
      /// Wall-clock end (last FileChecked emitted before this BatchChecked).
      CompletedAt: DateTime }
```

The payload deliberately does **not** include per-file results. TestPrune
already accumulates per-file analysis from the `FileChecked` stream into
agent-local `state.PendingAnalysis`; what it needs from the new event is a
*signal* that "the cohort is done; flush and decide." BuildPlugin similarly
only needs the signal plus the file set (to verify all queued files
actually checked).

Keeping the payload signal-shaped (not result-shaped) avoids the temptation
to rebuild plugin state inside the framework — every plugin's per-file fold
stays in the plugin.

### Cardinality

**Decision: one `BatchChecked` per cohort dispatched into `pipeline.CheckFile`.**

A *cohort* is the set of files a single caller passed through the check
pipeline before yielding control:

- `performScan` (`Daemon.fs:891`) → one cohort = every registered file across
  all tiers. One `BatchChecked` fires when the last tier's `Async.Parallel`
  completes (`Daemon.fs:960`).
- `processBatch` (`Daemon.fs:407`) source-files branch → one cohort = the
  computed `allFilesToCheck` (source + dependent transitive). One
  `BatchChecked` fires after the final `emitResults` in the
  uncovered-file pass (`Daemon.fs:567`).
- Project-rediscovery scans triggered from `processBatch` already replay
  through the same `pipeline.CheckFile` path; they collapse into the same
  cohort as the source-file batch they share `processBatch` with. No
  separate event needed.

**Why not per-tier?** The tier loop (`Daemon.fs:937–971`) is an internal
parallelization detail. Plugins don't currently know about tiers and have
no reason to start. Firing per-tier multiplies events for no consumer
benefit and forces TestPrune to dedup ("did I already flush for this scan?").

**Why not per-project?** Same reason in different clothing. The
project-dependency tier graph is a daemon implementation detail; per-project
granularity would force every consumer to re-aggregate by hand. The cohort
shape is what `processBatch` already commits to as a unit, so it's the
natural granularity.

**Why one event for boot scan + in-session batch?** They flow through the
same `pipeline.CheckFile` API, produce the same `FileChecked` cohort
shape, and the two consumers (TestPrune, BuildPlugin) want the same
"flush and decide" semantics regardless of trigger. The `Trigger`
discriminator in the payload lets a future plugin distinguish if needed
(e.g. for warm-up logic specific to boot scan).

## Item 2 — When it fires

### Boot scan

In `performScan` (`Daemon.fs:891`), after the tier loop completes
(`Daemon.fs:973` "Checked %d files (%d tiers), skipped %d") and before
`scanSignal.SignalGeneration` (`Daemon.fs:978`). Emitting *before*
`SignalGeneration` lets `WaitForScanGeneration` callers (IPC) safely
assume `BatchChecked` has already been processed by the time
`fshw scan --wait` returns.

### In-session batches

In `processBatch` (`Daemon.fs:407`), after the second `emitResults` call
for uncovered files (`Daemon.fs:567`), before `processBatch` returns the
new suppressed set. The `Files` payload is the union of `allFilesToCheck`
and `uncovered`. Empty cohorts (e.g. solution-file change with no source
edits, or all source files filtered as content-unchanged) **do not fire
`BatchChecked`** — there's nothing to "flush and decide" against.

### Relationship to `BuildCompleted`

`BuildCompleted` is a separate concern owned by BuildPlugin. It signals
"the build artifact is up to date." `BatchChecked` signals "FCS finished
its pass over this cohort." They are independent:

- After a *non-test* source change, BuildPlugin runs `dotnet build`
  (no FCS dependency on its part) and emits `BuildCompleted` from
  the build process exit. `BatchChecked` fires when FCS finishes its
  parallel check of the same cohort. Either can land first; subscribers
  treat them independently.
- After a *test-only* source change, BuildPlugin's new behavior is to
  wait for the matching `BatchChecked` and then emit `BuildSucceeded`
  directly (no `dotnet build` invocation). `BatchChecked` strictly
  precedes the `BuildCompleted` emission in this case.
- After a project-file change, BuildPlugin runs a fresh build;
  `BatchChecked` fires after FCS re-checks the rediscovered project
  graph. Order between the two depends on which finishes first.

`BatchChecked` does **not** replace `BuildCompleted`. `BuildCompleted`
carries build-result semantics that FCS doesn't speak (artifact freshness,
CSC errors, MSBuild diagnostics). Keep both.

## Item 3 — Plugin subscription mapping

| Plugin | Today | After |
|---|---|---|
| TestPrune | `SubscribeFileChecked` (symbol-diff fold) + `SubscribeBuildCompleted` (run trigger) | `SubscribeBatchChecked` (symbol-diff fold + run trigger) + `SubscribeBuildCompleted` (run trigger). FileChecked dropped. |
| BuildPlugin | `SubscribeFileChanged` + `SubscribeFileChecked` (drain WaitingForFcsPhase) | `SubscribeFileChanged` + `SubscribeBatchChecked` (replace WaitingForFcsPhase with one match arm) |
| Lint | `SubscribeFileChecked` | unchanged — work *is* per-file |
| Analyzers | `SubscribeFileChecked` | unchanged — work *is* per-file |
| FormatCheck | `SubscribeFileChecked` | unchanged |
| FileCommand | `SubscribeFileChanged` + test-result events | unchanged |
| Coverage | `SubscribeTestRunCompleted` | unchanged |

### TestPrune migration shape

```fsharp
// Before (FileChecked path, today):
| FileChecked result -> // analyze single file, fold into ChangedSymbols
| BuildCompleted BuildSucceeded -> // flushAndQueryAffected, RunExclusive

// After:
| BatchChecked batch ->
    // Iterate batch.Files; analyze each; fold into ChangedSymbols.
    // changedSymbolsRef is updated once at end; cache key is correct
    // for the *next* event (BuildCompleted).
| BuildCompleted BuildSucceeded -> // unchanged
```

The single-event handler analyzes the whole cohort in one pass. The cache
key for `BuildCompleted` (computed from `changedSymbolsRef`) is set
*synchronously* before any subsequent dispatch can race in. No
`RequireWarmStart` flag needed — there's no half-formed-key window to
guard.

### BuildPlugin migration shape

```fsharp
// Before:
| FileChanged(SourceChanged files), IdlePhase _ when allTestFiles ->
    state with Phase = WaitingForFcsPhase(Set.ofList files, idle)
| FileChecked result, WaitingForFcsPhase(awaiting, idle) ->
    let remaining = Set.remove result.File awaiting
    if remaining.IsEmpty then EmitBuildCompleted; ReportStatus Completed
    else state with Phase = WaitingForFcsPhase(remaining, idle)

// After:
| FileChanged(SourceChanged files), IdlePhase _ when allTestFiles ->
    state with Phase = WaitingForBatchPhase(expecting = Set.ofList files, idle)
| BatchChecked batch, WaitingForBatchPhase(expecting, idle) when
    Set.isSubset expecting (Set.ofList batch.Files) ->
    EmitBuildCompleted BuildSucceeded; ReportStatus Completed
    state with Phase = IdlePhase(idle, [])
```

The `Set.remove` per `FileChecked` collapses into one `Set.isSubset` per
`BatchChecked`. The "merge late-arriving SourceChanged into awaiting" branch
(`BuildPlugin.fs:579–588`) needs a corresponding mid-batch handling
strategy — see Item 7's open question on multi-batch races.

## Item 4 — Cache-key implications

### TestPrune's BatchChecked event

The new `BatchChecked` event itself is *not* a cacheable trigger for
TestPrune — TestPrune doesn't emit anything in response to BatchChecked
beyond updating internal state. So `CacheKey` returns `None` for
`BatchChecked` (mirroring the current `FileChecked` no-cache path —
`TestPrunePlugin.fs:1352–1364` returns Some, but it caches per-file
*analysis* output, not run output; under BatchChecked we'd consolidate
this into one cache write at end of cohort, see below).

The cacheable trigger remains `BuildCompleted`. Its cache key keeps the
current shape (`TestPrunePlugin.fs:1320–1332`):

```
plugin-version | event=BuildCompleted | changed-symbols=hash | build-outcome
```

The change: `changedSymbolsRef` is now *guaranteed* fully populated at
the moment of `BuildCompleted` dispatch, because `BatchChecked` strictly
precedes it for any change that touched FCS. (For a `BuildCompleted`
that *doesn't* follow a code change — e.g. forced rebuild — the empty
symbol set is still correct: nothing changed → no tests need rerun →
cache hit on the empty-symbols entry, which is the desired cold-boot
behavior.)

### BuildPlugin's cache key

Unchanged in shape (`BuildPlugin.fs:631–643`). The `BuildInputsHasher`
merkle was always computable from disk state alone — its issue was
`hasBuiltInSessionRef`/`RequireWarmStart`, which existed because the
boot dispatch (`FileChanged`) might fire *before* the project graph
was loaded enough for `BuildInputsHasher` to compute a meaningful
merkle. Under the new world, `BatchChecked` fires *after* the boot
scan, so the project graph is fully populated. BuildPlugin's
`FileChanged` handler can keep its current cache shape; the
`RequireWarmStart` field deletes.

### Cold-start cache hit conditions

For a daemon restart with no on-disk changes since the prior session:

1. Daemon starts, loads project graph, FCS warm-up.
2. `performScan` runs; `BatchChecked { Trigger = BootScan }` fires after
   FCS check.
3. TestPrune's BatchChecked handler analyzes every file's symbols against
   the on-disk symbol DB; identical → `ChangedSymbols = []`.
4. **No subsequent event triggers a TestPrune cache check** — TestPrune
   is now subscribed only to BatchChecked + BuildCompleted, and
   BuildCompleted didn't fire (no source change).
5. Result: tests don't run on cold start (correct — nothing changed),
   and they don't run *spuriously* either. The "RequireWarmStart forces
   one real run" symptom disappears.

If a file *did* change while the daemon was down:

1. Same as above, but step 3 finds symbol diffs → `ChangedSymbols ≠ []`.
2. BuildPlugin runs `dotnet build` (project-fingerprint mismatch or
   `BuildInputsHasher` merkle mismatch) → `BuildCompleted BuildSucceeded`.
3. TestPrune's BuildCompleted handler computes cache key from
   `changedSymbols + outcome`; if the same change was already tested
   in a prior session, cache hits and re-emits the cached
   `TestRunStarted`/`TestRunCompleted`. If new, runs tests.

Both branches are correct without `RequireWarmStart`.

### Confirmation: `RequireWarmStart` is deletable

Yes — for both consumers (TestPrune and BuildPlugin), the workaround
exists *only* to paper over "first dispatch happens before cache key is
meaningful." Under `BatchChecked`, the cache-keyed events
(`BuildCompleted`, future TestPrune triggers) all fire after the cohort
is complete and state is populated. The flag has no remaining consumer.

Migration sequence (Item 5) deletes the field from `PluginHandler` and
the `tryReplayCache` gate (`PluginFramework.fs:282–294`) at the end.

## Item 5 — Migration sequence

Five commits, in order. Each commit is independently mergeable.

1. **`framework: add BatchChecked event type and dispatcher`** —
   - `Events.fs`: add `BatchCheckedTrigger`, `BatchChecked`, extend
     `PluginEvent<'Msg>` with `BatchChecked of BatchChecked`.
   - `PluginFramework.fs`: add `SubscribeBatchChecked`,
     `DispatchBatchChecked`, dispatch wiring.
   - `Daemon.fs`: emit `BatchChecked { Trigger = BootScan; ... }` at end
     of `performScan` (before `SignalGeneration`); emit
     `BatchChecked { Trigger = InSessionBatch _; ... }` at end of
     `processBatch` source-files path.
   - **No plugin migrated yet.** Additive only. Existing plugins ignore
     the new event because their subscription set doesn't include it.
   - Test: framework-level — `BatchChecked` fires after the last
     `FileChecked` in a synthetic batch.

2. **`testprune: subscribe to BatchChecked, drop per-FileChecked path`** —
   - Move the `FileChecked` analysis fold into a `BatchChecked` handler
     that iterates `batch.Files` and runs the same per-file analysis.
   - Remove `SubscribeFileChecked` from `Subscriptions`.
   - Tests: existing TestPrune tests should pass with the new event
     (assuming test harness fires `BatchChecked` after batches of
     `FileChecked`). Add: cold-boot with no on-disk change → no test
     execution.

3. **`buildplugin: replace WaitingForFcsPhase with WaitingForBatchPhase`** —
   - Rename phase; collapse the two `FileChecked, WaitingForFcsPhase`
     match arms into one `BatchChecked, WaitingForBatchPhase`.
   - Subscribe to `SubscribeBatchChecked` instead of `SubscribeFileChecked`.
   - Tests: existing BuildPlugin tests should pass; verify the test-only
     branch (`BuildPluginTests` — `Skip rebuild when only test files
     change`) still emits `BuildSucceeded` after `BatchChecked`.

4. **`framework: delete RequireWarmStart`** —
   - Remove field from `PluginHandler<'State, 'Msg>`.
   - Remove `hasCompletedThisSession` flag and gate from `tryReplayCache`
     (`PluginFramework.fs:282–294`).
   - Remove `RequireWarmStart = true` from BuildPlugin
     (`BuildPlugin.fs:647`) and TestPrunePlugin
     (`TestPrunePlugin.fs:1368`).
   - Tests: regression — cold boot of a previously-tested project hits
     the cache for both Build and TestPrune (no spurious first run).

5. **`docs: ADR-002 BatchChecked event + RequireWarmStart removal`** —
   record the design decision; supersede the implicit "RequireWarmStart
   is the cold-start contract" note in `PluginFramework.fs:121–123`.

The smallest viable first slice is commit 1 alone. It ships the event
without any consumer. That's a useful checkpoint: framework tests prove
the event fires correctly before any plugin is restructured. Commits 2
and 3 are independent and could land in either order; commit 4 must
follow both.

## Item 6 — TDD/test strategy

### Framework-level

- `BatchChecked` fires once per cohort; the `Files` payload equals the
  set of files actually dispatched.
- `BatchChecked.Generation` matches `Daemon.GetScanGeneration` for
  BootScan-triggered events.
- An empty cohort (no source changes after dedup) does **not** fire
  `BatchChecked`.
- `BatchChecked` fires *after* the last `FileChecked` for the cohort
  (ordering test — subscribe to both, verify ordering by sequence
  number).

### TestPrunePlugin

- Cold start, project unchanged from prior session: no test execution
  (cache hit). This is the regression that kills `RequireWarmStart`.
- Cold start, one source file changed: TestPrune's `BatchChecked`
  handler folds the changed symbol; subsequent `BuildCompleted` triggers
  affected-tests run.
- Mid-session: edit a source file → debounce → `BatchChecked` →
  `BuildCompleted` → tests run with correct affected set.

### BuildPlugin

- Test-only-changes branch: edit N test files →
  `WaitingForBatchPhase(expecting=N)` → `BatchChecked` → `BuildSucceeded`
  emitted.
- Late-arriving `SourceChanged` mid-wait merges into expected set
  without firing `BuildSucceeded` early (see Item 7).

### Edge cases

- **Scan-while-batch-in-flight.** The `Generation` field disambiguates;
  subscribers that need to dedup can use it.
- **Cancellation mid-cohort.** If `performScan` is cancelled, no
  `BatchChecked` fires (the loop exits early without reaching the emit
  point). Consumers stay in their pre-batch state.
- **Project-file changes.** `processBatch`'s rediscover branch
  (`Daemon.fs:467–488`) replays through the same `pipeline.CheckFile`
  path *within* the source-files branch that follows; one `BatchChecked`
  fires for the combined cohort. (If `processBatch` ever splits these
  into two cohorts, two events would fire and TestPrune would need
  dedup — currently they share `processBatch`.)
- **Multi-project tier scans.** Decision: one `BatchChecked` after the
  last tier completes (covered in Item 1 cardinality). Per-project
  events are out of scope.

## Item 7 — Risks and open questions

### Risk: event ordering

Adding `BatchChecked` between the last `FileChecked` of a cohort and
any subsequent event (e.g. `BuildCompleted`) introduces a new ordering
constraint. Plugins that today assume "after FileChecked, BuildCompleted
arrives next" must continue to work — `BuildCompleted` arrival is not
delayed by `BatchChecked`'s emission, only preceded by it. No
synchronous wait is introduced.

### Risk: dispatch-loop ordering

The framework dispatches events to plugin agents via `agent.Post`, which
is fire-and-forget. Two plugins both subscribed to `BatchChecked` may
process it in arbitrary order. This matches today's `FileChecked`
ordering — no new hazard.

### Open question: late-arriving SourceChanged in BuildPlugin

The current `WaitingForFcsPhase` has a branch
(`BuildPlugin.fs:579–588`) that merges late-arriving `SourceChanged`
into the awaiting set. Under `BatchChecked`, two scenarios:

1. The late `SourceChanged` arrives while we're still waiting for the
   *first* batch's `BatchChecked`. The watcher will eventually trigger
   a *second* `processBatch`, producing a second `BatchChecked` whose
   `Files` payload covers the late files. BuildPlugin's
   `WaitingForBatchPhase` handler needs to track expecting across
   *batch boundaries* if the late `SourceChanged` is only test files.
2. The late `SourceChanged` includes non-test files. Same as today:
   fall through to `handleSourceChanged`, which starts a real build.

Recommendation: in commit 3, model `WaitingForBatchPhase` as
`expecting: Set<AbsFilePath> * idle: Lifecycle`, and on `BatchChecked`
check `Set.isSubset expecting batchFiles`. If not a subset (the cohort
covered files we weren't waiting for, or didn't cover what we expected),
*don't* emit `BuildSucceeded` yet — trust that the late files will
arrive in a subsequent `BatchChecked`. This preserves the today's
"merge into awaiting" semantics without having to track partial
progress.

### Open question: Custom(Started)/Custom(Resumed) interaction

Daemon resume (re-attaching to a still-running `fshw start` daemon)
doesn't currently fire any events for the resume itself — IPC just
queries current state. Adding `BatchChecked` doesn't change resume
semantics; if the resumed daemon is mid-scan, `BatchChecked` will fire
when that scan completes regardless of whether the consumer attached
mid-scan or post-scan. No special handling needed.

### Open question: per-project events for very large monorepos

For a hypothetical monorepo with 100+ projects where a single edit
triggers transitive checks across most of the graph, "one `BatchChecked`
per cohort" may be coarser than ideal — a TestPrune that wanted to run
tests project-by-project as their dependencies clear would prefer
per-project events. **Lean: don't design for it now.** The TestPrune
fold is over symbol diffs, not per-project; a per-project event would
fragment that fold without obvious benefit. If a future plugin (e.g.
incremental coverage merge) wants per-project granularity, add a
*second* event (`ProjectChecked`) at that point — `BatchChecked` and
`ProjectChecked` would coexist, with most plugins subscribing to the
former. Don't preemptively split.

## Questions for human

1. **Event name.** I picked `BatchChecked` over `ScanCompleted` because
   `processBatch` covers in-session edits as well as boot scans. If
   you prefer a name that reads more naturally for the boot case
   (`ScanCompleted` is more familiar, `CohortChecked` is more
   precise but ugly), say so before commit 1 lands.
2. **Cardinality on rediscover-then-source mixed batches.**
   `processBatch` runs rediscovery and source-file checks in a single
   batch; today both contribute to one cohort. If a future refactor
   splits them into two `pipeline.CheckFile` waves, do you want one
   `BatchChecked` per wave or one per `processBatch` invocation? My
   inclination: per `pipeline.CheckFile` wave, with the `Trigger`
   payload distinguishing them. If you want one-per-`processBatch`,
   the framework needs an explicit "begin batch / end batch" bracket
   in `processBatch`, which is fine but more invasive.
3. **`BatchChecked.Files` payload size.** Boot scan cohorts can be
   thousands of files; the payload allocates one list. If allocation
   pressure is a concern (it shouldn't be — these events fire on
   debounce/scan timescales, not per-keystroke), consider passing
   the cohort as a `Set<AbsFilePath>` or a closure-deferred lookup.
   Default: `AbsFilePath list`.
4. **Should the test-only-changes branch in BuildPlugin survive the
   migration at all?** It exists today as an FCS-driven optimization
   ("don't run dotnet build for pure test edits"). With `BatchChecked`
   replacing `WaitingForFcsPhase`, the optimization is cleaner, but
   it's also a candidate for "delete the optimization entirely; let
   the real build short-circuit through MSBuild's incremental check."
   Out of scope here, but worth flagging — the migration could
   alternatively be "delete the test-only branch, then BuildPlugin
   doesn't need `BatchChecked` at all." That would shrink commit 3
   to deletion-only.
