module FsHotWatch.Tests.VerdictEvidenceTests

open System
open System.Threading
open Xunit
open Swensen.Unquote
open FsHotWatch.Events
open FsHotWatch.PluginFramework
open FsHotWatch.Tests.TestHelpers

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

[<Fact>]
let ``client observation inhibits idle exit without keeping observed work busy`` () =
    let store = FsHotWatch.PluginWorkOwner.Store()
    let before = store.Snapshot
    let lease = store.Observe()
    let observed = store.Snapshot
    // A watching client is a reason not to exit, and no reason to call the host busy:
    // the work it observes is exactly as finished as it was a moment ago.
    Assert.Equal(0, before.ObserverCount)
    Assert.Equal(1, observed.ObserverCount)
    Assert.False observed.IsBusy
    lease.Dispose()
    // Releasing twice is the ordinary shape of a `use` inside a task that also faults.
    lease.Dispose()
    Assert.Equal(0, store.Snapshot.ObserverCount)
    // A pinned publication keeps the count it was published with.
    Assert.Equal(1, observed.ObserverCount)

/// A host that publishes a model and registers the analysis-only TestPrune: it MINTS
/// evidence, so a verdict wait may ask it for some.
let private evidenceMintingHost (repoRoot: string) =
    let host = FsHotWatch.PluginHost.PluginHost.create sharedChecker.Value repoRoot
    host.WorkStore.PublishProjectModelWithFiles(fixtureModel, Set.empty)

    host.RegisterHandler(
        FsHotWatch.TestPrune.TestPrunePlugin.create
            (System.IO.Path.Combine(repoRoot, "wait.db"))
            repoRoot
            None
            None
            None
            None
            None
            []
    )

    host

[<Fact(Timeout = 30000)>]
let ``a reported terminal status cannot mint evidence after its event drains`` () =
    withTempDir "verdict-wait-unearned" (fun repoRoot ->
        let host = evidenceMintingHost repoRoot

        // A plugin that CLAIMS success. The claim carries no run, no coverage and no
        // discharged obligations — it must remain legal to report progress without
        // gaining the ability to end a verdict wait.
        host.RegisterHandler
            { Name = PluginName.create "claims-success"
              Init = ()
              Update =
                fun ctx state _ ->
                    async {
                        ctx.ReportStatus(
                            Completed(System.DateTime.UtcNow, RunVerdict.create "claimed success" System.TimeSpan.Zero)
                        )

                        return state
                    }
              Commands = []
              Subscriptions = Set.singleton SubscribeFileChanged
              CacheKey = None
              PrepareCommit = None
              Teardown = None }

        host.EmitFileChanged(SourceChanged [ "Unverified.fs" ])
        waitForQuiescent host 10000

        // Positive controls: the claim really was published, and the event really drained.
        Assert.True(
            host.GetAllStatuses()
            |> Map.exists (fun _ status -> PluginStatus.isTerminal status)
        )

        Assert.False(host.AnyPluginBusy())
        // Nothing earned it: no receipt exists for the published model.
        Assert.Empty host.WorkSnapshot.Evidence
        Assert.Empty host.WorkSnapshot.AnalysisEvidence

        let waiting =
            FsHotWatch.Daemon.waitForVerdict host (System.TimeSpan.FromSeconds 1.0) CancellationToken.None

        Assert.Throws<System.TimeoutException>(fun () -> waiting.GetAwaiter().GetResult())
        |> ignore)

[<Fact(Timeout = 30000)>]
let ``a verdict wait ends on the analysis receipt its cohort seal earned`` () =
    withTempDir "verdict-wait-earned" (fun repoRoot ->
        let host = evidenceMintingHost repoRoot

        host.EmitBatchChecked
            { fakeBatchChecked [] with
                ModelGeneration = Some fixtureModelGeneration }

        waitForQuiescent host 10000
        Assert.Single host.WorkSnapshot.AnalysisEvidence |> ignore

        // The control for the test above: with evidence for the current model, the same
        // wait resolves instead of timing out.
        FsHotWatch.Daemon.waitForVerdict host (System.TimeSpan.FromSeconds 5.0) CancellationToken.None
        |> fun waiting -> waiting.GetAwaiter().GetResult())

