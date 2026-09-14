module FsHotWatch.Tests.TestPruneCheckReachSeamTests

open System
open System.IO
open System.Text.Json
open Xunit
open Swensen.Unquote
open FsHotWatch.Events
open FsHotWatch.PluginHost
open FsHotWatch.TestPrune.TestPrunePlugin
open FsHotWatch.Tests.TestHelpers
open FsHotWatch.Tests.TestPrunePluginTestSupport
open TestPrune.Database

let private markerRunner (project: string) (marker: string) : TestConfig =
    { Project = project
      Command = "sh"
      Args = $"-c \"touch '{marker}'; exit 0\""
      Group = "default"
      Environment = []
      FilterTemplate = None
      ClassJoin = " "
      TimeoutSec = None
      ReportVerificationFormat = AutoDetect }

let private seamTree (tmpDir: string) =
    let dbPath = Path.Combine(tmpDir, "tp.db")
    let database = Database.create dbPath
    PendingQueueHelpers.seedCoveredSymbol database "Lib.foo" "Lib.fs" "P1" "P1Tests" "fooTest"
    PendingQueueHelpers.seedCoveredSymbol database "Lib.debt" "Debt.fs" "P2" "P2Tests" "debtTest"
    FsHotWatch.TestPrune.PendingVerification.save tmpDir (Set.ofList [ "Lib.foo" ])
    seedBaseline tmpDir [ "P1"; "P2" ]
    let firstMarker = Path.Combine(tmpDir, "p1-ran")
    let secondMarker = Path.Combine(tmpDir, "p2-ran")
    let configs = [ markerRunner "P1" firstMarker; markerRunner "P2" secondMarker ]
    let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
    host.RegisterHandler(create dbPath tmpDir (Some configs) None None None None [])
    host, firstMarker, secondMarker

let private runToCompletion (host: PluginHost) =
    let terminal = beginAwaitNextTerminal host "test-prune"
    host.EmitBuildCompleted(BuildSucceeded)
    Assert.True(terminal.Wait(TimeSpan.FromSeconds 20.0), "test-prune never reached a terminal status")
    waitForQuiescent host 5000

    match host.GetStatus("test-prune") with
    | Some(Completed _) -> ()
    | other -> Assert.Fail($"expected successful test-prune completion, got %A{other}")

let private checkReachReply (host: PluginHost) =
    match host.RunCommand("check-reach", [||]) |> Async.RunSynchronously with
    | Some reply -> JsonDocument.Parse(reply)
    | None -> failwith "the plugin serves no check-reach command"

[<Fact(Timeout = 30000)>]
let ``full-suite completion serves the narrower retained impact selection`` () =
    withTempDir "tp-check-reach-full" (fun tmpDir ->
        let host, firstMarker, secondMarker = seamTree tmpDir

        let setScope =
            host.RunCommand("set-scope", [| """{"scope":"full"}""" |])
            |> Async.RunSynchronously

        test <@ setScope.IsSome && setScope.Value.Contains "\"scope\":\"full\"" @>
        runToCompletion host
        test <@ File.Exists firstMarker @>
        test <@ File.Exists secondMarker @>
        use reply = checkReachReply host
        let root = reply.RootElement
        Assert.True(root.GetProperty("recorded").GetBoolean())
        Assert.Equal("filtered", root.GetProperty("scope").GetString())
        Assert.Equal(1, root.GetProperty("ranProjects").GetInt32())
        Assert.Equal(2, root.GetProperty("totalProjects").GetInt32())
        Assert.Equal("no-failures-to-reach", root.GetProperty("reach").GetString()))

[<Fact(Timeout = 30000)>]
let ``impact completion reports absent comparison instead of inventing a selection`` () =
    withTempDir "tp-check-reach-impact" (fun tmpDir ->
        let host, firstMarker, secondMarker = seamTree tmpDir
        runToCompletion host
        test <@ File.Exists firstMarker @>
        test <@ not (File.Exists secondMarker) @>
        use reply = checkReachReply host
        let root = reply.RootElement
        Assert.True(root.GetProperty("recorded").GetBoolean())
        Assert.Equal(JsonValueKind.Null, root.GetProperty("scope").ValueKind)
        Assert.Equal("unknown", root.GetProperty("reach").GetString())

        let expectedReason =
            "this run carries no retained impact selection (a forced re-run, an aborted run, or a skip), so there is nothing to project the result through"

        Assert.Equal(expectedReason, root.GetProperty("reason").GetString()))

[<Fact>]
let ``class-filter refusal preserves readable word spacing`` () =
    let selection =
        Map.ofList [ "Alpha.Tests", ProjectClasses(Set.ofList [ "Alpha.OneTests" ]) ]

    let failure: OutstandingFailure =
        { Project = "Alpha.Tests"
          Class = None
          Method = None
          File = "tests/Alpha.fs"
          Entry = FsHotWatch.ErrorLedger.ErrorEntry.error "Alpha.Tests timed out" }

    match CheckReach.classify (Some selection) [ failure ] with
    | ReachUnknown reason ->
        let expectedReason =
            "the Alpha.Tests red names no test class (a timeout, an errored host, or unparseable failure output) and the retained selection runs that project under a CLASS filter, so whether `check` would have executed it cannot be decided"

        test <@ reason = expectedReason @>
    | other -> Assert.Fail($"expected an undecidable class-filter projection, got %A{other}")
