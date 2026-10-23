/// No green without evidence, as a checked property (AUTOMATION-107, item 2).
///
/// A receipt with no refusals is what the CLI grades as green (ADR-033). These properties
/// generate arbitrary launches, model generations, debts, baselines and per-project results,
/// and assert that a refusal-free receipt can only be minted from a completion that earned
/// it: the launch's own run, under the model it was selected against, owing nothing, with
/// every expected project verified whole by this run or by a whole-project run the current
/// model still vouches for, and nothing anywhere failed, errored or deferred. And that no
/// later handling of a receipt ever drops a refusal it already carries.
module FsHotWatch.Tests.EvidencePropertyTests

open System
open Xunit
open FsCheck
open FsCheck.FSharp
open FsHotWatch.Events

let private projects = [ "A"; "B"; "C" ]
let private runIds = [ Guid.Empty; Guid.NewGuid(); Guid.NewGuid() ]
let private fsprojOf project = $"%s{project}.fsproj"

/// Weighted toward a pass, so the green region the properties are about is actually reached.
let private resultGen =
    gen {
        let! filtered = Gen.frequency [ 2, Gen.constant false; 1, Gen.constant true ]

        return!
            Gen.frequency
                [ 8, Gen.constant (TestsPassed("passed", filtered, TimeSpan.Zero))
                  1, Gen.constant (TestsFailed("failed", filtered, TimeSpan.Zero))
                  1, Gen.constant (TestsTimedOut("timed out", TimeSpan.FromSeconds 1.0, filtered, TimeSpan.Zero))
                  1, Gen.constant (TestsDeferred "apphost not produced")
                  1, Gen.constant (TestsErrored "exit 7, no report")
                  2, Gen.constant (TestsNoMatch("zero tests ran", TimeSpan.Zero)) ]
    }

/// Results for a subset of the projects, plus sometimes one the launch did not expect.
let private resultsGen =
    gen {
        let! chosen = Gen.subListOf (projects @ [ "Extra" ])
        let! results = Gen.listOfLength chosen.Length resultGen
        return List.zip chosen results |> Map.ofList
    }

let private completionGen =
    gen {
        let! runId = Gen.elements runIds
        let! aborted = Gen.frequency [ 8, Gen.constant false; 1, Gen.constant true ]
        let! results = resultsGen

        return
            { RunId = runId
              TotalElapsed = TimeSpan.Zero
              Outcome = if aborted then Aborted "cancelled" else Normal
              Results = results
              Verification = RunVerification.ofResults results }
    }

let private generationGen =
    Gen.frequency [ 1, Gen.constant None; 4, Gen.elements [ Some 1L; Some 2L ] ]

/// Each project's compile-input identity, sometimes unknown, sometimes moved.
let private inputsGen =
    Gen.frequency
        [ 1, Gen.constant None
          4,
          gen {
              let! identities = Gen.listOfLength projects.Length (Gen.elements [ "v1"; "v2" ])
              return Some(List.zip (projects |> List.map fsprojOf) identities |> Map.ofList)
          } ]

/// Everything one `fromCompletion` call reads.
type private Mint =
    {
        LaunchRunId: Guid
        LaunchGeneration: int64 option
        CurrentGeneration: int64 option
        CurrentInputs: Map<string, string> option
        Expected: Set<string>
        Pending: int
        Baseline: EarnedEvidence option
        /// The mint `Baseline` came from, so the oracle can judge it without asking the code under test.
        Prior: Mint option
        Completed: TestRunCompleted
    }

let private projectFiles =
    projects |> List.map (fun project -> project, fsprojOf project) |> Map.ofList

let private mint (m: Mint) =
    EarnedEvidence.fromCompletion
        m.LaunchRunId
        m.LaunchGeneration
        m.CurrentGeneration
        m.CurrentInputs
        projectFiles
        m.Expected
        m.Pending
        m.Baseline
        m.Completed

let private mintGen (baseline: EarnedEvidence option) =
    gen {
        let! launchRunId = Gen.elements runIds
        let! launchGeneration = generationGen
        let! currentGeneration = generationGen
        let! inputs = inputsGen
        let! expected = Gen.subListOf projects
        let! pending = Gen.frequency [ 5, Gen.constant 0; 1, Gen.choose (1, 3) ]
        let! completed = completionGen

        // Bias toward the launch that owns the completion, or almost nothing is minted.
        let! ownLaunch = Gen.frequency [ 1, Gen.constant false; 3, Gen.constant true ]
        let! sameModel = Gen.frequency [ 1, Gen.constant false; 3, Gen.constant true ]

        return
            { LaunchRunId = if ownLaunch then completed.RunId else launchRunId
              LaunchGeneration = if sameModel then currentGeneration else launchGeneration
              CurrentGeneration = currentGeneration
              CurrentInputs = inputs
              Expected = Set.ofList expected
              Pending = pending
              Baseline = baseline
              Prior = None
              Completed = completed }
    }

/// A mint whose baseline is itself an earlier mint: evidence carried across a chain.
let private chainedGen =
    gen {
        let! first = mintGen None
        let! second = mintGen (mint first)
        return { second with Prior = Some first }
    }

let private fail message =
    raise (Xunit.Sdk.XunitException message)

let private isWholePass result =
    match result with
    | TestsPassed(_, false, _) -> true
    | _ -> false

