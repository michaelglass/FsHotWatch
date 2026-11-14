module FsHotWatch.Simulation.Tests.SimulationTests

open System
open Xunit
open Swensen.Unquote
open FsHotWatch.Simulation.Model
open FsHotWatch.Simulation.System
open FsHotWatch.Simulation.Simulation

/// AUTOMATION-1695's gate: N = 1,000 histories per CI run. `FSHW_SIM_HISTORIES` raises it for
/// the nightly run (100,000); `FSHW_SIM_SEED` moves the first seed, so a nightly explores new
/// histories and a reported seed replays exactly.
let private histories =
    match Int32.TryParse(Environment.GetEnvironmentVariable "FSHW_SIM_HISTORIES") with
    | true, n when n > 0 -> n
    | _ -> 1000

let private firstSeed =
    match Int32.TryParse(Environment.GetEnvironmentVariable "FSHW_SIM_SEED") with
    | true, n -> n
    | _ -> 1

let private report (violations: Violation list) =
    violations |> List.truncate 3 |> List.map describe |> String.concat "\n\n"

[<Fact>]
let ``the current lookup keeps the theorem over every history`` () =
    let violations = explore Subject.Current firstSeed histories

    if not violations.IsEmpty then
        failwith $"%d{violations.Length} violation(s):\n%s{report violations}"

[<Fact>]
let ``a history is a function of its seed`` () =
    test <@ generate 42 HistoryLength = generate 42 HistoryLength @>
    test <@ generate 42 HistoryLength <> generate 43 HistoryLength @>

/// The teeth: each planted defect class is found within the same N histories the gate runs.
[<Theory>]
[<InlineData("key-omits-file")>]
[<InlineData("no-staleness-check")>]
[<InlineData("selector-forgets-failures")>]
[<InlineData("fail-read-as-pass")>]
[<InlineData("missing-read-as-pass")>]
[<InlineData("kp-omits-file")>]
let ``every planted defect is caught`` (name: string) =
    let mutant =
        match name with
        | "key-omits-file" -> Mutant.KeyOmitsFile "B/Two.fs"
        | "no-staleness-check" -> Mutant.NoStalenessCheck
        | "selector-forgets-failures" -> Mutant.SelectorForgetsFailures
        | "fail-read-as-pass" -> Mutant.FailReadAsPass
        | "missing-read-as-pass" -> Mutant.MissingReadAsPass
        | "kp-omits-file" -> Mutant.KpOmitsFile "B/Two.fs"
        | other -> failwith $"no mutant named %s{other}"

    let violations = explore (Subject.Mutated mutant) firstSeed histories
    test <@ not violations.IsEmpty @>

/// A caught violation replays from its seed alone.
[<Fact>]
let ``a violation reproduces from its seed`` () =
    let subject = Subject.Mutated Mutant.FailReadAsPass

    match explore subject firstSeed histories with
    | first :: _ ->
        let again = execute subject first.Seed (generate first.Seed HistoryLength)

        test
            <@
                again
                |> List.map (fun v -> v.Step, v.Property)
                |> List.contains (first.Step, first.Property)
            @>
    | [] -> failwith "the mutant was not caught, so there is nothing to replay"

/// Positive control of the reference interpreter itself: a planted fault reddens exactly its
/// requirement, and a tree without faults is green.
[<Fact>]
let ``the reference interpreter reddens exactly the faulted requirement`` () =
    test <@ cleanFailing initialTree = Set.empty @>

    let withFault path fault =
        Map.add path { Version = 1; Fault = Some fault } initialTree

    test <@ cleanFailing (withFault "A/One.fs" Fault.Lint) = set [ "plugin:lint" ] @>
    test <@ cleanFailing (withFault "B/Two.fs" Fault.TestBreak) = set [ "test:B.Tests" ] @>
    test <@ cleanFailing (withFault "A.Tests/Tests.fs" Fault.TestBreak) = set [ "test:A.Tests" ] @>
    test <@ cleanFailing (withFault "B/One.fs" Fault.TypeError) = set [ "diagnostics" ] @>
