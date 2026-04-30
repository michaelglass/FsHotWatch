# Framework tightening: cold-start gating + single-flight primitive

Date: 2026-04-30

## Context

After a root-cause cleanup pass, surveyed FsHotWatch for belts-and-suspenders
patterns where defensive code at one layer guards an invariant a deeper layer
could enforce. Eight candidates emerged; this design covers the two highest-
value structural items. The other six are scoped for a follow-up reassessment.

## Goals

Move two pieces of per-plugin defensive state into `PluginFramework` so the
framework owns the invariant and plugins shrink to pure update functions:

1. **Cold-start cache invalidation** — currently each plugin carries a session
   "have I run yet?" flag and consults it inside its `CacheKey` function.
2. **Single-flight execution** — currently each plugin reinvents a "running
   phase" state machine to coordinate `Async.Start`-ed work and coalesce or
   drop overlapping triggers.

Non-goals: items #3–#8 from the survey (dedup layers, `ScanAgent` volatile
fields, broad `try/with`, path normalization, `SolutionChanged` asymmetry,
TestPrune query overlap). These are reassessed after Phase 3.

## Item 1 — `RequireWarmStart` field

### Current shape

`BuildPlugin.fs` declares `let mutable hasBuiltInSessionRef = false` (line 165),
sets it to `true` after the first build finishes (lines 250, 328, 422), and
gates its `CacheKey` function on it (line 643): when the flag is false, return
`None` so the framework skips replay and runs the plugin. Same pattern in
`TestPrunePlugin.fs` (`hadPriorResultsRef` at lines 737, 1188, 1362).

### Change

Add to `PluginHandler<'State, 'Msg>`:

```fsharp
/// When true, the framework skips cache replay until this plugin has
/// reached a terminal state (Completed or Failed) at least once this
/// session. Use for plugins whose cached output may not reflect current
/// disk state across daemon restarts.
RequireWarmStart: bool
```

`registerHandler` maintains a closure-local `mutable hasCompletedThisSession =
false`. The existing `runAndCache` already observes terminal status via
`capturedStatus` — extend that branch to flip the flag. `tryReplayCache` returns
`false` when `RequireWarmStart && not hasCompletedThisSession`, regardless of
cache hit.

Plugins delete their flag, the `Volatile.Write` at completion, and the
`CacheKey` short-circuit. The `CacheKey` function returns to its straightforward
"compute the input hash" shape.

### Why this is correct

The flag's semantics are observable at the framework boundary: terminal status
already flows through `capturingCtx.ReportStatus`. Moving ownership doesn't
change behavior — it removes a parallel implementation in each plugin.

### Risk

Low. Mechanical change. Test surface: cold-start integration tests must still
observe a real run on first daemon boot; warm-restart scenarios (currently
broken anyway because cache replay would otherwise skip the run) must still
trigger a real run.

## Item 2 — `RunExclusive` primitive

### Current shape

**BuildPlugin** uses a `RunningPhase of Lifecycle<...>` state. On `FileChanged`
during `RunningPhase`, the handler returns the existing state (drop). Work is
launched via `Async.Start` inside `Update` and posts back via `Custom(BuildDone
...)`.

**TestPrunePlugin** uses `TestsRunning of Lifecycle<...> * RerunIntent` where
`RerunIntent = NoRerun | RerunQueued`. On a trigger during `TestsRunning`, sets
`RerunQueued`. After current run finishes, if `RerunQueued`, kicks another via
`Async.Start`. IPC commands (`affected-tests`, etc.) match on `TestsRunning _`
to return "already running" replies.

### Change

Add to `PluginCtx<'Msg>`:

```fsharp
/// Run `work` exclusively under `key`. While running, additional calls
/// with the same key are governed by `policy`:
///   - Drop: the new call is ignored.
///   - CoalesceLatest: the new `work` replaces any previously-stashed
///     follower; one follower runs after the current call finishes.
/// On completion, the framework posts the returned 'Msg back to the
/// agent's mailbox.
RunExclusive: key:string -> policy:CoalescePolicy -> work:Async<'Msg> -> unit

/// Whether `key` is currently running under RunExclusive. Plugins use this
/// for IPC-facing status without maintaining their own "is running" bit.
IsRunning: key:string -> bool
```

```fsharp
type CoalescePolicy = Drop | CoalesceLatest
```

Framework state: a per-handler `Dictionary<string, RunSlot>` where `RunSlot`
holds the in-flight `Async` and an optional pending follower. The framework
also calls `inbox.Post(Choice1Of2(Custom completionMsg))` when the work
finishes, restoring the existing pattern of completions arriving as ordinary
messages.

### Migration plan

1. **BuildPlugin** uses `Drop`. Delete `RunningPhase` from the state union; the
   plugin becomes a record of build outputs and the most recent `BuildOutcome`.
   `FileChanged` always calls `ctx.RunExclusive "build" Drop (build ...)`. The
   framework drops the call if a build is already running. IPC `build-status`
   reads `ctx.IsRunning "build"`.

