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

/// Messages the plugin posts to itself.
type MyMsg = ResetCount

let myPlugin: PluginHandler<MyState, MyMsg> =
    { Name = PluginName.create "my-plugin"
      Init = { FilesChecked = 0 }
      Update =
        fun ctx state event ->
            async {
                match event with
                // Most plugins care about only some of what they are handed. Say so
                // out loud: a run that examined NOTHING must not render as a ✓, and
                // the framework cannot infer that for you. `RunVerdict.verifiedNothing`
                // is the verdict every surface reads to downgrade the glyph to ⚠ (and
                // the agent / verdict.json token to `warn`) — the run record carries
                // `RunOutcome.VerifiedNothing`. The STATUS stays `Completed` — nothing
                // failed.
                | FileChecked result when not ((AbsFilePath.value result.File).EndsWith ".fs") ->
                    ctx.ReportStatus(
                        Completed(DateTime.UtcNow, RunVerdict.verifiedNothing "not an F# source file" TimeSpan.Zero)
                    )

                    return state
                | FileChecked result ->
                    let started = DateTime.UtcNow

                    // result.ParseResults and result.CheckResults come from the
                    // warm FSharpChecker — no re-parsing needed.
                    printfn "Checked: %s" (AbsFilePath.value result.File)

                    // A terminal status carries its verdict: what was done, and
                    // how long it took — MEASURED, never guessed. "Done with
                    // nothing to report" does not typecheck (`RunVerdict.create`
                    // rejects an empty summary) — by design. An empty summary the
                    // compiler catches; a MISLEADING one ("checked 0 files") it does
                    // not, which is what the arm above exists to prevent.
                    ctx.ReportStatus(
                        Completed(
                            DateTime.UtcNow,
                            RunVerdict.create $"checked %d{state.FilesChecked + 1} files" (DateTime.UtcNow - started)
                        )
                    )

                    return
                        { state with
                            FilesChecked = state.FilesChecked + 1 }
                | Custom ResetCount -> return { state with FilesChecked = 0 }
                | _ -> return state
            }
      Commands =
        // An observation reads the last committed state and cannot start work.
        [ "my-status",
          PluginCommand.Observe(fun _ctx state _args -> async { return $"checked %d{state.FilesChecked} files" })
          // A request never sees state: it posts, and `Update` applies the message.
          "my-reset",
          PluginCommand.Request(fun ctx _args ->
              async {
                  ctx.Post ResetCount
                  return "reset requested"
              }) ]
      Subscriptions = Set.ofList [ SubscribeFileChecked ]
      PrepareCommit = None
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
| `PrepareCommit` | `Some (fun committed candidate -> async { ... })` for durable work that must succeed before the new state counts, or `None`. See [Committing state](#committing-state). |
| `Commands` | IPC commands, `(name, PluginCommand.Observe ...)` or `(name, PluginCommand.Request ...)`. Invoked by tools over the pipe; the string you return is the reply. See [Commands](#commands). |
| `Subscriptions` | A `Set` of the events you want delivered. Use `PluginSubscriptions.none` (empty) if you only handle custom messages. |
| `CacheKey` | `Some (fun state event -> hash)` to replay a cached result on an unchanged input, or `None` to always run. `state` is the committed state the event's `Update` will receive. |
| `Teardown` | `Some (fun () -> ...)` to clean up when the host shuts down, or `None`. |

## Commands

A command is one of two contracts, and the case you pick says which.

- **`PluginCommand.Observe (fun ctx state args -> ...)`** reads state. It receives the
  last committed state, read from the plugin's published snapshot, so it answers at once
  even while an `Update` is running. Its context has no `Post`, so an observation cannot
  start work.
- **`PluginCommand.Request (fun ctx args -> ...)`** asks the plugin to do something. It
  gets `ctx.Post` and never sees state. A choice that depends on state, such as "re-run
  the tests that failed", belongs in `Update`: post a message that names the request and
  let `Update` decide against the state it is folding the message into. A request that
  read state first and then posted could act on a result that was already stale.

A request that has to report what the plugin did with it uses
`ctx.EnqueueExclusiveIntent key coalescingKey message`. The message waits behind whatever
holds `key` and is delivered once the key is free; the returned task completes when that
exact message's state is committed, and fails if its `Update` fails. `ctx.Post` is
fire-and-forget.

`PluginCommand.invoke command ctx state args` runs either case against an explicit
state, which is how a plugin's own tests drive a command without a host.

## Committing state

`Update` returns a candidate state. Most plugins stop there. A plugin that also has to
make something durable, such as writing a ledger to disk, supplies `PrepareCommit`:

```fsharp
PrepareCommit =
    Some(fun committed candidate ->
        async {
            // Stage the durable write. Throwing here keeps `committed`.
            let staged = stage candidate
            return { Finalize = async { do! publish staged } }
        })
```

The framework commits an event in a fixed order: it publishes the candidate state, runs
`Finalize`, writes the cache entry, and only then marks the event committed. Anything
waiting on the event, and the host's "at rest" answer, sees it as outstanding until that
last step.

If preparation throws, the candidate is discarded and the event fails. If `Finalize` or
the cache write throws, the candidate stays published and the event fails. A failed
preparation or finalization is recorded until a later event's own preparation succeeds.

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

## Reading a test run's results

`TestRunCompleted` carries `Results: Map<string, TestResult>` — one entry per test
project — plus `Verification`, the run-level answer. **They answer different
questions, and the API is shaped so you cannot use one for the other.**

Per project, ask [`TestResult.verdict`][verdict]. It returns a `ProjectVerdict` with
exactly three cases:

| `ProjectVerdict` | What it means |
|------------------|---------------|
| `Verified` | The runner executed at least one test and every one passed. The only case that discharges anything. |
| `Refuted` | Tests executed and at least one failed (or was killed at its timeout). The only case that is this project's own fault, and the only one a "which projects failed?" report should list. |
| `NothingVerified` | The filter matched no test, the build artifact was not ready, or the host aborted before writing a report. **Neither a pass nor a failure.** |

`NothingVerified` is the whole point of the type. There used to be a
`TestResult.isPassed` bool, and it answered **`true`** for a project that ran zero
tests — so `Map.forall isPassed` over a run where every project executed nothing
type-checked, was total, and folded to a green. It is **deleted**. What replaces it is
`verdict` plus exactly one bool, `TestResult.verifiedGreen`, which is true for
`Verified` alone.

There is deliberately **no** "did not fail" bool. `not (TestResult.verifiedGreen r)`
is true for a zero-match project as well as a red one — which is what you want for a
*gate* (nothing was proved) and wrong for a failure *report* (nothing failed). A
report must match on `Refuted`. Adding a bool that spans all three cases would
rebuild the original defect under a new name.

For the **run-level** question, read `Verification` rather than folding the
per-project bool: `Map.forall` is vacuously `true` for an empty map, so a run that
selected no project at all would fold to a green having executed nothing.

<!-- This block is sourced from a real, compiled example — see
     examples/PluginExample/PluginExample.fs. Do not edit it here; edit the
     source file and run `mise run sync-docs`. It reuses the `open`s from the
     block above. -->
<!-- sync:test-verdict-example:start src=examples/PluginExample/PluginExample.fs -->
```fsharp
/// A plugin that reports what a finished test run actually ESTABLISHED.
///
/// It exists to demonstrate the two questions a test run answers and why they need
/// different tools: `TestResult.verdict` answers the PER-PROJECT one, and
/// `TestRunCompleted.Verification` answers the RUN-level one. Reaching for the first
/// to answer the second is the mistake this API shape exists to prevent.
let testVerdictPlugin: PluginHandler<unit, unit> =
    { Name = PluginName.create "test-verdict"
      Init = ()
      Update =
        fun ctx state event ->
            async {
                match event with
                | TestRunCompleted completed ->
                    // PER PROJECT. `TestResult.verdict` is the ONE place the six
                    // `TestResult` cases are told apart, and `ProjectVerdict` has exactly
                    // three answers. Match it exhaustively — there is deliberately no
                    // bool spanning all three, because `NothingVerified` is neither a
                    // pass nor a failure and any single bool has to lie about one of them.
                    let describe (project: string) (result: TestResult) =
                        match TestResult.verdict result with
                        | Verified -> $"%s{project}: verified green"
                        | Refuted -> $"%s{project}: FAILED — this project's own fault"
                        // The filter matched no test, the build was not ready, or the
                        // host aborted before writing a report. Nothing was proved — and
                        // nothing failed either, so this project is not a red.
                        | NothingVerified -> $"%s{project}: nothing verified"

                    for KeyValue(project, result) in completed.Results do
                        printfn "%s" (describe project result)

                    // The projects safe to treat as green. `verifiedGreen` is the only
                    // bool offered over `verdict`, and it is TRUE for `Verified` alone —
                    // so selecting with it can never let a project that ran nothing pass.
                    let green =
                        completed.Results
                        |> Map.toList
                        |> List.filter (fun (_, r) -> TestResult.verifiedGreen r)

                    // A failure REPORT matches `Refuted` instead of negating that bool.
                    // `not (TestResult.verifiedGreen r)` is also true for a project that
                    // verified nothing — correct for a gate, wrong for a report, which is
                    // why no "not a failure" bool is offered to blur the two.
                    let failed =
                        completed.Results
                        |> Map.toList
                        |> List.filter (fun (_, r) -> TestResult.verdict r = Refuted)

                    // PER RUN. Do NOT fold the per-project bool to get here: `Map.forall`
                    // is vacuously TRUE for an empty map, so a run that selected no
                    // project at all would report green having executed nothing.
                    // `Verification` is the total run-level answer and it settles
                    // emptiness first.
                    let verdict =
                        let elapsed = completed.TotalElapsed

                        match completed.Verification with
                        | Ran FullSuite ->
                            RunVerdict.create
                                $"full suite: %d{List.length green} green, %d{List.length failed} failed"
                                elapsed
                        | Ran Partial ->
                            RunVerdict.create
                                $"impact-filtered: %d{List.length green} green, %d{List.length failed} failed"
                                elapsed
                        | NoProjectsSelected -> RunVerdict.verifiedNothing "no project was selected" elapsed
                        | AllZeroMatch n ->
                            RunVerdict.verifiedNothing $"%d{n} project(s) ran, all matched zero tests" elapsed
                        | NothingExecuted -> RunVerdict.verifiedNothing "no project executed a test" elapsed

                    // `verifiedNothing` makes the three cases above a verdict every surface
                    // refuses a ✓ — the run record's outcome is `VerifiedNothing`, a case,
                    // not a summary prefix. The status stays `Completed` — nothing FAILED.
                    ctx.ReportStatus(Completed(DateTime.UtcNow, verdict))

                    return state
                | _ -> return state
            }
      Commands = []
      Subscriptions = Set.ofList [ SubscribeTestRunCompleted ]
      PrepareCommit = None
      CacheKey = None
      Teardown = None }
```
<!-- sync:test-verdict-example:end -->

[verdict]: ../src/FsHotWatch/Events.fs

## The context (`ctx`)

`ctx` carries everything a handler can do as a side effect:

**Status & errors**
- `ctx.ReportStatus(status)` — report `Running(since = ...)`, or a terminal `Completed(at, verdict)` / `Failed(error, at, verdict)`.
  Every terminal CARRIES its `RunVerdict` (what the run did + how long it took), and `RunVerdict.create` rejects an empty summary — a content-free `✓` does not typecheck. `PluginCtxHelpers.completeWith` / `failedWith` are the ergonomic constructors.
- `RunVerdict.verifiedNothing "<why>" elapsed` (or `PluginCtxHelpers.completeVerifyingNothing ctx "<why>" elapsed`) — the verdict for a run that **executed nothing**: no file compared, no test run, no project selected. The run record's outcome becomes `RunOutcome.VerifiedNothing` — a case, not a summary prefix — the summary reads `NOTHING VERIFIED: …`, and every surface then refuses the run a green (`⚠` instead of `✓`, `warn` in agent mode and in `.fshw/verdict.json`'s `plugins[]`). Report the terminal as `Completed` — nothing failed, and a `Failed` would turn an honest "no verdict" into "failures found".
  An empty summary does not typecheck, but a *misleading* one does: `0 files checked` is a well-formed summary that renders `✓`. `verifiedNothing` is how you stop it. The framework cannot infer this, so a plugin that verifies nothing and stays quiet about it still shows a checkmark.
- `ctx.ReportErrors(file, entries)` / `ctx.ClearErrors(file)` / `ctx.ClearAllErrors()` — manage diagnostics in the shared error ledger.
- `ctx.CompleteWithTimeout(reason)` — mark the next terminal transition as `TimedOut` (you still report the terminal itself; its verdict carries the summary).

**Running work**
- `ctx.RunExclusive(key, work)` — run `work` under a single-flight slot, returning a `RunClaim`. The framework reports `Running` at the claim and posts `work`'s result back as a `Custom` message.
  The result must be handled: `SlotBusy` means the work was **not started**, so decide explicitly — skip it, or queue it. Dropping a refused claim is dropping work.
  Wrap `work` in `PluginWork.resultFirst` to let its result fold ahead of the dispatched events queued before it — see [Result ordering](#result-ordering).

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
- `ctx.SlotHolder(key)` — what holds `key` under `RunExclusive`: `SlotHolder.Free`, `LiveRun` (a worker is running), or `Fold` (a finished run's result or a delivered intent has not committed). A claim on a held key returns `SlotBusy`, so ask before doing expensive work whose only purpose is to launch. IPC commands keep `ctx.IsRunning(key)` for status.

## Result ordering

A plugin's mailbox folds one message at a time. Two kinds of message reach it:
**dispatched events** (`FileChanged`, `FileChecked`, `BatchChecked`, `BuildCompleted`,
the test lifecycle, `CommandCompleted`) and the plugin's **own messages**, which are
`Custom` (a `ctx.Post`, a delivered intent, an exclusive run's result).

By default the mailbox is first in, first out. A finished run's result therefore waits
behind every event admitted before it. The result keeps its key held (`SlotHolder.Fold`)
and its `Running` status until it folds. When those folds are slow, the result can wait a
long time. The wait shows as the `<key> result queued` subtask.

`PluginWork.resultFirst work` declares that `work`'s result does not have to wait for
dispatched events. Once the result is in the mailbox, the next message folded is the
oldest of the plugin's own messages. Own messages keep folding until the result has
folded, and only then do dispatched events resume. So the result folds once the fold
already in flight commits. It does not wait for the events queued behind that fold.

What stays fixed:

- **Own messages keep their order.** Anything the worker posted before returning folds
  before its result. So does any intent or post admitted earlier. Own messages move
  ahead of dispatched events as one block.
- **Dispatched events keep their order.** Each `BatchChecked` still folds after the
  `FileChecked` events of its cohort, and a `BuildCompleted` still folds after both.
- **Nothing is lost.** Only the order changes: every admitted message still folds once.
- **Run N+1 cannot start ahead of run N's result.** Until run N's result commits, it
  holds its key, so any other fold that claims the key gets `SlotBusy`. The only fold
  that can claim run N+1 before then is run N's own result fold. Moving the result
  earlier just means fewer events see `SlotBusy`: an event that folds after the result
  finds the key free and claims it directly.
- **The owner model is unchanged.** Admission, `SlotHolder`, `Running`, and the
  commit order work as before, because they never depended on mailbox order.

Declare `resultFirst` only when the result's `Update` gives the same answer whether a
dispatched event admitted during the run folds before it or after it. In other words,
the fold must not rely on "an event I folded while the key was held" to learn that the
world moved during the run. Two plugins show the difference:

- **TestPrune** declares its tests runs. Its result fold removes only the symbols the run
  launched with, so file checks that arrive during the run stay owed either way.
- **Build** does not declare. A `FileChanged` folded during a build is held in
  `PendingFiles`, and `BuildDone` launches those files as the next build. Suppose
  `BuildDone` folded first instead. It would find nothing pending and cache a success
  under a key hashed from the edited sources, sources that build never compiled.

That second case is why the declaration is opt-in. The framework cannot tell whether a
result depends on the events queued before it. The declaring plugin must decide. This
includes any event the run itself emitted that the plugin also subscribes to.

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
