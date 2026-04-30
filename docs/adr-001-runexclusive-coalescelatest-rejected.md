# ADR-001: `RunExclusive.CoalesceLatest` rejected; removed from framework

Status: Accepted (2026-04-30)

## Context

Phase 2 introduced the `RunExclusive` primitive in `PluginFramework` to take
ownership of "single-flight + coalesce trailing trigger" coordination away from
individual plugins. The original design exposed two policies:

```fsharp
type CoalescePolicy = Drop | CoalesceLatest
```

`Drop`: while a key is running, additional calls are ignored.
`CoalesceLatest`: while a key is running, the new call's `Async<'Msg>` replaces
any prior follower; one follower runs after the current call finishes.

`Drop` was a clean fit for `BuildPlugin` (replaces `RunningPhase`).
`CoalesceLatest` was intended for `TestPrunePlugin`, replacing the
`TestsRunning(running, RerunQueued)` state machine.

## Decision

`CoalesceLatest` was tried for TestPrunePlugin (Phase 3 spike, abandoned).
The migration produced a **silent correctness regression**, not just an
awkward shape. Subsequently `CoalesceLatest` was removed from the framework
entirely — the only would-be consumer rejected it, and BuildPlugin's `Drop`
usage worked fine without a policy parameter at all.

`RunExclusive` is now `string -> Async<'Msg> -> unit` (always-Drop).

## The race that killed CoalesceLatest

`CoalesceLatest`'s contract is that the follower closure is **captured at
queue time**, not at run time. TestPrunePlugin's `RerunQueued` semantics
require run-time state.

Concrete trace (against TestPrunePlugin code as of `nskunyxz`):

| Time | Event | Today's behavior | Under `CoalesceLatest` |
|------|-------|------------------|------------------------|
| t0 | `BuildCompleted #1` | `flushAndQueryAffected` snapshots `ChangedSymbols = X`; run #1 starts with `f(X)` | same |
| t1 | `FileChecked` events | State accumulates `ChangedSymbols = X ∪ Y` | same |
| t2 | `BuildCompleted #2` (mid-run #1) | Set `TestPhase = TestsRunning(running, RerunQueued)`. **No closure built; no state captured.** | Build `Async<TestPruneMsg>` *now*, capturing `f(X ∪ Y)` |
| t3 | More `FileChecked` events | `ChangedSymbols = X ∪ Y ∪ Z` (Z arrives *between* t2 and t4) | same |
| t4 | `Custom(TestsFinished)` for run #1 | `RerunQueued` branch calls `flushAndQueryAffected` against *current* state. Rerun tests `f(X ∪ Y ∪ Z)`. **Z is included.** | Framework starts queued-at-t2 closure. Rerun tests `f(X ∪ Y)`. **Z is silently dropped.** |

Whether Z gets re-tested ever depends on whether `BuildCompleted #3` fires for
the file that introduced Z. Under FSEvent storms with `BuildPlugin`'s own
`Drop` policy, that's not guaranteed. The current `RerunQueued` semantics
prevent this entire class of dropped-test failure by re-snapshotting at the
latest possible moment (run time, not queue time).

## Why the closure couldn't snapshot at run time

The closure body needs:

- `state.PendingAnalysis` — per-file analysis records flushed via
  `db.RebuildProjects`.
- `state.ChangedSymbols` — input to `db.QueryAffectedTests`.
- `state.SymbolSnapshot` — warm-start fall-back behavior.

These live in the agent's `state` record. The agent loop is the single owner;
`RunExclusive`'s body executes off-thread. Run-time access to `state` would
require either:

1. **A side channel** — mutable `volatile` references for every field the
   body touches. TestPrunePlugin already has one such ref (`changedSymbolsRef`)
   that the code itself flags as a workaround. Extending the workaround to
   `PendingAnalysis` and `SymbolSnapshot` is exactly the kind of "threading
   state through a side channel" that Phase 2 set out to eliminate.
2. **Self-posting** — have the agent loop initiate `RunExclusive` from inside
   a `Custom(StartRun)` handler. Then the agent already owns the rerun-trigger
   state machine; `CoalesceLatest`'s queue mechanism is redundant. Each
   `Custom(StartRun)` arrives sequentially in the inbox, and `RunExclusive`
   always sees `IsRunning("tests") == false` at its call site. At that point
   `CoalesceLatest` adds nothing over `Drop`.

Both paths reduce `CoalesceLatest` to either "a workaround the framework
shouldn't endorse" or "a feature with no observable difference from `Drop`."

## What replaced the spike

Phase 3 alt shipped a narrower migration that *does* fit:

- `ctx.RunExclusive "tests" (...)` — framework owns single-flight (Drop only).
- `RerunIntent = NoRerun | RerunQueued` → `PendingRerun: bool` field on
  `TestPruneState`.
- `BuildCompleted` while running sets the flag without calling `RunExclusive`.
- `Custom(TestsFinished)` checks the flag and conditionally calls
  `flushAndQueryAffected` + `RunExclusive` for the rerun. **State read at
  run time, not queue time** — preserves the t1-t4 race fix.
- IPC `TestsRunning _` matches replaced with `ctx.IsRunning "tests"`.

Net: ~17 fewer lines in `TestPrunePlugin.fs`; framework gains one bool field
on plugin state in exchange for losing the entire `TestsRunning` discriminant.

## Consequences

- `RunExclusive`'s API is simpler: `string -> Async<'Msg> -> unit` instead of
  `string -> CoalescePolicy -> Async<'Msg> -> unit`. ~25 lines removed from
  `PluginFramework`.
- The "framework owns single-flight, plugin owns rerun-trigger flag" pattern
  is now the canonical idiom for any plugin that needs trailing-rerun
  semantics. Future plugins should follow TestPrunePlugin's shape, not
  `CoalesceLatest`.
- If a plugin ever genuinely needs queue-time-capture semantics (none in line
  today), reintroducing `CoalesceLatest` is ~25 lines and this ADR documents
  why we held the line.

## Related

- Phase 3 alt commit: `qqsxmxwm testprune: migrate to RunExclusive +
  PendingRerun flag`.
- `CoalesceLatest` removal commit: `qzlylzxy framework: remove CoalesceLatest
  policy from RunExclusive`.