/// Did the baseline's own completion run `project` whole, under a model the current one still
/// is, or one in which `project` compiles from the same inputs? Judged from the mint the
/// baseline came from, independently of `EarnedEvidence.carriedCoverage`.
let private baselineVouches (m: Mint) (project: string) =
    match m.Prior with
    | Some first when (mint first).IsSome && first.Completed.Outcome = Normal ->
        let ranWhole =
            match Map.tryFind project first.Completed.Results with
            | Some(TestsPassed(_, false, _))
            | Some(TestsFailed(_, false, _)) -> true
            | _ -> false

        let sameInputs () =
            match first.CurrentInputs, m.CurrentInputs with
            | Some before, Some now ->
                Set.contains project first.Expected
                && Map.tryFind (fsprojOf project) now = Map.tryFind (fsprojOf project) before
            | _ -> false

        ranWhole && (first.CurrentGeneration = m.CurrentGeneration || sameInputs ())
    | _ -> false

/// Every condition a refusal-free receipt stands on, stated without reference to how the
/// refusals are computed. Answers whether `m` minted one, so a test can show it reached any.
let private earnedOnly (m: Mint) : bool =
    match mint m with
    | None ->
        match m.LaunchGeneration, m.CurrentGeneration with
        | Some launched, Some current when
            launched = current
            && m.LaunchRunId <> Guid.Empty
            && m.LaunchRunId = m.Completed.RunId
            ->
            fail "the launch that owns this completion, under its own model, earned nothing"
        | _ -> ()

        false
    | Some evidence when not evidence.FailureReasons.IsEmpty -> false
    | Some evidence ->
        let said claim =
            fail $"a refusal-free receipt was minted although %s{claim}: %A{m}"

        if m.LaunchRunId = Guid.Empty || m.LaunchRunId <> m.Completed.RunId then
            said "the completion belongs to another launch"

        if
            m.LaunchGeneration <> m.CurrentGeneration
            || Some evidence.Generation <> m.CurrentGeneration
        then
            said "the model changed under the launch"

        if m.Completed.Outcome <> Normal then
            said "the run was cut short"

        if m.Pending <> 0 then
            said "verification obligations remain pending"

        if m.Expected.IsEmpty then
            said "no project obligation was selected"

        if not (TestResult.executedAnything m.Completed.Results) then
            said "the completion executed no tests"

        for KeyValue(project, result) in m.Completed.Results do
            match result with
            | TestsFailed _
            | TestsTimedOut _
            | TestsErrored _
            | TestsDeferred _ -> said $"%s{project} did not pass"
            | TestsPassed _
            | TestsNoMatch _ -> ()

        for project in m.Expected do
            let verifiedHere =
                Map.tryFind project m.Completed.Results |> Option.exists isWholePass

            if not verifiedHere && not (baselineVouches m project) then
                said $"%s{project} was verified neither whole by this run nor by a baseline the model still vouches for"

        true

/// Check `earnedOnly` over `mints`, and that at least `floor` of them minted a green: a
/// property that never reaches a green proves nothing about one.
let private checkReaching (tests: int) (floor: int) (relied: Mint -> bool) (mints: Gen<Mint>) =
    let greens = ref 0
    let leaning = ref 0

    Check.One(
        Config.QuickThrowOnFailure.WithMaxTest(tests),
        Prop.forAll (Arb.fromGen mints) (fun m ->
            if earnedOnly m then
                greens.Value <- greens.Value + 1

                if relied m then
                    leaning.Value <- leaning.Value + 1)
    )

    if greens.Value < floor then
        fail
            $"only %d{greens.Value} of %d{tests} generated mints were green: the generator misses the region under test"

    leaning.Value

[<Fact(Timeout = 30000)>]
let ``a refusal-free receipt can only be minted from a completion that earned it`` () =
    checkReaching 10000 60 (fun _ -> false) (mintGen None) |> ignore

/// A green that leans on its baseline for an expected project it did not run whole.
let private leansOnBaseline (m: Mint) =
    m.Expected
    |> Set.exists (fun project -> not (Map.tryFind project m.Completed.Results |> Option.exists isWholePass))

[<Fact(Timeout = 30000)>]
let ``a baseline carried from an earlier receipt never lets an unverified project through`` () =
    let leaning = checkReaching 20000 100 leansOnBaseline chainedGen

    if leaning < 10 then
        fail $"only %d{leaning} green(s) leaned on a carried baseline: the chain is not exercised"

[<Fact(Timeout = 30000)>]
let ``no later handling of a receipt drops a refusal it carries`` () =
    let handled =
        gen {
            let! m = chainedGen
            let! generation = Gen.elements [ 1L; 2L; 3L ]
            let! inputs = inputsGen
            let! extra = Gen.subListOf [ "a sibling errored"; "an obligation remains" ]
            return m, generation, inputs, extra
        }

    let keeps (m, generation, inputs, extra) =
        match mint m with
        | None -> ()
        | Some evidence ->
            let kept (label: string) (after: EarnedEvidence) =
                let lost =
                    Set.difference (Set.ofList evidence.FailureReasons) (Set.ofList after.FailureReasons)

                if not lost.IsEmpty then
                    fail $"%s{label} dropped refusal(s) %A{Set.toList lost}"

            kept "withRefusals" (EarnedEvidence.withRefusals extra evidence)

            EarnedEvidence.forModel generation inputs evidence
            |> Option.iter (kept "forModel")

            EarnedEvidence.retainedForRun (Some generation) inputs evidence.RunId (Some evidence)
            |> Option.iter (kept "retainedForRun")

            EarnedEvidence.retainedForZeroSelection (Some generation) inputs (Some evidence)
            |> Option.iter (kept "retainedForZeroSelection")

    Check.One(Config.QuickThrowOnFailure.WithMaxTest(1000), Prop.forAll (Arb.fromGen handled) keeps)
