module FsHotWatch.Tests.ActivityIntegrationTests

open System
open System.Threading
open Xunit
open Swensen.Unquote
open FsHotWatch.Events
open FsHotWatch.PluginFramework
open FsHotWatch.PluginHost
open FsHotWatch.Cli.RunOnceOutput
open FsHotWatch.Cli.IpcOutput
open FsHotWatch.Cli.ProgressRenderer
open FsHotWatch.Tests.TestHelpers

let private nullChecker =
    Unchecked.defaultof<FSharp.Compiler.CodeAnalysis.FSharpChecker>

/// Project the live host state into a ParsedPluginStatus for one plugin so the
/// renderer can consume it exactly like the IPC payload parser would.
let private parsedFor (host: PluginHost) (name: string) : ParsedPluginStatus =
    let status = host.GetStatus(name) |> Option.defaultValue Idle

    let lastRun = host.GetHistory(name) |> List.tryHead

    { Status = status
      Subtasks = host.GetSubtasks(name)
      ActivityTail = host.GetActivityTail(name)
      LastRun = lastRun }

/// A fake plugin that on FileChanged starts 3 subtasks, logs 2 messages,
/// ends the subtasks one by one, and records an explicit summary.
let private makeFakePlugin (duringRun: PluginCtx<unit> -> PluginHost -> unit) (host: PluginHost) =
    { Name = PluginName.create "fake"
      Init = ()
      Update =
        fun ctx state event ->
            async {
                match event with
                | FileChanged _ ->
                    ctx.ReportStatus(Running System.DateTime.UtcNow)
                    ctx.StartSubtask "a" "doing a"
                    ctx.StartSubtask "b" "doing b"
                    ctx.StartSubtask "c" "doing c"
                    ctx.Log "starting batch"
                    ctx.Log "midway through"
                    // Let the test observe the running snapshot.
                    duringRun ctx host
                    do! Async.Sleep 20
                    ctx.EndSubtask "a"
                    do! Async.Sleep 20
                    ctx.EndSubtask "b"
                    do! Async.Sleep 20
                    ctx.EndSubtask "c"
                    ctx.CompleteWithSummary "did 3 things"
                    ctx.ReportStatus(Completed System.DateTime.UtcNow)
                | _ -> ()

                return state
            }
      Commands = []
      Subscriptions = Set.ofList [ SubscribeFileChanged ]
      CacheKey = None
      Teardown = None }

[<Fact>]
let ``running snapshot exposes 3 subtasks and 2 activity lines; final history carries summary`` () =
    let host = PluginHost.create nullChecker "/tmp/test"

    let duringSnapshot: Subtask list ref = ref []
    let duringTail: string list ref = ref []

    let handler =
        makeFakePlugin
            (fun _ctx h ->
                duringSnapshot.Value <- h.GetSubtasks("fake")
                duringTail.Value <- h.GetActivityTail("fake"))
            host

    host.RegisterHandler(handler)
    host.EmitFileChanged(SourceChanged [ "a.fs" ])
    waitUntil (fun () -> host.GetHistory("fake") |> List.isEmpty |> not) 10000

    // Snapshot captured during the running phase.
    test <@ duringSnapshot.Value |> List.length = 3 @>
    test <@ duringTail.Value |> List.length >= 2 @>
    test <@ duringTail.Value |> List.contains "midway through" @>

    // History after completion.
    let history = host.GetHistory("fake")
    test <@ history.Length = 1 @>
    let r = List.head history
    test <@ r.Summary = Some "did 3 things" @>

    match r.Outcome with
    | CompletedRun -> ()
    | other -> failwithf "expected CompletedRun, got %A" other

    // Activity tail is preserved in the history snapshot.
    test <@ r.ActivityTail |> List.contains "starting batch" @>
    test <@ r.ActivityTail |> List.contains "midway through" @>

[<Fact>]
let ``verbose renderer over final payload shows completion line with summary`` () =
    let host = PluginHost.create nullChecker "/tmp/test"
    let handler = makeFakePlugin (fun _ _ -> ()) host
    host.RegisterHandler(handler)
    host.EmitFileChanged(SourceChanged [ "a.fs" ])
    waitUntil (fun () -> host.GetHistory("fake") |> List.isEmpty |> not) 10000

    let parsed = parsedFor host "fake"
    let lines = renderPlugin Verbose DateTime.UtcNow "fake" parsed
    let joined = String.concat "\n" lines
    test <@ joined.Contains "fake" @>
    test <@ joined.Contains "did 3 things" @>

[<Fact>]
let ``renderer during running phase shows 3 subtasks in verbose mode`` () =
    let host = PluginHost.create nullChecker "/tmp/test"

    // Block in the middle of the run so we can render while it's in flight.
    let gate = new ManualResetEventSlim(false)
    let captured: string list ref = ref []

    let handler =
        { Name = PluginName.create "slow"
          Init = ()
          Update =
            fun ctx state event ->
                async {
                    match event with
                    | FileChanged _ ->
                        ctx.ReportStatus(Running System.DateTime.UtcNow)
                        ctx.StartSubtask "a" "doing a"
                        ctx.StartSubtask "b" "doing b"
                        ctx.StartSubtask "c" "doing c"
                        ctx.Log "queued 3"
                        // Wait until the test has rendered the running snapshot.
                        gate.Wait(10000) |> ignore
                        ctx.EndSubtask "a"
                        ctx.EndSubtask "b"
                        ctx.EndSubtask "c"
                        ctx.ReportStatus(Completed System.DateTime.UtcNow)
                    | _ -> ()

                    return state
                }
          Commands = []
          Subscriptions = Set.ofList [ SubscribeFileChanged ]
          CacheKey = None
          Teardown = None }

    host.RegisterHandler(handler)
    host.EmitFileChanged(SourceChanged [ "a.fs" ])
    // Wait until subtasks appear.
    waitUntil (fun () -> host.GetSubtasks("slow") |> List.length = 3) 5000
    let parsed = parsedFor host "slow"
    let lines = renderPlugin Verbose DateTime.UtcNow "slow" parsed
    captured.Value <- lines
    gate.Set()
    waitUntil (fun () -> host.GetHistory("slow") |> List.isEmpty |> not) 10000

    let joined = String.concat "\n" captured.Value
    test <@ joined.Contains "3 running" @>
    test <@ joined.Contains "a" && joined.Contains "b" && joined.Contains "c" @>
    // Tree glyphs
    test <@ joined.Contains "\u251c\u2500" || joined.Contains "\u2514\u2500" @>
