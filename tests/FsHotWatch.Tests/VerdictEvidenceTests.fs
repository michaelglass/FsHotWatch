module FsHotWatch.Tests.VerdictEvidenceTests

open System
open System.Threading
open System.Threading.Tasks
open Xunit
open FsHotWatch.Events
open FsHotWatch.PluginFramework
open FsHotWatch.PluginHost
open FsHotWatch.Daemon

[<Fact(Timeout = 15000)>]
[<Trait("SnapshotEvidence", "ReportedStatusCannotMintVerdict")>]
let ``a reported terminal status cannot mint evidence after its event drains`` () =
    let checker = Unchecked.defaultof<FSharp.Compiler.CodeAnalysis.FSharpChecker>
    let host = PluginHost.create checker "/tmp/fshw-unearned-verdict"

    let reported =
        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    host.OnStatusChanged.Add(fun (name, status) ->
        match name, status with
        | "test-prune", Completed _ -> reported.TrySetResult(()) |> ignore
        | _ -> ())

    let handler: PluginHandler<unit, unit> =
        { Name = PluginName.create "test-prune"
          Init = ()
          Update =
            fun ctx state _ ->
                async {
                    // This UI claim carries no executed results, launch identity,
                    // coverage or pending-obligation evidence. It must remain legal
                    // to report progress without granting the ability to mint green.
                    ctx.ReportStatus(Completed(DateTime.UtcNow, RunVerdict.create "claimed success" TimeSpan.Zero))
                    return state
                }
          Commands = []
          Subscriptions = Set.singleton SubscribeFileChanged
          CacheKey = None
          PrepareCommit = None
          Teardown = None }

    host.RegisterHandler handler
    host.EmitFileChanged(SourceChanged [ "Unverified.fs" ])
    reported.Task.WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult()
    Assert.True(SpinWait.SpinUntil((fun () -> host.CompletedDispatches() = 1L), TimeSpan.FromSeconds 5.0))
    Assert.False(host.AnyPluginBusy(), "the accepted event has actually drained")

    Assert.True(
        host.GetAllStatuses()
        |> Map.exists (fun _ status -> PluginStatus.isTerminal status),
        "positive control: the UI terminal claim was published"
    )

    let verdict = waitForVerdict host (TimeSpan.FromSeconds 1.0) CancellationToken.None

    let winner =
        Task.WhenAny([| verdict :> Task; Task.Delay(TimeSpan.FromSeconds 5.0) |]).GetAwaiter().GetResult()

    Assert.True(obj.ReferenceEquals(verdict, winner), "the original verdict wait must settle, not just its observer")

    Assert.Throws<TimeoutException>(fun () -> verdict.GetAwaiter().GetResult())
    |> ignore

let private completed runId results =
    { RunId = runId
      TotalElapsed = TimeSpan.Zero
      Outcome = Normal
      Results = results
      Verification = RunVerification.ofResults results }

let private fullRun runId =
    completed runId (Map.ofList [ "Tests.fsproj", TestsPassed("one passed", false, TimeSpan.Zero) ])

let private earn launchGeneration currentGeneration pending baseline completion =
    EarnedEvidence.fromCompletion
        completion.RunId
        launchGeneration
        currentGeneration
        (Set.singleton "Tests.fsproj")
        pending
        baseline
        completion

[<Fact>]
let ``only actual current completion with discharged obligations earns evidence`` () =
    let result = fullRun (Guid.NewGuid())
    let proof = earn (Some 3L) (Some 3L) 0 None result |> Option.get
    Assert.Equal(result.RunId, proof.RunId)
    Assert.Empty proof.FailureReasons
    Assert.True((earn None (Some 3L) 0 None result).IsNone)
    Assert.True((earn (Some 2L) (Some 3L) 0 None result).IsNone)
    Assert.NotEmpty((earn (Some 3L) (Some 3L) 1 None result |> Option.get).FailureReasons)
    Assert.True(
        (EarnedEvidence.fromCompletion
            (Guid.NewGuid())
            (Some 3L)
            (Some 3L)
            (Set.singleton "Tests.fsproj")
            0
            None
            result).IsNone
    )

