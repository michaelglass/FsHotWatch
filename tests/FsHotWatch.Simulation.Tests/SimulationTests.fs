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
    let violations = explore Backend.Disk Subject.Current firstSeed histories

    if not violations.IsEmpty then
        failwith $"%d{violations.Length} violation(s):\n%s{report violations}"

/// The same histories through FsHotWatch.Core's in-memory store (L1's `IOutcomeStore`), where
/// faults are injected at the interface. Beside the disk runs, never instead of them.
[<Fact>]
let ``the current lookup keeps the theorem over every history on the in-memory store`` () =
    let violations = explore Backend.InMemory Subject.Current firstSeed histories

    if not violations.IsEmpty then
        failwith $"%d{violations.Length} violation(s):\n%s{report violations}"

/// A planted defect is caught on the in-memory store too, so its runs have teeth of their own.
[<Fact>]
let ``a planted defect is caught on the in-memory store`` () =
    test <@ not (explore Backend.InMemory (Subject.Mutated Mutant.FailReadAsPass) firstSeed histories).IsEmpty @>

    test
        <@ not (explore Backend.InMemory (Subject.Mutated(Mutant.KpOmitsFile "B/Two.fs")) firstSeed histories).IsEmpty @>

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

    let violations = explore Backend.Disk (Subject.Mutated mutant) firstSeed histories
    test <@ not violations.IsEmpty @>

/// AUTOMATION-1701, as the harness found it: a record file made unreadable left its key a miss for
/// good, so a concluded unfaulted confirm on that tree answered Missing (disk seed 73:
/// `missing [plugin:format]`; in-memory seed 52: `missing [diagnostics]`). These seeds stay in
/// the suite whatever the first seed and N are.
[<Theory>]
[<InlineData("disk", 73)>]
[<InlineData("in-memory", 52)>]
let ``an unreadable record no longer makes its key a permanent miss (AUTOMATION-1701)`` (backend: string, seed: int) =
    let backend =
        match backend with
        | "disk" -> Backend.Disk
        | "in-memory" -> Backend.InMemory
        | other -> failwith $"no backend named %s{other}"

    let history = generate seed HistoryLength
    test <@ history |> List.exists (fun e -> e.IsUnreadableRecord) @>

    let violations = execute backend Subject.Current seed history

    if not violations.IsEmpty then
        failwith $"%d{violations.Length} violation(s):\n%s{report violations}"

/// A caught violation replays from its seed alone.
[<Fact>]
let ``a violation reproduces from its seed`` () =
    let subject = Subject.Mutated Mutant.FailReadAsPass

    match explore Backend.Disk subject firstSeed histories with
    | first :: _ ->
        let again =
            execute Backend.Disk subject first.Seed (generate first.Seed HistoryLength)

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

/// The pin on the completeness exclusion (ADR-072): a TEST requirement whose record cannot be read
/// stays a miss after a fresh confirm, and the harness excuses exactly that. Kept beside the
/// exclusion so it is asserted, not merely tolerated.
[<Fact>]
let ``an unreadable test record keeps its key a miss (ADR-072)`` () =
    let repoRoot =
        IO.Path.Combine(IO.Path.GetTempPath(), "fshw-sim", Guid.NewGuid().ToString "N")

    IO.Directory.CreateDirectory repoRoot |> ignore

    try
        let shadow = FsHotWatch.OutcomeRecords.InMemoryOutcomeStore()

        let stores =
            Stores.InMemory(shadow, FsHotWatch.OutcomeRecords.InMemoryOutcomeStore())

        let at = DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero)

        let confirm (runId: string) (now: DateTimeOffset) =
            let run =
                { Command = FsHotWatch.Cli.Verdict.Command.Confirm
                  Key = treeKey Subject.Current initialTree
                  Started = initialTree
                  Read = initialTree
                  Selected = testProjects
                  Scope = FsHotWatch.Cli.ShadowVerdict.GradedScope.FullSuite
                  RunId = runId }

            let o = observation Subject.Current run initialTree "sim-fshw" repoRoot now
            o, publish Subject.Current repoRoot stores o

        let first, answer = confirm "run1" at
        test <@ answer = FsHotWatch.LookupVerdict.Answer.Green @>

        let full = FsHotWatch.Cli.ShadowVerdict.testKey first "A.Tests" "full"
        let name = shadow.FileNames full.Value |> List.exactlyOne
        shadow.Replace(full.Value, name, Error "permission denied")

        let second, answer = confirm "run2" (at.AddMinutes 1.0)
        test <@ answer = FsHotWatch.LookupVerdict.Answer.Missing [ "test:A.Tests" ] @>
        test <@ excusedMiss stores second "test:A.Tests" @>
        test <@ not (excusedMiss stores second "plugin:lint") @>
    finally
        IO.Directory.Delete(repoRoot, true)
