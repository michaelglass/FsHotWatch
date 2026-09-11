module FsHotWatch.Tests.TestPruneReceiptBoundaryTests

open System
open System.IO
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open FsHotWatch.Events
open FsHotWatch.PluginFramework
open FsHotWatch.TestPrune
open FsHotWatch.TestPrune.TestPrunePlugin
open FsHotWatch.Tests.TestHelpers
open FsHotWatch.Tests.TestPrunePluginTestSupport

let private makeTestPruneRecordingCtx () =
    let statuses = System.Collections.Generic.List<PluginStatus>()

    let ledger =
        System.Collections.Generic.Dictionary<string, FsHotWatch.ErrorLedger.ErrorEntry list>()

    let ctx: FsHotWatch.PluginFramework.PluginCtx<TestPruneMsg> =
        { ReportStatus = fun s -> statuses.Add s
          ReportErrors = fun file entries -> ledger.[file] <- entries
          ClearErrors = fun file -> ledger.Remove(file) |> ignore
          ClearAllErrors = fun () -> ledger.Clear()
          EmitBuildCompleted = fun _ -> ()
          EmitTestRunStarted = fun _ -> ()
          EmitTestProgress = fun _ -> ()
          EmitTestRunCompleted = fun _ -> ()
          EmitCommandCompleted = fun _ -> ()
          Checker = Unchecked.defaultof<_>
          RepoRoot = ""
          Post = fun _ -> ()
          EnqueueExclusiveIntent = fun _ _ _ -> System.Threading.Tasks.Task.FromResult(())
          StartSubtask = fun _ _ -> ()
          UpdateSubtask = fun _ _ -> ()
          EndSubtask = fun _ -> ()
          Log = fun _ -> ()
          CompleteWithTimeout = fun _ -> ()
          RunExclusive = fun _ _ -> FsHotWatch.PluginFramework.Claimed
          RunExclusiveShared = fun _ _ _ _ _ -> FsHotWatch.PluginFramework.SharedClaimed
          IsRunning = fun _ -> false
          FcsSuppressedCodes = Set.empty
          ProjectGraph = FsHotWatch.PluginFramework.ProjectGraphAccessor.none }

    ctx, statuses, ledger

let private projConfig (project: string) : TestConfig =
    { Project = project
      Command = "sh"
      Args = "-c \"exit 0\""
      Group = "default"
      Environment = []
      FilterTemplate = Some "-- --filter-class {classes}"
      ClassJoin = "|"
      TimeoutSec = None
      ReportVerificationFormat = AutoDetect }

let private testsFinishedEvent (results: (string * TestResult) list) (launch: TestRunLaunch) =
    let runId = Guid.NewGuid()

    let started: TestRunStarted =
        { RunId = runId
          StartedAt = DateTime.UtcNow }

    let completed: TestRunCompleted =
        { RunId = runId
          TotalElapsed = TimeSpan.FromSeconds 1.0
          Outcome = Normal
          Results = Map.ofList results
          Verification = RunVerification.ofResults (Map.ofList results) }

    Custom(TestsFinished(started, completed, launch))

let private passed filtered = TestsPassed("one test passed", filtered, TimeSpan.FromSeconds 1.0)

let private bind root (launch: TestRunLaunch) =
    { launch with InputTreeHash = ReceiptInputTree.read root; ModelGeneration = Some 1L }

let private withFixture action =
    withTempDir "owner-boundary" (fun root ->
        Directory.CreateDirectory(Path.Combine(root, "src")) |> ignore
        File.WriteAllText(Path.Combine(root, "src", "Lib.fs"), "module Lib\nlet value = 1\n")
        let recording, _, _ = makeTestPruneRecordingCtx ()
        let scheduled = System.Collections.Generic.Queue<SharedResourceState -> Async<TestPruneMsg>>()
        let ctx =
            { recording with
                RepoRoot = root
                ProjectGraph =
                    { ProjectGraphAccessor.none with
                        ObserveModel =
                            fun () ->
                                FsHotWatch.ProjectModel.ofCompleted 1L
                                    { Discovered = 1; Loaded = 1; OptionsMapped = 1; Registered = 1 } }
                RunExclusiveShared = fun _ _ work _ _ -> scheduled.Enqueue work; SharedClaimed }
        action root ctx scheduled)