[<Fact>]
let ``filtered completion requires the same model whole-project baseline`` () =
    let baseline = fullRun (Guid.NewGuid()) |> earn (Some 3L) (Some 3L) 0 None |> Option.get
    let filtered =
        completed (Guid.NewGuid()) (Map.ofList [ "Tests.fsproj", TestsPassed("one passed", true, TimeSpan.Zero) ])

    let withoutBaseline = earn (Some 3L) (Some 3L) 0 None filtered |> Option.get
    Assert.NotEmpty withoutBaseline.FailureReasons
    let withBaseline = earn (Some 3L) (Some 3L) 0 (Some baseline) filtered |> Option.get
    Assert.Empty withBaseline.FailureReasons
    let staleBaseline = earn (Some 4L) (Some 4L) 0 (Some baseline) filtered |> Option.get
    Assert.NotEmpty staleBaseline.FailureReasons

[<Fact>]
let ``missing and errored outcomes remain refusal evidence`` () =
    let mixed =
        completed
            (Guid.NewGuid())
            (Map.ofList
                [ "Tests.fsproj", TestsPassed("passed", false, TimeSpan.Zero)
                  "Integration.fsproj", TestsErrored "host exited without a report" ])
    let proof = earn (Some 1L) (Some 1L) 0 None mixed |> Option.get
    Assert.Contains(proof.FailureReasons, fun reason -> reason.Contains "Integration.fsproj")
    let missing = completed (Guid.NewGuid()) Map.empty
    Assert.NotEmpty((earn (Some 1L) (Some 1L) 0 None missing |> Option.get).FailureReasons)

[<NoEquality; NoComparison>]
type private EvidenceDomain =
    { Proof: EarnedEvidence option }
    interface IEarnedEvidenceState with
        member this.EarnedEvidence = this.Proof

[<Fact>]
let ``evidence and event retirement share the same immutable publication`` () =
    let store = FsHotWatch.PluginWorkOwner.Store()
    let owner = FsHotWatch.PluginWorkOwner.Owner({ Proof = None }, store, "tests")
    let identity = owner.AdmitEvent()
    let before = store.Snapshot
    let proof = fullRun (Guid.NewGuid()) |> earn (Some 1L) (Some 1L) 0 None
    owner.CommitEvent(identity, { Proof = proof })
    let after = store.Snapshot
    Assert.True before.IsBusy
    Assert.Empty before.Evidence
    Assert.False after.IsBusy
    Assert.Single after.Evidence |> ignore
    Assert.True before.IsBusy
    Assert.Empty before.Evidence

[<Fact>]
let ``client observation inhibits idle exit without keeping observed work busy`` () =
    let store = FsHotWatch.PluginWorkOwner.Store()
    let before = store.Snapshot
    let lease = store.Observe()
    let observed = store.Snapshot
    Assert.Equal(0, before.ObserverCount)
    Assert.Equal(1, observed.ObserverCount)
    Assert.False observed.IsBusy
    lease.Dispose()
    lease.Dispose()
    Assert.Equal(0, store.Snapshot.ObserverCount)
    Assert.Equal(1, observed.ObserverCount)

[<Fact>]
let ``accountable red full suite can support filtered recovery without laundering remaining debt`` () =
    let red =
        completed (Guid.NewGuid()) (Map.ofList [ "Tests.fsproj", TestsFailed("one failed", false, TimeSpan.Zero) ])
        |> earn (Some 1L) (Some 1L) 1 None |> Option.get
    let rerun =
        completed (Guid.NewGuid()) (Map.ofList [ "Tests.fsproj", TestsPassed("fixed test passed", true, TimeSpan.Zero) ])
    Assert.NotEmpty red.FailureReasons
    Assert.Empty((earn (Some 1L) (Some 1L) 0 (Some red) rerun |> Option.get).FailureReasons)
    Assert.NotEmpty((earn (Some 1L) (Some 1L) 1 (Some red) rerun |> Option.get).FailureReasons)