2. **TestPrunePlugin** uses `CoalesceLatest`. Delete `TestsRunning` from the
   state union. Remaining state is the impact-analysis data only. The
   `RerunIntent` semantics — that the *next* run sees the *latest* trigger
   context — are preserved by `CoalesceLatest`'s "replace previous follower"
   behavior.

### Open uncertainty

TestPrunePlugin's `RerunIntent` carries trigger-specific context that may not
fit cleanly into a `Async<'Msg>` thunk. The plan explicitly migrates BuildPlugin
first (simpler, pure-`Drop` semantics), evaluates the API in real use, then
attempts TestPrunePlugin. If TestPrunePlugin's migration produces an awkward
shape, we revert that migration, document the reason, and accept a partial win.

### Risk

Medium. Migration of BuildPlugin is contained — pure `Drop` semantics map
cleanly. TestPrunePlugin migration is the experiment; abort path is to keep its
bespoke state machine.

## Plan

### Phase 1: ship `RequireWarmStart`

1a. Add `RequireWarmStart: bool` to `PluginHandler`. Update `registerHandler` to
    track per-handler `hasCompletedThisSession`. Gate `tryReplayCache`. TDD: a
    framework-level test that opts in and verifies first event runs Update,
    second event hits cache.

1b. Migrate `BuildPlugin`: set `RequireWarmStart = true`, delete
    `hasBuiltInSessionRef` and its three `Volatile.Write` sites, simplify
    `CacheKey`. Verify with existing integration tests that cold-start still
    runs a real build.

1c. Migrate `TestPrunePlugin` analogously. Delete `hadPriorResultsRef`.

1d. Run full test suite; verify cold-start integration tests still pass.

### Phase 2: ship `RunExclusive` + BuildPlugin migration

2a. Add `RunExclusive`, `IsRunning`, `CoalescePolicy` to `PluginCtx` and the
    framework. TDD: framework-level tests for Drop and CoalesceLatest semantics
    (drop while running; coalesce replaces follower; completion posts back).

2b. Migrate BuildPlugin to `RunExclusive "build" Drop`. Delete `RunningPhase`
    from the state union. Update IPC handlers to use `ctx.IsRunning "build"`.

2c. Verify: build-during-build is dropped; build-after-build runs. Existing
    integration tests pass.

### Phase 3: attempt TestPrunePlugin migration

3a. Try `RunExclusive "tests" CoalesceLatest` with the latest test-trigger
    closure threaded into the `Async<'Msg>`. Delete `TestsRunning` if it works
    cleanly.

3b. If the shape is awkward (RerunIntent doesn't compose, IPC contracts break,
    or the framework primitive needs a bespoke escape hatch), revert,
    document the friction in the design doc as a follow-up note, and proceed
    to Phase 4 with TestPrunePlugin's bespoke state machine intact.

### Phase 4: reassess remaining six items

After Phases 1–3 land, re-survey items #3–#8 (file-change dedup layers,
`ScanAgent` volatile + bootstrap ref, broad `try/with` swallows, path
normalization through typed wrappers, `SolutionChanged` asymmetry, TestPrune's
duplicate `QueryAffectedTests`). The set may shrink (some no longer matter
after framework changes) or grow (new patterns may surface during migration).

## Testing strategy

- TDD: every framework change starts with a failing unit test in
  `tests/FsHotWatch.Tests`.
- Per-plugin migrations rely on existing integration coverage in
  `tests/FsHotWatch.IntegrationTests` for end-to-end behavior; supplement with
  unit tests where a behavior was previously implicit.
- Coverage ratchet (per `CLAUDE.md`) auto-corrects upward via `mise check`;
  watch for the lucky-ceiling trap on new framework code.

## VCS

Per project memory: jj-based, push directly to main, never rebase. Each phase
is a separate commit at minimum; sub-steps within a phase may be separate
commits if the diff is large.

## TestPrunePlugin migration: deferred

Date: 2026-04-30
Spike workspace: `.workspaces/phase-3` (commit `nskunyxz`, parent `ovmxktvn`).
Time spent before abort: ~30 minutes (well under the 2-hour budget).

### Decision

Aborted per Phase 3.b — the explicit "abort if `RerunIntent` doesn't compose
into the `Async<'Msg>` closure" criterion fired. Spike commits abandoned;
TestPrunePlugin's `TestPhase = TestsRunning of Lifecycle * RerunIntent` state
machine stays.

### Friction

`CoalesceLatest`'s contract: "stash the latest follower closure; when the
in-flight call finishes, run the stashed closure." The follower closure is
**captured at queue time**, not run time. This does not preserve TestPrunePlugin's
current `RerunQueued` semantics.

Concrete trace of the divergence:

- t0: `BuildCompleted #1` arrives while idle. State has
  `ChangedSymbols = X`. `flushAndQueryAffected` runs, computing
  `AffectedTests = f(X)`. Run #1 starts.
