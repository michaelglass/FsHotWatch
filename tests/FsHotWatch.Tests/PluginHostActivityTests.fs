module FsHotWatch.Tests.PluginHostActivityTests

open System
open Xunit
open Swensen.Unquote
open FsHotWatch.Events
open FsHotWatch.PluginFramework
open FsHotWatch.PluginHost
open FsHotWatch.Tests.TestHelpers

let private nullChecker =
    Unchecked.defaultof<FSharp.Compiler.CodeAnalysis.FSharpChecker>

let private makeCtxAwareHandlerWithVerdict
    (name: string)
    (verdict: RunVerdict)
    (action: PluginCtx<unit> -> Async<unit>)
    =
    { Name = PluginName.create name
      Init = ()
      Update =
        fun ctx state event ->
            async {
                match event with
                | FileChanged _ ->
                    ctx.ReportStatus(Running System.DateTime.UtcNow)
                    do! action ctx
                    ctx.ReportStatus(Completed(System.DateTime.UtcNow, verdict))
                | _ -> ()

                return state
            }
      Commands = []
      Subscriptions = Set.ofList [ SubscribeFileChanged ]
      PrepareCommit = None
      CacheKey = None
      Teardown = None }

let private makeCtxAwareHandler (name: string) (action: PluginCtx<unit> -> Async<unit>) =
    makeCtxAwareHandlerWithVerdict name testVerdict action

[<Fact(Timeout = 15000)>]
let ``ctx.Log appears in host activity tail`` () =
    let host = PluginHost.create nullChecker "/tmp/test"

    let handler =
        makeCtxAwareHandler "logger" (fun ctx ->
            async {
                ctx.Log "first"
                ctx.Log "second"
            })

    host.RegisterHandler(handler)
    host.EmitFileChanged(SourceChanged [ "a.fs" ])
    waitUntil (fun () -> host.GetHistory("logger") |> List.isEmpty |> not) 12000
    waitForTerminalStatus host "logger" 12000
    let hist = host.GetHistory("logger")
    test <@ hist.Length = 1 @>
    let r = List.head hist
    test <@ r.ActivityTail = [ "first"; "second" ] @>

[<Fact(Timeout = 20000)>]
let ``ctx.StartSubtask and EndSubtask reflected in host`` () =
    let host = PluginHost.create nullChecker "/tmp/test"
    let observedDuring = ref []

    let handler =
        makeCtxAwareHandler "subtasker" (fun ctx ->
            async {
                ctx.StartSubtask "k1" "label1"
                ctx.StartSubtask "k2" "label2"
                observedDuring.Value <- host.GetSubtasks("subtasker")
                ctx.EndSubtask "k1"
                ctx.EndSubtask "k2"
            })

    host.RegisterHandler(handler)
    host.EmitFileChanged(SourceChanged [ "a.fs" ])
    waitUntil (fun () -> host.GetHistory("subtasker") |> List.isEmpty |> not) 12000
    waitForTerminalStatus host "subtasker" 12000
    test <@ observedDuring.Value |> List.length = 2 @>
    test <@ List.isEmpty (host.GetSubtasks("subtasker")) @>

[<Fact(Timeout = 15000)>]
let ``Completed verdict summary is captured in history`` () =
    // The status IS the summary channel — CompleteWithSummary is gone,
    // so no side-channel is left to disagree with it.
    let host = PluginHost.create nullChecker "/tmp/test"

    let handler =
        makeCtxAwareHandlerWithVerdict "summarizer" (RunVerdict.create "did the thing" TimeSpan.Zero) (fun ctx ->
            async { ctx.Log "working" })

    host.RegisterHandler(handler)
    host.EmitFileChanged(SourceChanged [ "a.fs" ])
    waitUntil (fun () -> host.GetHistory("summarizer") |> List.isEmpty |> not) 12000
    waitForTerminalStatus host "summarizer" 12000
    let r = List.head (host.GetHistory("summarizer"))
    test <@ r.Summary = Some "did the thing" @>

[<Fact(Timeout = 15000)>]
let ``Completed verdict elapsed is recorded in history`` () =
    let host = PluginHost.create nullChecker "/tmp/test"

    let handler =
        makeCtxAwareHandlerWithVerdict
            "timer"
            (RunVerdict.create "timed work" (TimeSpan.FromMilliseconds 25.0))
            (fun _ctx -> async { do! Async.Sleep 10 })

    host.RegisterHandler(handler)
    host.EmitFileChanged(SourceChanged [ "a.fs" ])
    waitUntil (fun () -> host.GetHistory("timer") |> List.isEmpty |> not) 12000
    waitForTerminalStatus host "timer" 12000
    let r = List.head (host.GetHistory("timer"))
    // The verdict's measured duration, exactly — not a host wall-clock estimate.
    test <@ r.Elapsed = TimeSpan.FromMilliseconds 25.0 @>

