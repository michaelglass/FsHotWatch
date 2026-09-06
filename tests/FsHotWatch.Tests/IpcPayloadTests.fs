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

let private rpcConfigWithUnchecked (unchecked: int) (host: PluginHost) : DaemonRpcConfig =
    { Host = host
      RequestShutdown = ignore
      RequestScan = ignore
      GetScanStatus = fun () -> "idle"
      GetScanGeneration = fun () -> 0L
      TriggerBuild = fun () -> async { return () }
      FormatAll = fun () -> async { return "" }
      WaitForScanGeneration = fun _ -> Task.FromResult(())
      WaitForAllTerminal = fun _ -> Task.FromResult(())
      RerunPlugin = fun _ -> async { return Result.Ok() }
      InvalidateCache = fun () -> Task.FromResult(())
      GetUncheckedCount = fun () -> unchecked }

let private defaultRpcConfig (host: PluginHost) : DaemonRpcConfig = rpcConfigWithUnchecked 0 host

let private completedHandlerWith (name: string) (summary: string) (action: PluginCtx<unit> -> Async<unit>) =
    { Name = PluginName.create name
      Init = ()
      Update =
        fun ctx state event ->
            async {
                match event with
                | FileChanged _ ->
                    ctx.ReportStatus(Running DateTime.UtcNow)
                    do! action ctx

                    ctx.ReportStatus(Completed(DateTime.UtcNow, RunVerdict.create summary TimeSpan.Zero))
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
                    ctx.ReportStatus(PluginStatus.failedNow err err TimeSpan.Zero)
                | _ -> ()

                return state
            }
      Commands = []
      Subscriptions = Set.ofList [ SubscribeFileChanged ]
      CacheKey = None
      Teardown = None }

[<Fact(Timeout = 15000)>]
let ``GetStatus payload round-trips completed run with subtasks and activity`` () =
    let host = PluginHost.create nullChecker "/tmp/test"

    let handler =
        completedHandlerWith "worker" "did 3 things" (fun ctx ->
            async {
                ctx.StartSubtask "p1" "project A"
                ctx.Log "line one"
                ctx.Log "line two"
                ctx.Log "line three"
                ctx.EndSubtask "p1"
            })

    host.RegisterHandler(handler)
    host.EmitFileChanged(SourceChanged [ "a.fs" ])
    waitUntil (fun () -> host.GetHistory("worker") |> List.isEmpty |> not) 12000

    let target = DaemonRpcTarget(defaultRpcConfig host)
    let json = target.GetStatus()
    let parsed = FsHotWatch.Tests.TestHelpers.parseStatuses json

    test <@ parsed.ContainsKey("worker") @>
    let w = parsed.["worker"]

    match w.Status with
    | StatusView.Completed _ -> ()
    | other -> failwithf "expected Completed, got %A" other

    test <@ w.LastRun.IsSome @>
    let run = w.LastRun.Value
    test <@ run.Outcome = CompletedRun @>
    test <@ run.Summary = Some "did 3 things" @>
    test <@ run.ActivityTail = [ "line one"; "line two"; "line three" ] @>
    test <@ run.Elapsed >= TimeSpan.Zero @>

let private verifiedNothingHandler (name: string) (detail: string) =
    { Name = PluginName.create name
      Init = ()
      Update =
        fun ctx state event ->
            async {
                match event with
                | FileChanged _ ->
                    ctx.ReportStatus(Running DateTime.UtcNow)
                    ctx.ReportStatus(PluginStatus.verifiedNothingNow detail TimeSpan.Zero)
                | _ -> ()

                return state
            }
      Commands = []
      Subscriptions = Set.ofList [ SubscribeFileChanged ]
      CacheKey = None
      Teardown = None }