// ---------------------------------------------------------------------------
// A build that FAILED is an answer about the model it failed under.
// ---------------------------------------------------------------------------

[<Fact>]
let ``a completed build failure is evidence about the model it failed under`` () =
    let failure =
        CompletedBuildFailure.fromFailure (Some 3L) "Build failed: FS0039" |> Option.get

    Assert.Equal(3L, failure.Generation)
    Assert.NotEmpty failure.FailureReasons

    // Nothing to be evidence ABOUT: a build that ran under no model says nothing about
    // the model the verdict is graded against.
    Assert.True((CompletedBuildFailure.fromFailure None "Build failed: FS0039").IsNone)
    Assert.True((CompletedBuildFailure.fromFailure (Some -1L) "Build failed: FS0039").IsNone)
    // A failure that names nothing is not a proof that anything failed.
    Assert.True((CompletedBuildFailure.fromFailure (Some 3L) "   ").IsNone)

/// A plugin state that owns a completed build failure, as `BuildPlugin`'s does. Minting
/// stays first-party; this is the fixture that proves the OWNER publishes it and the wait
/// consumes it, without spawning MSBuild.
type private FailedBuildState =
    { Failure: CompletedBuildFailure option }

    interface ICompletedBuildFailureState with
        member this.CompletedBuildFailure = this.Failure

[<Fact(Timeout = 30000)>]
let ``a verdict wait ends on a completed build failure, which mints no other receipt`` () =
    withTempDir "verdict-wait-red-build" (fun repoRoot ->
        let host = evidenceMintingHost repoRoot

        host.RegisterHandler
            { Name = PluginName.create "red-build"
              Init = { Failure = None }
              Update =
                fun _ _ _ ->
                    async {
                        return
                            { Failure = CompletedBuildFailure.fromFailure (Some fixtureModelGeneration) "Build failed" }
                    }
              Commands = []
              Subscriptions = Set.singleton SubscribeFileChanged
              CacheKey = None
              PrepareCommit = None
              Teardown = None }

        host.EmitFileChanged(SourceChanged [ "Broken.fs" ])
        waitForQuiescent host 10000

        // The point of the case: a red build leaves NO test receipt and NO analysis
        // receipt, so before this the evidence wait had nothing to end on and hung until
        // its timeout — on the one tree where the answer was already known.
        Assert.Empty host.WorkSnapshot.Evidence
        Assert.Empty host.WorkSnapshot.AnalysisEvidence
        Assert.NotEmpty host.WorkSnapshot.CompletedFailures

        FsHotWatch.Daemon.waitForVerdict host (System.TimeSpan.FromSeconds 5.0) CancellationToken.None
        |> fun waiting -> waiting.GetAwaiter().GetResult())

// ---------------------------------------------------------------------------
// A configuration that turns every plugin off registers none.
// ---------------------------------------------------------------------------

/// A host as a daemon configured with no plugins builds it: a published model, an empty
/// registry.
let private pluginFreeHost (repoRoot: string) =
    let host = FsHotWatch.PluginHost.PluginHost.create sharedChecker.Value repoRoot
    host.WorkStore.PublishProjectModelWithFiles(fixtureModel, Set.empty)
    host

[<Fact(Timeout = 30000)>]
let ``a verdict wait over a host with no plugins registered resolves at once`` () =
    withTempDir "verdict-wait-no-plugins" (fun repoRoot ->
        let host = pluginFreeHost repoRoot

        // Nothing owns work and nothing owes a receipt, so the answer exists the moment
        // the wait starts. Waiting on would be waiting for evidence no plugin can produce.
        test <@ host.GetAllStatuses() |> Map.isEmpty @>
        test <@ not host.WorkSnapshot.IsBusy @>
        test <@ not host.WorkSnapshot.OffersEvidence @>

        FsHotWatch.Daemon.waitForVerdict host (TimeSpan.FromSeconds 5.0) CancellationToken.None
        |> fun waiting -> waiting.GetAwaiter().GetResult())