[<Fact>]
let ``retained receipt requires equal input and keeps the new refusal`` () =
    let previous = fullRun (Guid.NewGuid()) |> earn (Some 1L) (Some 1L) 0 None |> Option.get
    let failed =
        completed (Guid.NewGuid()) (Map.ofList [ "Tests.fsproj", TestsFailed("new failure", true, TimeSpan.Zero) ])
        |> earn (Some 1L) (Some 1L) 1 (Some previous) |> Option.get
    let retained =
        EarnedEvidence.authorizeSameInputReceipt previous.RunId (Some "tree-a") (Some "tree-a") (Some previous) failed
    Assert.Contains(previous.RunId, retained.AuthorizedRunIds)
    Assert.NotEmpty retained.FailureReasons
    let changed =
        EarnedEvidence.authorizeSameInputReceipt previous.RunId (Some "tree-a") (Some "tree-b") (Some previous) failed
    Assert.DoesNotContain(previous.RunId, changed.AuthorizedRunIds)

[<Theory>]
[<InlineData("aborted", "run aborted")>]
[<InlineData("no-obligations", "no project obligations")>]
[<InlineData("unexpected-failure", "Unexpected.fsproj")>]
[<InlineData("unexpected-timeout", "Unexpected.fsproj")>]
[<InlineData("no-match", "no tests verified")>]
let ``actual completion preserves every independent refusal reason`` kind expected =
    let result =
        match kind with
        | "unexpected-failure" -> TestsFailed("failure", false, TimeSpan.Zero)
        | "unexpected-timeout" -> TestsTimedOut("timed out", TimeSpan.FromSeconds 1.0, false, TimeSpan.Zero)
        | "no-match" -> TestsNoMatch("no match", TimeSpan.Zero)
        | _ -> TestsPassed("passed", false, TimeSpan.Zero)
    let project = if kind.StartsWith("unexpected-") then "Unexpected.fsproj" else "Tests.fsproj"
    let completion = completed (Guid.NewGuid()) (Map.ofList [ project, result ])
    let completion = if kind = "aborted" then { completion with Outcome = Aborted "shutdown" } else completion
    let expectedProjects = if kind = "no-obligations" then Set.empty else Set.singleton "Tests.fsproj"
    let proof =
        EarnedEvidence.fromCompletion completion.RunId (Some 1L) (Some 1L) expectedProjects 0 None completion
        |> Option.get
    Assert.Contains(proof.FailureReasons, fun reason -> reason.Contains expected)

[<Fact>]
let ``same-model baseline accounts untouched and no-match siblings without hiding current failures`` () =
    let expected = Set.ofList [ "Tests.fsproj"; "Sibling.fsproj" ]
    let baselineCompletion =
        completed (Guid.NewGuid())
            (Map.ofList [ "Tests.fsproj", TestsPassed("passed", false, TimeSpan.Zero)
                          "Sibling.fsproj", TestsPassed("passed", false, TimeSpan.Zero) ])
    let mint baseline completion =
        EarnedEvidence.fromCompletion completion.RunId (Some 1L) (Some 1L) expected 0 baseline completion
        |> Option.get
    let baseline = mint None baselineCompletion
    for sibling in [ None; Some(TestsNoMatch("filter found no matching tests", TimeSpan.Zero)) ] do
        let current =
            Map.ofList [ "Tests.fsproj", TestsPassed("passed", true, TimeSpan.Zero) ]
            |> fun results ->
                match sibling with
                | None -> results
                | Some result -> Map.add "Sibling.fsproj" result results
            |> completed (Guid.NewGuid())
        Assert.Empty((mint (Some baseline) current).FailureReasons)
        Assert.NotEmpty((mint None current).FailureReasons)
    let refused =
        completed (Guid.NewGuid())
            (Map.ofList [ "Tests.fsproj", TestsPassed("passed", true, TimeSpan.Zero)
                          "Sibling.fsproj", TestsDeferred "input changed" ])
        |> mint (Some baseline)
    Assert.Contains(refused.FailureReasons, fun reason -> reason.Contains "input changed")