[<Fact(Timeout = 15000)>]
let ``AUTOMATION-339: a verified-nothing run reaches the CLI as the VerifiedNothing case, status still Completed`` () =
    // The whole hop the ticket is about: plugin verdict → host run record → status
    // payload → CLI parser. The status stays `Completed` (so `check` keeps its exit 3
    // rather than an exit 1), and the run record's outcome is the case, not a prefix
    // some reader has to find in the summary.
    let host = PluginHost.create nullChecker "/tmp/test"
    host.RegisterHandler(verifiedNothingHandler "empty" "0 test project(s) ran, no test executed")
    host.RegisterHandler(completedHandlerWith "control" "6 passed, 0 failed in 6 projects" (fun _ -> async.Return()))
    host.EmitFileChanged(SourceChanged [ "a.fs" ])
    waitUntil (fun () -> host.GetHistory("empty") |> List.isEmpty |> not) 12000
    waitUntil (fun () -> host.GetHistory("control") |> List.isEmpty |> not) 12000

    let target = DaemonRpcTarget(defaultRpcConfig host)
    let parsed = FsHotWatch.Tests.TestHelpers.parseStatuses (target.GetStatus())

    let empty = parsed.["empty"]

    match empty.Status with
    | StatusView.Completed _ -> ()
    | other -> failwithf "expected Completed, got %A" other

    test <@ empty.LastRun.Value.Outcome = VerifiedNothing "0 test project(s) ran, no test executed" @>
    test <@ empty.LastRun.Value.Summary = Some "NOTHING VERIFIED: 0 test project(s) ran, no test executed" @>
    test <@ ParsedPluginStatus.verifiedNothing empty @>

    // Positive control on the same wire: a run that verified something is `CompletedRun`.
    let control = parsed.["control"]
    test <@ control.LastRun.Value.Outcome = CompletedRun @>
    test <@ not (ParsedPluginStatus.verifiedNothing control) @>

[<Fact(Timeout = 15000)>]
let ``GetStatus payload preserves multi-line failure error`` () =
    let host = PluginHost.create nullChecker "/tmp/test"
    let multiline = "first line of error\nsecond line\nthird line with detail"
    host.RegisterHandler(failingHandler "breaker" multiline)
    host.EmitFileChanged(SourceChanged [ "a.fs" ])
    waitUntil (fun () -> host.GetHistory("breaker") |> List.isEmpty |> not) 12000

    let target = DaemonRpcTarget(defaultRpcConfig host)
    let json = target.GetStatus()
    let parsed = FsHotWatch.Tests.TestHelpers.parseStatuses json

    let b = parsed.["breaker"]

    match b.Status with
    | StatusView.Failed(msg, _) -> test <@ msg = multiline @>
    | other -> failwithf "expected Failed, got %A" other

    test <@ b.LastRun.IsSome @>
    let run = b.LastRun.Value

    match run.Outcome with
    | FailedRun err -> test <@ err = multiline @>
    | other -> failwithf "expected FailedRun, got %A" other

[<Fact(Timeout = 20000)>]
let ``GetDiagnostics payload exposes structured per-plugin statuses`` () =
    let host = PluginHost.create nullChecker "/tmp/test"

    let handler =
        completedHandlerWith "diag" "ok" (fun ctx -> async { ctx.Log "hello" })

    host.RegisterHandler(handler)
    host.EmitFileChanged(SourceChanged [ "a.fs" ])
    waitUntil (fun () -> host.GetHistory("diag") |> List.isEmpty |> not) 12000

    let target = DaemonRpcTarget(defaultRpcConfig host)
    let json = target.GetDiagnostics("")
    let resp = parseDiagnosticsResponse json

    test <@ resp.Statuses.ContainsKey("diag") @>
    let d = resp.Statuses.["diag"]
    test <@ d.LastRun.IsSome @>
    test <@ d.LastRun.Value.Summary = Some "ok" @>
    test <@ d.LastRun.Value.ActivityTail = [ "hello" ] @>

[<Fact(Timeout = 15000)>]
let ``GetDiagnostics payload carries unchecked count -> Complete coverage`` () =
    let host = PluginHost.create nullChecker "/tmp/test"
    let target = DaemonRpcTarget(rpcConfigWithUnchecked 0 host)
    let json = target.GetDiagnostics("")
    let resp = parseDiagnosticsResponse json
    test <@ resp.Coverage = Complete @>

[<Fact(Timeout = 15000)>]
let ``GetDiagnostics payload carries nonzero unchecked count -> Incomplete coverage`` () =
    let host = PluginHost.create nullChecker "/tmp/test"
    let target = DaemonRpcTarget(rpcConfigWithUnchecked 4 host)
    let json = target.GetDiagnostics("")
    let resp = parseDiagnosticsResponse json
    test <@ resp.Coverage = Incomplete 4 @>