- t1: `FileChecked` events fire during run #1. State accumulates
  `ChangedSymbols = X ∪ Y`.
- t2: `BuildCompleted #2` arrives mid-run. **Today**
  (`src/FsHotWatch.TestPrune/TestPrunePlugin.fs:1118-1128`):
  the handler just sets `TestPhase = TestsRunning(running, RerunQueued)` —
  it does *not* call `flushAndQueryAffected` or capture state. With
  `CoalesceLatest`: the handler must build an `Async<TestPruneMsg>`
  *now*, capturing `f(X ∪ Y)` into the closure.
- t3: more `FileChecked` events fire. State accumulates
  `ChangedSymbols = X ∪ Y ∪ Z`. (Z arrives **between BuildCompleted #2 and
  TestsFinished #1**.)
- t4: `Custom(TestsFinished)` for run #1. **Today**
  (`TestPrunePlugin.fs:1257-1303`, the `RerunQueued` branch): calls
  `flushAndQueryAffected` against *current* state, which has Z in
  `ChangedSymbols`. The rerun captures `f(X ∪ Y ∪ Z)` and tests Z. With
  `CoalesceLatest`: the framework starts the *previously queued* follower,
  whose closure was built at t2 and still computes `f(X ∪ Y)`. **Z is
  silently dropped from this rerun.**

Whether Z gets tested at all then depends on a subsequent `BuildCompleted #3`
firing — which itself depends on BuildPlugin's own scheduling (its
`WaitingForFcsPhase` drain, FCS completion timing). Under FSEvent storms with
BuildPlugin's own `Drop` policy, BuildCompleted #3 may not fire for the file
that introduced Z, so Z is permanently untested until the user touches the
file again. The current `RerunQueued` semantics prevent this entire class of
dropped-test by re-snapshotting at the latest possible moment.

### Why the closure can't snapshot at run time instead

The closure body needs:

- `state.PendingAnalysis` — per-file analysis records to flush via
  `db.RebuildProjects`. Read at `TestPrunePlugin.fs:586-630`.
- `state.ChangedSymbols` — input to `db.QueryAffectedTests`. Read at
  `:713-720`.
- `state.SymbolSnapshot` for warm-start fall-back behavior.

These live in the agent's `state` record. The agent loop is the single owner;
the framework's `RunExclusive` body executes off-thread on the thread pool.
For the body to read latest state we'd need either:

1. A side channel (mutable `volatile` references for every field the body
   touches). The current code has exactly one such ref —
   `changedSymbolsRef` (`:731`) — and the comment at `:728-731` flags the
   pattern as a workaround. Extending it to `PendingAnalysis: Map<...>` and
   `SymbolSnapshot: Map<...>` is "threading state through a side channel" —
   the explicit abort criterion.
2. Self-post a `Custom(StartRun)` message and have the agent loop initiate
   `RunExclusive` from inside that handler. But then the agent already owns
   the rerun-trigger state machine — `CoalesceLatest`'s queue mechanism is
   redundant, since each `Custom(StartRun)` arrives sequentially in the
   inbox and `RunExclusive` would always see `IsRunning("tests") == false`
   at its call site (the prior run's `Custom(TestsFinished)` was processed
   before it). At that point `CoalesceLatest` adds nothing over `Drop`.

### Workable alternative (not pursued in this spike)

The friction is specifically with `CoalesceLatest`'s queue-time capture. A
narrower migration that *does* fit:

- `ctx.RunExclusive "tests" Drop` — framework owns single-flight, matching
  BuildPlugin.
- Replace `RerunIntent` with a `PendingRerun: bool` flag in
  `TestPruneState`. `BuildCompleted` while running sets the flag without
  calling `RunExclusive`; `Custom(TestsFinished)` checks the flag and
  conditionally calls `flushAndQueryAffected` + `RunExclusive` for the
  rerun. State at run-time, not queue-time.
- IPC `TestsRunning _` matches replaced with `ctx.IsRunning "tests"`.
- Estimated savings: ~40-60 lines (drop `TestRunPhase` discriminant, the
  `TestsRunning` arms, and the inline `Async.Start`); the rerun branch in
  `TestsFinished` shrinks but doesn't disappear.

This alternative is **not** what Phase 3 prescribed (it doesn't use
`CoalesceLatest` at all), so it's left as a follow-up for whoever owns
Phase 4's reassessment. The mental model — "framework owns single-flight,
plugin owns rerun-trigger flag" — is cleaner than the current discriminated
union and worth considering separately from `CoalesceLatest` adoption.

### Status

`TestsRunning of Lifecycle<...> * RerunIntent` stays. No code change
shipped from the spike. Item 8 (duplicate `QueryAffectedTests`, see
`.workspaces/items-568-research/docs/plans/2026-04-30-items-5-6-8-design.md`)
is independent and unaffected by this deferral.
