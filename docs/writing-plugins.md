# Writing a plugin

FsHotWatch plugins are declarative: you write an `update` function that takes
the current state and an event and returns the next state. The framework owns
the agent (one mailbox per plugin), status tracking, caching, and IPC command
registration — you just describe how your state reacts to events.

A plugin is a `PluginHandler<'State, 'Msg>` record. `'State` is whatever you
want to remember between events; `'Msg` is the type of custom messages the
plugin can post back to itself (use `unit` if you don't need any).

```fsharp
open System
open FsHotWatch.Events          // PluginEvent cases, FileCheckResult, AbsFilePath
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
                    // result.ParseResults and result.CheckResults come from the
                    // warm FSharpChecker — no re-parsing needed.
                    printfn "Checked: %s" (AbsFilePath.value result.File)
                    ctx.ReportStatus(Completed(DateTime.UtcNow))
                    return { state with FilesChecked = state.FilesChecked + 1 }
                | _ -> return state
            }
      Commands =
        [ "my-status",
          fun _ctx state _args ->
              async { return $"checked %d{state.FilesChecked} files" } ]
      Subscriptions = Set.ofList [ SubscribeFileChecked ]
      CacheKey = None
      Teardown = None }

// Register with the daemon:
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
- `ctx.ReportStatus(status)` — report `Running(since = ...)`, `Completed(...)`, or `Failed(...)`.
- `ctx.ReportErrors(file, entries)` / `ctx.ClearErrors(file)` / `ctx.ClearAllErrors()` — manage diagnostics in the shared error ledger.
- `ctx.CompleteWithSummary(text)` — override the auto-derived summary captured in run history on the next terminal transition (e.g. `"built 4 projects"`).
- `ctx.CompleteWithTimeout(reason)` — mark the next terminal transition as `TimedOut`.

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
