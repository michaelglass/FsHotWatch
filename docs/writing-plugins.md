# Writing a plugin

FsHotWatch plugins are declarative: you write an `update` function that takes
the current state and an event and returns the next state. The framework owns
the agent (one mailbox per plugin), status tracking, caching, and IPC command
registration — you just describe how your state reacts to events.

A plugin is a `PluginHandler<'State, 'Msg>` record. `'State` is whatever you
want to remember between events; `'Msg` is the type of custom messages the
plugin can post back to itself (use `unit` if you don't need any).

<!-- This block is sourced from a real, compiled example — see
     examples/PluginExample/PluginExample.fs. Do not edit it here; edit the
     source file and run `mise run sync-docs`. The example is a member of
     FsHotWatch.slnx, so the solution build (CI included) compiles it with
     TreatWarningsAsErrors; `mise run ci` additionally runs `sync-docs-check`,
     which fails if this block drifts from the source. -->
<!-- sync:plugin-example:start src=examples/PluginExample/PluginExample.fs -->
```fsharp
open System
open FsHotWatch.Events // PluginEvent cases, FileCheckResult, AbsFilePath
open FsHotWatch.PluginFramework // PluginHandler, PluginName, SubscribeFileChecked

type MyState = { FilesChecked: int }

let myPlugin: PluginHandler<MyState, unit> =
    { Name = PluginName.create "my-plugin"
      Init = { FilesChecked = 0 }
      Update =
        fun ctx state event ->
            async {
                match event with
                | FileChecked result ->
                    let started = DateTime.UtcNow

                    // result.ParseResults and result.CheckResults come from the
                    // warm FSharpChecker — no re-parsing needed.
                    printfn "Checked: %s" (AbsFilePath.value result.File)

                    // A terminal status carries its verdict: what was done, and
                    // how long it took — MEASURED, never guessed. "Done with
                    // nothing to report" does not typecheck (`RunVerdict.create`
                    // rejects an empty summary) — by design.
                    ctx.ReportStatus(
                        Completed(
                            DateTime.UtcNow,
                            RunVerdict.create $"checked %d{state.FilesChecked + 1} files" (DateTime.UtcNow - started)
                        )
                    )

                    return
                        { state with
                            FilesChecked = state.FilesChecked + 1 }
                | _ -> return state
            }
      Commands = [ "my-status", fun _ctx state _args -> async { return $"checked %d{state.FilesChecked} files" } ]
      Subscriptions = Set.ofList [ SubscribeFileChecked ]
      CacheKey = None
      Teardown = None }
```
<!-- sync:plugin-example:end -->

Then register the handler with a live daemon:

```fsharp
daemon.RegisterHandler(myPlugin)
```

## The handler record

| Field | What it is |
|-------|-----------|
| `Name` | `PluginName.create "..."` — display name, shown in `fshw check` / `fshw status`. |
| `Init` | The plugin's starting state. |
| `Update` | `ctx -> state -> event -> Async<state>`. Pattern-match the event, do your work, return the next state. |
| `Commands` | IPC commands, `(name, fun ctx state args -> Async<string>)`. Invoked by tools over the pipe; the string you return is the reply. |
| `Subscriptions` | A `Set` of the events you want delivered. Use `PluginSubscriptions.none` (empty) if you only handle custom messages. |
| `CacheKey` | `Some (fun event -> hash)` to replay a cached result on an unchanged input, or `None` to always run. |
| `Teardown` | `Some (fun () -> ...)` to clean up when the host shuts down, or `None`. |

## Events

Subscribe to these via `Subscriptions` (each tag below maps to the matching
`PluginEvent` case you pattern-match on in `Update`):

| Subscription tag | Event you match | Fires when |
|------------------|-----------------|------------|
| `SubscribeFileChanged` | `FileChanged kind` | A source or project file was saved. |
| `SubscribeFileChecked` | `FileChecked result` | A file finished type-checking (carries warm parse + check results). |
| `SubscribeBatchChecked` | `BatchChecked batch` | A batch of files finished checking together. |
| `SubscribeBuildCompleted` | `BuildCompleted result` | `dotnet build` finished (success or failure). |
| `SubscribeTestRunStarted` | `TestRunStarted info` | A test run began (fires once per run). |
| `SubscribeTestProgress` | `TestProgress delta` | One or more test projects finished mid-run. |
| `SubscribeTestRunCompleted` | `TestRunCompleted result` | A test run finished (carries the full cumulative results). |
| `SubscribeCommandCompleted` | `CommandCompleted result` | A `fileCommand` finished. |

Whatever you subscribe to is delivered; `Custom msg` (your own `'Msg`, posted
via `ctx.Post`) is always delivered regardless of subscriptions.

## The context (`ctx`)

`ctx` carries everything a handler can do as a side effect:

**Status & errors**
- `ctx.ReportStatus(status)` — report `Running(since = ...)`, or a terminal `Completed(at, verdict)` / `Failed(error, at, verdict)`.
  Every terminal CARRIES its `RunVerdict` (what the run did + how long it took), and `RunVerdict.create` rejects an empty summary — a content-free `✓` does not typecheck. `PluginCtxHelpers.completeWith` / `failedWith` are the ergonomic constructors.
- `ctx.ReportErrors(file, entries)` / `ctx.ClearErrors(file)` / `ctx.ClearAllErrors()` — manage diagnostics in the shared error ledger.
- `ctx.CompleteWithTimeout(reason)` — mark the next terminal transition as `TimedOut` (you still report the terminal itself; its verdict carries the summary).

**Running work**
- `ctx.RunExclusive(key, work)` — run `work` under a single-flight slot, returning a `RunClaim`. The framework reports `Running` at the claim and posts `work`'s result back as a `Custom` message.
  The result must be handled: `SlotBusy` means the work was **not started**, so decide explicitly — skip it, or queue it. Dropping a refused claim is dropping work.

**The warm compiler**
- `ctx.Checker` — the shared, warm `FSharpChecker`. Reuse it for your own analysis instead of starting a new one.
- `ctx.RepoRoot` — absolute path to the repository root.
- `ctx.FcsSuppressedCodes` — FCS warning codes the host treats as noise; merge with per-file `#nowarn` before any gate decision.

**Emitting events to other plugins**
- `ctx.EmitBuildCompleted(result)`, `ctx.EmitTestRunStarted/Progress/RunCompleted(...)`, `ctx.EmitCommandCompleted(result)`.
- `ctx.Post(msg)` — post a `Custom` message back to your own agent.

**Progress & logging**
- `ctx.StartSubtask(key, label)` / `ctx.UpdateSubtask(key, label)` / `ctx.EndSubtask(key)` — surface named concurrent work, with live per-subtask elapsed time in `fshw check` output.
- `ctx.Log(line)` — append to the activity tail shown under your plugin in `fshw check`; also routes to `Logging.info`.

**Concurrency**
- `ctx.RunExclusive(key, work)` — run `work` exclusively under `key`; further calls with the same key while it runs are dropped. On completion, the returned `'Msg` is posted back as a `Custom` event.
- `ctx.IsRunning(key)` — whether `key` is currently running under `RunExclusive` (handy for IPC status without your own "is running" flag).

## Run history

Status transitions are observed: when a plugin moves to `Completed` or `Failed`,
the host snapshots the current subtasks and activity into a bounded per-plugin
run history. That history shows up under `fshw check` verbose output as
`started / elapsed / summary` on the next run.

## A real example

The bundled plugins are the best reference. The smallest end-to-end one is
[`FsHotWatch.FileCommand`](../src/FsHotWatch.FileCommand/FileCommandPlugin.fs) —
it subscribes to file changes and test results, runs a command, and exposes a
status command over IPC.