let private scope root (ctx: PluginCtx<TestPruneMsg>) (handler: PluginHandler<TestPruneState, TestPruneMsg>) state =
    let command = handler.Commands |> List.find (fst >> (=) "test-scope") |> snd
    let commandCtx: CommandCtx<TestPruneMsg> =
        { RepoRoot = root
          Log = ignore
          Post = ignore
          EnqueueExclusiveIntent = fun _ _ _ -> Task.FromResult(())
          IsRunning = fun _ -> false
          ProjectGraph = ctx.ProjectGraph }
    PluginCommand.invoke command commandCtx state [||]
    |> Async.RunSynchronously
    |> FsHotWatch.Cli.IpcParsing.parseTestRunReport

[<Theory(Timeout = 20000)>]
[<InlineData(false)>]
[<InlineData(true)>]
let ``ordinary build retains full or filtered receipt through actual AlreadyVerified selection`` filtered =
    withFixture (fun root ctx scheduled ->
        let configs = [ projConfig "ProjA"; projConfig "ProjB" ]
        let handler = create ":memory:" root (Some configs) None
                          (Some(fun _ -> failwith "unexpected execution: this fixture must select AlreadyVerified")) None None []
        let update state event = handler.Update ctx state event |> Async.RunSynchronously
        let full = update handler.Init
                       (testsFinishedEvent [ "ProjA", passed false; "ProjB", passed false ]
                           (fullSuiteLaunch [ "ProjA"; "ProjB" ] |> bind root))
        Assert.True(full.Earned.IsSome, "positive control: seeded complete outcomes earned model-bound proof")
        let earned =
            if filtered then
                let selection = { OnlyFailed = false; Projects = Some(Set.singleton "ProjA") }
                let launched = update full (Custom(RunTestsRequested(selection, Some "ProjATests", TaskCompletionSource<string>())))
                update launched (testsFinishedEvent [ "ProjA", passed true ]
                                    (filteredLaunch [ "ProjA", [ "ProjATests" ] ] |> bind root))
            else full
        scheduled.Clear()
        let priorId = earned.EvidenceReceipt.Value.RunId
        let launched = update earned (BuildCompleted BuildSucceeded)
        Assert.Equal(1, scheduled.Count)
        let completion = scheduled.Dequeue() Ready |> Async.RunSynchronously
        match completion with
        | TestsFinished(_, completed, launch) ->
            Assert.Empty completed.Results
            Assert.Equal(NoProjectsSelected, completed.Verification)
            Assert.Equal(ZeroSelection.AlreadyVerified, launch.ZeroSelection)
        | other -> Assert.Fail($"expected actual AlreadyVerified selection, got {other}")
        let final = update launched (Custom completion)
        Assert.Empty final.LastCoverage
        let report = scope root ctx handler final
        Assert.Equal(Some priorId, report.RunId)
        Assert.Equal((if filtered then FsHotWatch.Cli.IpcParsing.ImpactFiltered(1, 2)
                      else FsHotWatch.Cli.IpcParsing.FullSuite 2), report.Scope)
        Assert.Contains(priorId, final.Earned.Value.AuthorizedRunIds))

[<Fact(Timeout = 20000)>]
let ``dependency-only force debt survives invalid artifacts and reaches the next valid selection`` () =
    withFixture (fun root ctx scheduled ->
        let mutable reachedExecutor = false
        let beforeRun _ =
            reachedExecutor <- true
            failwith "fixture stops after actual selection reaches the executor"
        let handler = create ":memory:" root (Some [ projConfig "ProjA" ]) None (Some beforeRun) None None []
        let update state event = handler.Update ctx state event |> Async.RunSynchronously
        let earned = update handler.Init
                        (testsFinishedEvent [ "ProjA", passed false ] (fullSuiteLaunch [ "ProjA" ] |> bind root))
        Assert.True(earned.Earned.IsSome)
        let withDebt = { earned with PendingForceRunProjects = Set.singleton "ProjA" }
        let launched = update withDebt (BuildCompleted BuildSucceeded)
        Assert.Equal(1, scheduled.Count)
        let refusal = scheduled.Dequeue() (Invalid "dependency artifacts unavailable") |> Async.RunSynchronously
        let refused = update launched (Custom refusal)
        Assert.False reachedExecutor
        Assert.True refused.EvidenceReceipt.IsNone
        Assert.True refused.Earned.IsNone
        // A later build with the resource now Ready must select the still-owed project.
        let next = update refused (BuildCompleted BuildSucceeded)
        Assert.Equal(1, scheduled.Count)
        let completion = scheduled.Dequeue() Ready |> Async.RunSynchronously
        match completion with
        | TestsFinished(_, _, _) -> ()
        | other -> Assert.Fail($"expected selection completion, got {other}")
        Assert.True(reachedExecutor, "the invalid resource must not discharge dependency-only force debt")
        update next (Custom completion) |> ignore)
