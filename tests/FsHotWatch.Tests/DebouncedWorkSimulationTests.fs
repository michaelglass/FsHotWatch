/// AUTOMATION-1695's gate for `DebouncedWork.Queue`: N seeded histories on the deterministic
/// runtime with no violation, and every planted defect caught within the same N.
module FsHotWatch.Tests.DebouncedWorkSimulationTests

open Xunit
open Swensen.Unquote
open FsHotWatch.Tests.DebouncedWorkSimulation
open FsHotWatch.Tests.TestHelpers

let private band =
    [ SimRegressions.firstSeed .. SimRegressions.firstSeed + SimRegressions.histories - 1 ]

let private report (violations: Violation list) =
    violations |> List.truncate 3 |> List.map describe |> String.concat "\n\n"

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``debounced work keeps its contract over every history`` () =
    let seeds =
        SimRegressions.debouncedWorkSeeds SimRegressions.firstSeed SimRegressions.histories (SimRegressions.load ())

    let violations, census = explore Subject.Current seeds false

    if not violations.IsEmpty then
        failwith $"%d{violations.Length} violation(s):\n%s{report violations}"

    // Coalescing, a timer's admission and a flush's are each reached.
    test <@ census.Coalesced > 0 @>
    test <@ census.TimerAdmitted > 0 @>
    test <@ census.Flushed > 0 @>

[<Theory(Timeout = SyncHangBackstopMs)>]
[<InlineData("timer-lost")>]
[<InlineData("post-order-swapped")>]
[<InlineData("duplicate-post")>]
let ``every planted defect is caught`` (name: string) =
    let mutant =
        match name with
        | "timer-lost" -> Mutant.TimerLost
        | "post-order-swapped" -> Mutant.PostOrderSwapped
        | "duplicate-post" -> Mutant.DuplicatePost
        | other -> failwith $"no mutant named %s{other}"

    let violations, _ = explore (Subject.Mutated mutant) band true
    test <@ not violations.IsEmpty @>

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a violation reproduces from its seed`` () =
    let subject = Subject.Mutated Mutant.PostOrderSwapped

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

/// AUTOMATION-1780: rest waits for receipts, so no history reaches rest with a receipt unsettled.
/// Before the fix these same seeds counted 482 (seed 2 first, at its sixth step).
[<Fact(Timeout = SyncHangBackstopMs)>]
let ``no history reaches rest with a receipt unsettled`` () =
    let violations, census = explore Subject.Current band false
    test <@ census.RestBeforeReceipt = 0 @>
    test <@ violations |> List.forall (fun v -> v.Theorem <> Theorem.HonestRest) @>