[<Fact(Timeout = 30000)>]
let ``a host with no plugins still waits for the work it owns, and names it`` () =
    withTempDir "verdict-wait-no-plugins-owned" (fun repoRoot ->
        // The control for the test above: an empty registry settles because it owns
        // nothing, not because an empty registry is waved through.
        let host = pluginFreeHost repoRoot
        let scan = host.WorkStore.BeginOperation "scan"

        let failure =
            Assert.Throws<TimeoutException>(fun () ->
                FsHotWatch.Daemon.waitForVerdict host (TimeSpan.FromSeconds 1.0) CancellationToken.None
                |> fun waiting -> waiting.GetAwaiter().GetResult())

        test <@ failure.Message.Contains "no plugins are registered" @>
        test <@ failure.Message.Contains "scan" @>

        host.WorkStore.EndOperation scan

        FsHotWatch.Daemon.waitForVerdict host (TimeSpan.FromSeconds 5.0) CancellationToken.None
        |> fun waiting -> waiting.GetAwaiter().GetResult())

[<Fact(Timeout = 30000)>]
let ``an in-flight verdict wait IS the client observation that inhibits idle exit`` () =
    withTempDir "verdict-wait-observed" (fun repoRoot ->
        // The counter this replaces lived beside the publication rather than in it: the
        // daemon incremented `activeVerdictWaits` around the RPC, and idle-exit read that
        // integer while reading the host's work from somewhere else. Two readings of one
        // fact can disagree. The lease is published with the work, so "a client is
        // waiting" and "the host owns nothing" are answered by the same snapshot.
        let host = evidenceMintingHost repoRoot
        Assert.Equal(0, host.WorkSnapshot.ObserverCount)

        let waiting =
            FsHotWatch.Daemon.waitForVerdict host (TimeSpan.FromSeconds 2.0) CancellationToken.None

        Assert.True(waitUntilTrue (fun () -> host.WorkSnapshot.ObserverCount = 1) 5000)
        // A watcher is not work: it inhibits the exit and leaves the host at rest.
        Assert.False host.WorkSnapshot.IsBusy

        Assert.Throws<System.TimeoutException>(fun () -> waiting.GetAwaiter().GetResult())
        |> ignore

        // Released on EVERY exit, timeout included — the `finally` the counter needed.
        Assert.True(waitUntilTrue (fun () -> host.WorkSnapshot.ObserverCount = 0) 5000))

/// A state that owns ONLY analysis evidence — no test receipt, no build failure.
type private AnalysisOnlyState =
    { Analysis: AnalysisEvidence option }

    interface IAnalysisEvidenceState with
        member this.AnalysisEvidence = this.Analysis

[<Fact(Timeout = 30000)>]
let ``a plugin that mints only analysis evidence still offers receipts`` () =
    withTempDir "verdict-analysis-only-offer" (fun repoRoot ->
        // Each of the three evidence interfaces has to be able to answer "this host offers
        // receipts" ON ITS OWN. TestPrune implements two of them at once, so the
        // analysis-only arm was never the deciding one, and an embedder registering a
        // plugin that mints only analysis evidence would have been read as offering none —
        // which is the one reading that ungates a green without evidence (ADR-033).
        let host = FsHotWatch.PluginHost.PluginHost.create sharedChecker.Value repoRoot
        host.WorkStore.PublishProjectModelWithFiles(fixtureModel, Set.empty)

        host.RegisterHandler
            { Name = PluginName.create "analysis-only"
              Init = { Analysis = None }
              Update = fun _ state _ -> async { return state }
              Commands = []
              Subscriptions = Set.singleton SubscribeFileChanged
              CacheKey = None
              PrepareCommit = None
              Teardown = None }

        host.EmitFileChanged(SourceChanged [ "Analysed.fs" ])
        waitForQuiescent host 10000

        test <@ host.WorkSnapshot.OffersEvidence @>
        // It offers them and holds none, which is the refusal, not silence.
        test <@ List.isEmpty host.WorkSnapshot.AnalysisEvidence @>
        test <@ List.isEmpty host.WorkSnapshot.Evidence @>
        test <@ List.isEmpty host.WorkSnapshot.CompletedFailures @>)

