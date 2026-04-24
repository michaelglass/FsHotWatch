module FsHotWatch.Tests.IpcPayloadTests

open System
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open FsHotWatch.Events
open FsHotWatch.Ipc
open FsHotWatch.PluginHost
open FsHotWatch.PluginFramework
open FsHotWatch.Cli.RunOnceOutput
open FsHotWatch.Cli.IpcParsing
open FsHotWatch.Tests.TestHelpers

let private nullChecker =
    Unchecked.defaultof<FSharp.Compiler.CodeAnalysis.FSharpChecker>

let private defaultRpcConfig (host: PluginHost) : DaemonRpcConfig =
    { Host = host
      RequestShutdown = ignore
      RequestScan = ignore
      GetScanStatus = fun () -> "idle"
      GetScanGeneration = fun () -> 0L
      TriggerBuild = fun () -> async { return () }
      FormatAll = fun () -> async { return "" }
      WaitForScanGeneration = fun _ -> Task.FromResult(())
      WaitForAllTerminal = fun _ -> Task.FromResult(())
      RerunPlugin = fun _ -> async { return "" } }

let private completedHandler (name: string) (action: PluginCtx<unit> -> Async<unit>) =
    { Name = PluginName.create name
      Init = ()
      Update =
        fun ctx state event ->
            async {
                match event with
                | FileChanged _ ->
                    ctx.ReportStatus(Running DateTime.UtcNow)
                    do! action ctx
                    ctx.ReportStatus(Completed DateTime.UtcNow)
                | _ -> ()

                return state
            }
      Commands = []
      Subscriptions = Set.ofList [ SubscribeFileChanged ]
      CacheKey = None
      Teardown = None }

let private failingHandler (name: string) (err: string) =
    { Name = PluginName.create name
      Init = ()
      Update =
        fun ctx state event ->
            async {
                match event with
                | FileChanged _ ->
                    ctx.ReportStatus(Running DateTime.UtcNow)
                    ctx.Log "starting work"
                    ctx.ReportStatus(Failed(err, DateTime.UtcNow))
                | _ -> ()

                return state
            }
      Commands = []
      Subscriptions = Set.ofList [ SubscribeFileChanged ]
      CacheKey = None
      Teardown = None }

[<Fact(Timeout = 5000)>]
let ``GetStatus payload round-trips completed run with subtasks and activity`` () =
    let host = PluginHost.create nullChecker "/tmp/test"

    let handler =
        completedHandler "worker" (fun ctx ->
            async {
                ctx.StartSubtask "p1" "project A"
                ctx.Log "line one"
                ctx.Log "line two"
                ctx.Log "line three"
                ctx.EndSubtask "p1"
                ctx.CompleteWithSummary "did 3 things"
            })

    host.RegisterHandler(handler)
    host.EmitFileChanged(SourceChanged [ "a.fs" ])
    waitUntil (fun () -> host.GetHistory("worker") |> List.isEmpty |> not) 5000

    let target = DaemonRpcTarget(defaultRpcConfig host)
    let json = target.GetStatus()
    let parsed = parsePluginStatuses json

    test <@ parsed.ContainsKey("worker") @>
    let w = parsed.["worker"]

    match w.Status with
    | Completed _ -> ()
    | other -> failwithf "expected Completed, got %A" other

    test <@ w.LastRun.IsSome @>
    let run = w.LastRun.Value
    test <@ run.Outcome = CompletedRun @>
    test <@ run.Summary = Some "did 3 things" @>
    test <@ run.ActivityTail = [ "line one"; "line two"; "line three" ] @>
    test <@ run.Elapsed >= TimeSpan.Zero @>

[<Fact(Timeout = 5000)>]
let ``GetStatus payload preserves multi-line failure error`` () =
    let host = PluginHost.create nullChecker "/tmp/test"
    let multiline = "first line of error\nsecond line\nthird line with detail"
    host.RegisterHandler(failingHandler "breaker" multiline)
    host.EmitFileChanged(SourceChanged [ "a.fs" ])
    waitUntil (fun () -> host.GetHistory("breaker") |> List.isEmpty |> not) 5000

    let target = DaemonRpcTarget(defaultRpcConfig host)
    let json = target.GetStatus()
    let parsed = parsePluginStatuses json

    let b = parsed.["breaker"]

    match b.Status with
    | Failed(msg, _) -> test <@ msg = multiline @>
    | other -> failwithf "expected Failed, got %A" other

    test <@ b.LastRun.IsSome @>
    let run = b.LastRun.Value

    match run.Outcome with
    | FailedRun err -> test <@ err = multiline @>
    | other -> failwithf "expected FailedRun, got %A" other

[<Fact(Timeout = 10000)>]
let ``GetDiagnostics payload exposes structured per-plugin statuses`` () =
    let host = PluginHost.create nullChecker "/tmp/test"

    let handler =
        completedHandler "diag" (fun ctx ->
            async {
                ctx.Log "hello"
                ctx.CompleteWithSummary "ok"
            })

    host.RegisterHandler(handler)
    host.EmitFileChanged(SourceChanged [ "a.fs" ])
    waitUntil (fun () -> host.GetHistory("diag") |> List.isEmpty |> not) 5000

    let target = DaemonRpcTarget(defaultRpcConfig host)
    let json = target.GetDiagnostics("")
    let resp = parseDiagnosticsResponse json

    test <@ resp.Statuses.ContainsKey("diag") @>
    let d = resp.Statuses.["diag"]
    test <@ d.LastRun.IsSome @>
    test <@ d.LastRun.Value.Summary = Some "ok" @>
    test <@ d.LastRun.Value.ActivityTail = [ "hello" ] @>
