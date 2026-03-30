# Plugin Status Safety: Prevent WaitForComplete Hangs

## Problem

Plugins report status via `ctx.ReportStatus(Running/Completed/Failed)` as a side effect.
If a plugin goes `Running` and never transitions to a terminal state, `WaitForComplete` hangs
(up to 30-minute timeout). This happened with TestPrune: a `FileChecked` event after
`BuildCompleted` put it back into `Running` with no subsequent `BuildCompleted` to resolve it.

Fixed in the immediate case, but the API still allows this class of bug.

## Plan

### 1. Auto-complete after Update returns (default-safe)

The framework (`registerHandler` in `PluginFramework.fs`) wraps `handler.Update` calls.
After `Update` returns, if the plugin status is `Running`, automatically set it to `Completed`.

Add `ctx.KeepRunning()` for plugins that intentionally stay `Running` after `Update` returns
(e.g., TestPrune launching tests via `Async.Start` on `BuildCompleted`).

```fsharp
// PluginCtx gets a new field:
KeepRunning: unit -> unit

// Framework wrapper (in registerHandler, around line 130):
let mutable keepRunning = false
let ctx = { ... KeepRunning = fun () -> keepRunning <- true ... }

let! nextState = handler.Update ctx state event
if not keepRunning then
    match currentStatus with
    | Running _ -> reportStatus handler.Name (Completed(DateTime.UtcNow))
    | _ -> ()
keepRunning <- false
```

**Call sites that need `ctx.KeepRunning()`:**
- `TestPrunePlugin.fs` — `BuildCompleted` handler when dispatching tests to thread pool
- Possibly `BuildPlugin.fs` if build is async (check — it may already be synchronous in Update)

All other plugins do synchronous work in Update and would benefit from auto-complete.

### 2. Per-plugin Running timeout (safety net)

Add a configurable timeout to the framework: if a plugin stays `Running` for longer than N seconds
without any status change, auto-fail it with a descriptive error.

```fsharp
type PluginHandler<'State, 'Msg> =
    { ...
      RunningTimeout: TimeSpan option  // default: Some (TimeSpan.FromMinutes 5) }
```

Framework starts a timer on `Running`, cancels on any status change. On expiry:
`ReportStatus(Failed("plugin exceeded running timeout of 5m", now))`

This catches bugs regardless of whether `KeepRunning` is used correctly.

### 3. Migration

- Add `KeepRunning` to `PluginCtx` (non-breaking — new field)
- Add auto-complete logic to `registerHandler`
- Audit each plugin, add `ctx.KeepRunning()` where needed
- Add `RunningTimeout` with a default so existing plugins get protection automatically
- Update tests

### Affected plugins

| Plugin | Async work after Update? | Needs `KeepRunning`? |
|--------|--------------------------|----------------------|
| BuildPlugin | No (awaits build in Update) | No |
| TestPrunePlugin | Yes (Async.Start for tests) | Yes, on BuildCompleted |
| CoveragePlugin | No | No |
| LintPlugin | No | No |
| AnalyzersPlugin | No | No |
| FormatCheckPlugin | No | No |
| FileCommandPlugin | No | No |