/// A TestPrune host running `GatedTests`, a suite that passes once `release` exists and
/// blocks until then.
let private gatedTestHost (repoRoot: string) (release: string) =
    let host = FsHotWatch.PluginHost.PluginHost.create sharedChecker.Value repoRoot
    host.WorkStore.PublishProjectModelWithFiles(fixtureModel, Set.empty)

    host.RegisterHandler(
        FsHotWatch.TestPrune.TestPrunePlugin.create
            (System.IO.Path.Combine(repoRoot, "verdict.db"))
            repoRoot
            (Some
                [ { FsHotWatch.TestPrune.TestPrunePlugin.TestConfig.Project = "GatedTests"
                    Command = "sh"
                    Args = "-c \"" + gatedWait repoRoot release 1500 + "\""
                    Group = "default"
                    Environment = []
                    FilterTemplate = None
                    ClassJoin = " "
                    TimeoutSec = None
                    ReportVerificationFormat = FsHotWatch.TestPrune.TestPrunePlugin.AutoDetect } ])
            None
            None
            None
            None
            []
    )

    host

/// Start a forced run for a client, wait until it is running, then let the client go.
let private cancelForcedRun (host: FsHotWatch.PluginHost.PluginHost) =
    use client = new CancellationTokenSource()

    let run =
        Async.StartAsTask(host.RunCommand("run-tests", [| "{}" |]), cancellationToken = client.Token)

    run.ContinueWith(fun (t: System.Threading.Tasks.Task<string option>) -> t.Exception |> ignore)
    |> ignore

    let running () =
        match host.GetStatus "test-prune" with
        | Some(Running _) -> true
        | _ -> false

    Assert.True(waitUntilTrue running 10000, "the forced run never started")
    client.Cancel()
    Assert.True(waitUntilTrue (fun () -> not (host.AnyPluginBusy())) 20000, "the host must come to rest")

/// A forced run cancelled because its client went away earns nothing: with no earlier
/// evidence, a verdict asked for afterwards finds no receipt to grade and refuses.
[<Fact(Timeout = 60000)>]
let ``a verdict after a cancelled forced run refuses when nothing else vouches for a green`` () =
    withTempDir "verdict-after-cancelled-run" (fun repoRoot ->
        withReleaseGate repoRoot "test-host" (fun release ->
            let host = gatedTestHost repoRoot release

            cancelForcedRun host

            Assert.True(host.WorkSnapshot.OffersEvidence)
            Assert.Empty host.WorkSnapshot.Evidence

            let waiting =
                FsHotWatch.Daemon.waitForVerdict host (TimeSpan.FromSeconds 1.0) CancellationToken.None

            Assert.Throws<TimeoutException>(fun () -> waiting.GetAwaiter().GetResult())
            |> ignore))

/// A cancelled forced run adds nothing to the evidence and takes nothing from it: the
/// receipt an earlier completed run earned is exactly what remains.
[<Fact(Timeout = 60000)>]
let ``a cancelled forced run leaves the evidence exactly as it found it`` () =
    withTempDir "evidence-after-cancelled-run" (fun repoRoot ->
        withReleaseGate repoRoot "test-host" (fun release ->
            let host = gatedTestHost repoRoot release

            // Positive control: an uncancelled forced run DOES earn a receipt here.
            System.IO.File.WriteAllText(release, "")
            host.RunCommand("run-tests", [| "{}" |]) |> Async.RunSynchronously |> ignore
            waitForQuiescent host 20000

            let earned =
                host.WorkSnapshot.Evidence |> List.map (fun e -> e.RunId, e.FailureReasons)

            Assert.NotEmpty earned

            System.IO.File.Delete release
            cancelForcedRun host

            let remaining =
                host.WorkSnapshot.Evidence |> List.map (fun e -> e.RunId, e.FailureReasons)

            Assert.Equal<(Guid * string list) list>(earned, remaining)))
