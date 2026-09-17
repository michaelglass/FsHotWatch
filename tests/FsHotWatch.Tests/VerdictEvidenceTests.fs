module FsHotWatch.Tests.VerdictEvidenceTests

open System
open Xunit
open FsHotWatch.Events

let private completed runId results =
    { RunId = runId
      TotalElapsed = TimeSpan.Zero
      Outcome = Normal
      Results = results
      Verification = RunVerification.ofResults results }

let private fullRun runId =
    completed runId (Map.ofList [ "Tests.fsproj", TestsPassed("one passed", false, TimeSpan.Zero) ])

/// Mint evidence for the single configured project `Tests.fsproj`.
let private earn launchGeneration currentGeneration pending baseline (completion: TestRunCompleted) =
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
    // A launch with no model, or selected under a replaced one, earns nothing.
    Assert.True((earn None (Some 3L) 0 None result).IsNone)
    Assert.True((earn (Some 2L) (Some 3L) 0 None result).IsNone)
    // Remaining debt is a refusal the evidence carries, not a reason to drop it.
    Assert.NotEmpty((earn (Some 3L) (Some 3L) 1 None result |> Option.get).FailureReasons)

    // A completion cannot be claimed by a different launch.
    Assert.True(
        (EarnedEvidence.fromCompletion (Guid.NewGuid()) (Some 3L) (Some 3L) (Set.singleton "Tests.fsproj") 0 None result)
            .IsNone
    )

[<Fact>]
let ``filtered completion requires the same model whole-project baseline`` () =
    let baseline =
        fullRun (Guid.NewGuid()) |> earn (Some 3L) (Some 3L) 0 None |> Option.get

    let filtered =
        completed (Guid.NewGuid()) (Map.ofList [ "Tests.fsproj", TestsPassed("one passed", true, TimeSpan.Zero) ])

    let withoutBaseline = earn (Some 3L) (Some 3L) 0 None filtered |> Option.get
    Assert.NotEmpty withoutBaseline.FailureReasons
    let withBaseline = earn (Some 3L) (Some 3L) 0 (Some baseline) filtered |> Option.get
    Assert.Empty withBaseline.FailureReasons
    // A baseline from a replaced model vouches for nothing in the current one.
    let staleBaseline =
        earn (Some 4L) (Some 4L) 0 (Some baseline) filtered |> Option.get

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

[<Fact>]
let ``accountable red full suite can support filtered recovery without laundering remaining debt`` () =
    let red =
        completed (Guid.NewGuid()) (Map.ofList [ "Tests.fsproj", TestsFailed("one failed", false, TimeSpan.Zero) ])
        |> earn (Some 1L) (Some 1L) 1 None
        |> Option.get

    let rerun =
        completed
            (Guid.NewGuid())
            (Map.ofList [ "Tests.fsproj", TestsPassed("fixed test passed", true, TimeSpan.Zero) ])

    // The red run is evidence of what it ran, and it refuses a green.
    Assert.NotEmpty red.FailureReasons
    // Once the red re-ran and passed, nothing is owed: its whole-project run is a baseline.
    Assert.Empty((earn (Some 1L) (Some 1L) 0 (Some red) rerun |> Option.get).FailureReasons)
    // While the red is still owed, the same filtered green refuses.
    Assert.NotEmpty((earn (Some 1L) (Some 1L) 1 (Some red) rerun |> Option.get).FailureReasons)

[<Theory>]
[<InlineData("aborted", "run aborted")>]
[<InlineData("no-obligations", "no project obligations")>]
[<InlineData("unexpected-failure", "Unexpected.fsproj")>]
[<InlineData("unexpected-timeout", "Unexpected.fsproj")>]
[<InlineData("no-match", "no tests verified")>]
let ``actual completion preserves every independent refusal reason`` (kind: string, expected: string) =
    let result =
        match kind with
        | "unexpected-failure" -> TestsFailed("failure", false, TimeSpan.Zero)
        | "unexpected-timeout" -> TestsTimedOut("timed out", TimeSpan.FromSeconds 1.0, false, TimeSpan.Zero)
        | "no-match" -> TestsNoMatch("no match", TimeSpan.Zero)
        | _ -> TestsPassed("passed", false, TimeSpan.Zero)

    let project =
        if kind.StartsWith("unexpected-", StringComparison.Ordinal) then
            "Unexpected.fsproj"
        else
            "Tests.fsproj"

    let completion = completed (Guid.NewGuid()) (Map.ofList [ project, result ])

    let completion =
        if kind = "aborted" then
            { completion with
                Outcome = Aborted "shutdown" }
        else
            completion

    let expectedProjects =
        if kind = "no-obligations" then
            Set.empty
        else
            Set.singleton "Tests.fsproj"

    let proof =
        EarnedEvidence.fromCompletion completion.RunId (Some 1L) (Some 1L) expectedProjects 0 None completion
        |> Option.get

    Assert.Contains(proof.FailureReasons, fun reason -> reason.Contains expected)

[<Fact>]
let ``same-model baseline accounts untouched and no-match siblings without hiding current failures`` () =
    let expected = Set.ofList [ "Tests.fsproj"; "Sibling.fsproj" ]

    let baselineCompletion =
        completed
            (Guid.NewGuid())
            (Map.ofList
                [ "Tests.fsproj", TestsPassed("passed", false, TimeSpan.Zero)
                  "Sibling.fsproj", TestsPassed("passed", false, TimeSpan.Zero) ])

    let mint baseline (completion: TestRunCompleted) =
        EarnedEvidence.fromCompletion completion.RunId (Some 1L) (Some 1L) expected 0 baseline completion
        |> Option.get

    let baseline = mint None baselineCompletion

    for sibling in [ None; Some(TestsNoMatch("filter found no matching tests", TimeSpan.Zero)) ] do
        let results =
            let current =
                Map.ofList [ "Tests.fsproj", TestsPassed("passed", true, TimeSpan.Zero) ]

            match sibling with
            | None -> current
            | Some result -> Map.add "Sibling.fsproj" result current

        let current = completed (Guid.NewGuid()) results
        Assert.Empty((mint (Some baseline) current).FailureReasons)
        Assert.NotEmpty((mint None current).FailureReasons)

    let refused =
        completed
            (Guid.NewGuid())
            (Map.ofList
                [ "Tests.fsproj", TestsPassed("passed", true, TimeSpan.Zero)
                  "Sibling.fsproj", TestsDeferred "input changed" ])
        |> mint (Some baseline)

    Assert.Contains(refused.FailureReasons, fun reason -> reason.Contains "input changed")

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
    // A pinned publication never learns later evidence.
    Assert.True before.IsBusy
    Assert.Empty before.Evidence