[<Fact(Timeout = 15000)>]
let ``Terminal transition auto-ends open subtasks`` () =
    let host = PluginHost.create nullChecker "/tmp/test"

    let handler =
        makeCtxAwareHandler "leaker" (fun ctx -> async { ctx.StartSubtask "k1" "leaky" })

    host.RegisterHandler(handler)
    host.EmitFileChanged(SourceChanged [ "a.fs" ])
    waitUntil (fun () -> host.GetHistory("leaker") |> List.isEmpty |> not) 12000
    waitForTerminalStatus host "leaker" 12000
    test <@ List.isEmpty (host.GetSubtasks("leaker")) @>

// --- The Failed path can no longer manufacture a zero-length run -------------
//
// The trap: with no `Running` before the terminal, RecordTerminal
// fell back to `startedAt = at` and recorded elapsed 0 — a terminal rendering
// "started:" with no "elapsed:". The verdict now rides the terminal transition,
// so the host derives startedAt from the run's own measured duration.

/// A handler that reports a terminal `Failed` WITHOUT ever reporting `Running`
/// — the shape that used to record a fabricated zero-length run.
let private failWithoutRunning (name: string) (error: string) (verdict: RunVerdict) =
    { Name = PluginName.create name
      Init = ()
      Update =
        fun ctx state event ->
            async {
                match event with
                | FileChanged _ -> ctx.ReportStatus(Failed(error, System.DateTime.UtcNow, verdict))
                | _ -> ()

                return state
            }
      Commands = []
      Subscriptions = Set.ofList [ SubscribeFileChanged ]
      PrepareCommit = None
      CacheKey = None
      Teardown = None }

[<Fact(Timeout = 15000)>]
let ``a Failed with no preceding Running still records the verdict's measured elapsed`` () =
    let host = PluginHost.create nullChecker "/tmp/test"

    host.RegisterHandler(
        failWithoutRunning
            "faller"
            "analysis failed: boom"
            (RunVerdict.create "analysis failed" (TimeSpan.FromSeconds 4.0))
    )

    host.EmitFileChanged(SourceChanged [ "a.fs" ])
    waitUntil (fun () -> host.GetHistory("faller") |> List.isEmpty |> not) 12000
    waitForTerminalStatus host "faller" 12000
    let r = List.head (host.GetHistory("faller"))

    // NOT TimeSpan.Zero — the "started: with no elapsed:" signature is gone.
    test <@ r.Elapsed = TimeSpan.FromSeconds 4.0 @>
    test <@ r.Summary = Some "analysis failed" @>

    match r.Outcome with
    | FailedRun err -> test <@ err = "analysis failed: boom" @>
    | other -> failwithf "expected FailedRun, got %A" other

[<Fact(Timeout = 15000)>]
let ``a Failed run's history summary comes from the verdict, not a side-channel`` () =
    let host = PluginHost.create nullChecker "/tmp/test"

    host.RegisterHandler(
        failWithoutRunning
            "reporter"
            "2 failed: Foo, Bar"
            (RunVerdict.create "1 passed, 2 failed in 3 projects" (TimeSpan.FromSeconds 9.0))
    )

    host.EmitFileChanged(SourceChanged [ "a.fs" ])
    waitUntil (fun () -> host.GetHistory("reporter") |> List.isEmpty |> not) 12000
    waitForTerminalStatus host "reporter" 12000
    let r = List.head (host.GetHistory("reporter"))
    test <@ r.Summary = Some "1 passed, 2 failed in 3 projects" @>
    test <@ r.Elapsed = TimeSpan.FromSeconds 9.0 @>

[<Fact(Timeout = 5000)>]
let ``pipeline activity sink updates its own subtask without changing another reporter`` () =
    let host = PluginHost.create nullChecker "/tmp/test"
    let pipeline = host.ActivitySinkFor("fcs")
    let other = host.ActivitySinkFor("test-prune")

    try
        pipeline.StartSubtask("parse", "reading source")
        other.StartSubtask("parse", "reading test symbols")
        let started = (host.GetSubtasks("fcs") |> List.exactlyOne).StartedAt
        pipeline.UpdateSubtask("parse", "checking source")
        pipeline.Log "parsed input"

        let current = host.GetActivitySnapshot("fcs")
        let subtask = current.Subtasks |> List.exactlyOne
        test <@ subtask.Key = "parse" @>
        test <@ subtask.Label = "checking source" @>
        test <@ subtask.StartedAt = started @>
        test <@ current.ActivityTail = [ "parsed input" ] @>
        test <@ (host.GetSubtasks("test-prune") |> List.exactlyOne).Label = "reading test symbols" @>
        test <@ host.GetActivityTail("test-prune") |> List.isEmpty @>

        pipeline.EndSubtask("parse")
        test <@ host.GetSubtasks("fcs") |> List.isEmpty @>
        test <@ host.GetSubtasks("test-prune") |> List.length = 1 @>
        other.EndSubtask("parse")
        test <@ host.GetSubtasks("test-prune") |> List.isEmpty @>
    finally
        host.Teardown()