// ---------------------------------------------------------------------------
// AUTOMATION-747 — the diagnostics reply is SIZE-BOUNDED.
//
// The gate this ticket came from died at its very last IPC call, six seconds after the
// daemon had logged `Tests complete: 7 projects, 117.1s` and `WaitForComplete()
// resolved`. The reply it died building was this one: the test-prune plugin attached the
// whole captured project output to EVERY parsed per-test failure, so a project with 753
// failing tests and a 48 MB capture (measured: `Intelligence.Tests.Integration.output.log`
// = 50,660,713 bytes) asks this method for ~36 GB of JSON in a single string. Seven
// finished runs in a row were discarded that way.
//
// The tests below pin the two halves of the bound separately, because they fail for
// different reasons and a fix to either one alone still leaves the gate killable:
// the growth LAW (a reply may not grow with failures × output) and the response
// BUDGET (a plugin that ignores the law still cannot produce an unbounded reply).
// ---------------------------------------------------------------------------

/// One ledger entry per "failing test", all sharing ONE detail string — exactly the
/// shape `TestPrunePlugin.failuresOf` produced. Sharing the reference is the point:
/// it costs one allocation to HOLD and `entries × |detail|` to SERIALIZE, which is
/// why this defect is invisible in the daemon's own memory and fatal on the wire.
let private ledgerOfSharedDetail (entries: int) (detailChars: int) : PluginHost =
    let host = PluginHost.create nullChecker "/tmp/test"
    let shared = String.replicate detailChars "x"

    for i in 1..entries do
        host.ReportErrors(
            "test-prune",
            $"tests/Suite%d{i}.fs",
            [ FsHotWatch.ErrorLedger.ErrorEntry.errorWithDetail $"failed Suite%d{i}.the test (0ms)" shared ]
        )

    waitUntil (fun () -> host.GetErrors() |> Map.count = entries) 12000
    host

[<Fact(Timeout = 60000)>]
let ``GetDiagnostics does not grow with failures times output — the reply is a sum, not a product`` () =
    // The LAW. Doubling the failing entries over the same captured output must not
    // double the reply: that product is the whole defect, and it is what makes a reply
    // reach gigabytes from a ledger holding one string.
    let detailChars = 50_000

    let replyChars (entries: int) =
        let host = ledgerOfSharedDetail entries detailChars
        (DaemonRpcTarget(defaultRpcConfig host)).GetDiagnostics("").Length

    let small = replyChars 100
    let large = replyChars 200

    // Every entry still travels — the count is what the exit code is computed from.
    test
        <@
            (parseDiagnosticsResponse (
                (DaemonRpcTarget(defaultRpcConfig (ledgerOfSharedDetail 200 detailChars))).GetDiagnostics("")
            ))
                .Files.Count = 200
        @>

    // Growth is bounded by the per-entry MESSAGE text, not by the shared output. A
    // couple of hundred characters an entry, not fifty thousand.
    test <@ large - small < 100 * 1_000 @>
    // And before the bound existed, `small` alone was 100 × 50,000 = 5,000,000 chars.
    test <@ small < 1_000_000 @>

[<Fact(Timeout = 60000)>]
let ``GetDiagnostics caps the whole response's detail, and says so rather than dropping it silently`` () =
    // The BUDGET. A plugin that still attaches a large detail per entry cannot make this
    // reply unbounded — but the entries it cost are NAMED, because "detail: null" and
    // "detail: elided" are different facts and only one of them means "go and look".
    let host = ledgerOfSharedDetail 200 50_000
    let json = (DaemonRpcTarget(defaultRpcConfig host)).GetDiagnostics("")

    let details =
        (parseDiagnosticsResponse json).Files
        |> Map.toList
        |> List.collect (fun (_, entries) -> entries |> List.choose (fun e -> e.Detail))

    test <@ details.Length = 200 @>

    let marked =
        details
        |> List.filter (fun d -> d = FsHotWatch.ErrorLedger.Transport.DetailBudgetSpentMarker)

    // Some entries carried their (truncated) text; the rest say why they did not.
    test <@ not marked.IsEmpty @>
    test <@ marked.Length < 200 @>

    // Every carried detail respects the per-field cap.
    test
        <@
            details
            |> List.forall (fun d -> d.Length <= FsHotWatch.ErrorLedger.Transport.MaxFieldChars + 64)
        @>
