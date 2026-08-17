# FsHotWatch

Core library for the FsHotWatch daemon. This package provides the event system,
plugin framework, file watcher, project graph, check pipeline, and IPC server
that all plugins build on top of.

> **Status: early alpha, and a lot of it is AI-written.** APIs shift between
> versions and rough edges are expected — your mileage may vary. Issues and PRs
> are very welcome.

## What it does

FsHotWatch keeps a single FSharpChecker instance warm in the background.
When you save a file, it re-checks just that file (milliseconds instead
of minutes) and dispatches the results to all registered plugins.

You don't use this package directly unless you're building a custom host
or writing your own plugin. Most users should install
[FsHotWatch.Cli](../FsHotWatch.Cli/) instead.

## Key types

| Type | Purpose |
|------|---------|
| `Daemon` | Ties together the warm FSharpChecker, file watcher, plugin host, and IPC server. |
| `PluginHost` | Manages plugin lifecycle, event dispatch, error ledger, and IPC commands. |
| `PluginHandler<'State, 'Msg>` | Declarative plugin definition: name, init state, update function, commands, subscriptions. |
| `PluginCtx<'Msg>` | What plugins receive during Update -- report status, report errors, emit events. |
| `CheckPipeline` | Incremental file checking with the warm FSharpChecker. |
| `ProjectGraph` | Tracks project dependencies and file ownership for transitive invalidation. |
| `FileWatcher` | Debounced file system monitoring (500ms source, 200ms project). |
| `IpcServer` / `IpcClient` | StreamJsonRpc over named pipes for CLI-to-daemon communication. |

## Event pipeline

```
File change -> FormatPreprocessor (rewrites) -> FileChanged
  +-- BuildPlugin -> dotnet build -> BuildCompleted
  |     +-- TestPrunePlugin -> affected tests -> TestCompleted
  +-- FCS Check -> FileChecked
  |     +-- LintPlugin (warm AST + check results)
  |     +-- AnalyzersPlugin (warm check results)
  |     +-- TestPrunePlugin (symbol analysis)
  +-- FileCommandPlugin (matching files -> command)
```

## Writing a plugin

See [Writing a plugin](../../docs/writing-plugins.md) for a
complete example using the declarative `PluginHandler` framework.

### Status visibility contract

Plugins own their status. The framework no longer derives a terminal summary
from the last log line — if you forget to set a summary, the run's recorded
summary is empty.

- At the start of a run, call `ctx.StartSubtask "primary" "<descriptive label>"`.
  This label is what the compact renderer shows while the plugin is running.
- As progress changes, call `ctx.UpdateSubtask "primary" "<new label>"` to
  update the label in place without churning state.
- At the end of the run, call `ctx.EndSubtask "primary"` then report a terminal
  status carrying its verdict — `PluginCtxHelpers.completeWith ctx "<result
  totals>" elapsed` (or `failedWith`). The verdict rides the terminal
  transition, so the run history's summary and the reported status cannot
  disagree; there is no separate summary channel to set (or forget).
- Per-file `ctx.Log` calls remain useful — they populate the verbose activity
  tail without being promoted to a summary.
- **If the run verified nothing, say so.** Build the summary with
  `RunSummary.nothingVerified "<why>"` instead of writing your own totals. It marks
  the summary `NOTHING VERIFIED: …`, and every surface then refuses the run a green:
  compact and verbose render `⚠` instead of `✓`, agent mode tokens it `warn`, and
  `plugins[]` in `.fshw/verdict.json` records `warn` instead of `ok`.

  Report the terminal as `Completed`, not `Failed` — nothing broke, and a failure
  would turn an honest "no verdict" into "failures found".

  This is a marker you must **set**; the framework cannot infer it. A plugin that
  verifies nothing and does not say so still renders a `✓`, so treat "could this run
  have checked zero things?" as a question every plugin owes an answer to.

### Per-event timeouts

All event-driven plugins — including the in-process `LintPlugin`,
`AnalyzersPlugin`, and `FormatCheckPlugin` — accept a `timeoutSec: int option`
on their `create` constructors. When set, per-event work is bounded by
`ProcessHelper.runWithTimeout`; on expiry the run is recorded as `TimedOut`
(rendered as ⏱) and the plugin continues with the next event.

**Orphan-task limitation (in-process plugins).** For the three in-process
plugins above, the timeout is *advisory*: the underlying FCS / FSharpLint /
Fantomas call is driven under the timeout's `CancellationToken`
(`ProcessHelper.runWithCancellableTimeout`), so a timed-out unit actually
unwinds and releases its locks rather than running on as an orphan thread.
Process-spawning plugins (`BuildPlugin`, `TestPrunePlugin`,
`FileCommandPlugin`) kill the child process tree on expiry via the single
spawn primitive, `ProcessHelper.runProcess`, which additionally polls
`HasExited` and bounds its post-exit stream drain — so neither a
machine-sleep-killed child nor a grandchild holding the inherited stdout
pipe can block it forever.

## Install

```bash
dotnet add package FsHotWatch
```
