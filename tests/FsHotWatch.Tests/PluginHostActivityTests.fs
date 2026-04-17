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

let private makeCtxAwareHandler (name: string) (action: PluginCtx<unit> -> Async<unit>) =
    { Name = PluginName.create name
      Init = ()
      Update =
        fun ctx state event ->
            async {
                match event with
                | FileChanged _ ->
                    ctx.ReportStatus(Running System.DateTime.UtcNow)
                    do! action ctx
                    ctx.ReportStatus(Completed System.DateTime.UtcNow)
                | _ -> ()

                return state
            }
      Commands = []
      Subscriptions = Set.ofList [ SubscribeFileChanged ]
      CacheKey = None
      Teardown = None }

[<Fact(Timeout = 5000)>]
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
    waitUntil (fun () -> host.GetHistory("logger") |> List.isEmpty |> not) 5000
    let hist = host.GetHistory("logger")
    test <@ hist.Length = 1 @>
    let r = List.head hist
    test <@ r.ActivityTail = [ "first"; "second" ] @>

[<Fact(Timeout = 10000)>]
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
    waitUntil (fun () -> host.GetHistory("subtasker") |> List.isEmpty |> not) 5000
    test <@ observedDuring.Value |> List.length = 2 @>
    test <@ List.isEmpty (host.GetSubtasks("subtasker")) @>

[<Fact(Timeout = 5000)>]
let ``CompleteWithSummary captured in history`` () =
    let host = PluginHost.create nullChecker "/tmp/test"

    let handler =
        makeCtxAwareHandler "summarizer" (fun ctx ->
            async {
                ctx.Log "working"
                ctx.CompleteWithSummary "did the thing"
            })

    host.RegisterHandler(handler)
    host.EmitFileChanged(SourceChanged [ "a.fs" ])
    waitUntil (fun () -> host.GetHistory("summarizer") |> List.isEmpty |> not) 5000
    let r = List.head (host.GetHistory("summarizer"))
    test <@ r.Summary = Some "did the thing" @>

[<Fact(Timeout = 5000)>]
let ``Running to Completed records positive elapsed`` () =
    let host = PluginHost.create nullChecker "/tmp/test"

    let handler = makeCtxAwareHandler "timer" (fun _ctx -> async { do! Async.Sleep 10 })

    host.RegisterHandler(handler)
    host.EmitFileChanged(SourceChanged [ "a.fs" ])
    waitUntil (fun () -> host.GetHistory("timer") |> List.isEmpty |> not) 5000
    let r = List.head (host.GetHistory("timer"))
    test <@ r.Elapsed > TimeSpan.Zero @>

[<Fact(Timeout = 5000)>]
let ``Terminal transition auto-ends open subtasks`` () =
    let host = PluginHost.create nullChecker "/tmp/test"

    let handler =
        makeCtxAwareHandler "leaker" (fun ctx -> async { ctx.StartSubtask "k1" "leaky" })

    host.RegisterHandler(handler)
    host.EmitFileChanged(SourceChanged [ "a.fs" ])
    waitUntil (fun () -> host.GetHistory("leaker") |> List.isEmpty |> not) 5000
    test <@ List.isEmpty (host.GetSubtasks("leaker")) @>
