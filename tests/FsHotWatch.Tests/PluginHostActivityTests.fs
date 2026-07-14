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
    test <@ observedDuring.Value |> List.length = 2 @>
    test <@ List.isEmpty (host.GetSubtasks("subtasker")) @>

[<Fact(Timeout = 15000)>]
let ``Completed verdict summary is captured in history and wins over CompleteWithSummary`` () =
    let host = PluginHost.create nullChecker "/tmp/test"

    let handler =
        makeCtxAwareHandlerWithVerdict
            "summarizer"
            { Summary = "did the thing"
              Elapsed = TimeSpan.Zero }
            (fun ctx ->
                async {
                    ctx.Log "working"
                    // The legacy side-channel must not override the verdict the
                    // Completed status carries — ONE source of truth.
                    ctx.CompleteWithSummary "a stale side-channel summary"
                })

    host.RegisterHandler(handler)
    host.EmitFileChanged(SourceChanged [ "a.fs" ])
    waitUntil (fun () -> host.GetHistory("summarizer") |> List.isEmpty |> not) 12000
    let r = List.head (host.GetHistory("summarizer"))
    test <@ r.Summary = Some "did the thing" @>

[<Fact(Timeout = 15000)>]
let ``Completed verdict elapsed is recorded in history`` () =
    let host = PluginHost.create nullChecker "/tmp/test"

    let handler =
        makeCtxAwareHandlerWithVerdict
            "timer"
            { Summary = "timed work"
              Elapsed = TimeSpan.FromMilliseconds 25.0 }
            (fun _ctx -> async { do! Async.Sleep 10 })

    host.RegisterHandler(handler)
    host.EmitFileChanged(SourceChanged [ "a.fs" ])
    waitUntil (fun () -> host.GetHistory("timer") |> List.isEmpty |> not) 12000
    let r = List.head (host.GetHistory("timer"))
    // The record carries the verdict's sworn duration — exactly, not a host
    // wall-clock approximation.
    test <@ r.Elapsed = TimeSpan.FromMilliseconds 25.0 @>

[<Fact(Timeout = 15000)>]
let ``Terminal transition auto-ends open subtasks`` () =
    let host = PluginHost.create nullChecker "/tmp/test"

    let handler =
        makeCtxAwareHandler "leaker" (fun ctx -> async { ctx.StartSubtask "k1" "leaky" })

    host.RegisterHandler(handler)
    host.EmitFileChanged(SourceChanged [ "a.fs" ])
    waitUntil (fun () -> host.GetHistory("leaker") |> List.isEmpty |> not) 12000
    test <@ List.isEmpty (host.GetSubtasks("leaker")) @>
