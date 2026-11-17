/// AUTOMATION-1695's gate for `SupervisedWork.Queue`: N seeded histories on the deterministic
/// runtime with no violation, and every planted defect caught within the same N.
module FsHotWatch.Tests.SupervisedWorkSimulationTests

open Xunit
open Swensen.Unquote
open FsHotWatch.Tests.SupervisedWorkSimulation
open FsHotWatch.Tests.TestHelpers

let private band =
    [ SimRegressions.firstSeed .. SimRegressions.firstSeed + SimRegressions.histories - 1 ]

let private report (violations: Violation list) =
    violations |> List.truncate 3 |> List.map describe |> String.concat "\n\n"

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``supervised work keeps its contract over every history`` () =
    let seeds =
        SimRegressions.supervisedWorkSeeds SimRegressions.firstSeed SimRegressions.histories (SimRegressions.load ())

    let violations, census = explore Subject.Current seeds false

    if not violations.IsEmpty then
        failwith $"%d{violations.Length} violation(s):\n%s{report violations}"

    // Each await arm a pool reaches only by chance, and the deadline, is reached by the seed.
    test <@ census.AdmittedAlready > 0 @>
    test <@ census.AdmittedLater > 0 @>
    test <@ census.DeadlinesExpired > 0 @>

[<Theory(Timeout = SyncHangBackstopMs)>]
[<InlineData("timer-lost")>]
[<InlineData("cancel-lost")>]
[<InlineData("submit-order-swapped")>]
let ``every planted defect is caught`` (name: string) =
    let mutant =
        match name with
        | "timer-lost" -> Mutant.TimerLost
        | "cancel-lost" -> Mutant.CancelLost
        | "submit-order-swapped" -> Mutant.SubmitOrderSwapped
        | other -> failwith $"no mutant named %s{other}"

    let violations, _ = explore (Subject.Mutated mutant) band true
    test <@ not violations.IsEmpty @>

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a violation reproduces from its seed`` () =
    let subject = Subject.Mutated Mutant.TimerLost

    match explore subject band true with
    | first :: _, _ ->
        let again, _ = execute subject first.Seed (generate first.Seed HistoryLength)

        test
            <@
                again
                |> List.map (fun v -> v.Step, v.Theorem, v.Detail, v.Choices)
                |> List.contains (first.Step, first.Theorem, first.Detail, first.Choices)
            @>
    | [], _ -> failwith "the mutant was not caught, so there is nothing to replay"
