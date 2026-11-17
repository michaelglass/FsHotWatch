/// AUTOMATION-1695's gate for the repository endpoint's frames: N seeded histories over
/// scripted streams with no violation, every planted defect caught within the same N, and both
/// arms of every frame-layer await reached by the seed.
module FsHotWatch.Tests.FrameSimulationTests

open Xunit
open Swensen.Unquote
open FsHotWatch.Tests.FrameSimulation
open FsHotWatch.Tests.TestHelpers

let private band =
    [ SimRegressions.firstSeed .. SimRegressions.firstSeed + SimRegressions.histories - 1 ]

let private report (violations: Violation list) =
    violations |> List.truncate 3 |> List.map describe |> String.concat "\n\n"

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``frames keep their contract over every history`` () =
    let seeds =
        SimRegressions.frameSeeds SimRegressions.firstSeed SimRegressions.histories (SimRegressions.load ())

    let violations, census = explore Subject.Current seeds false

    if not violations.IsEmpty then
        failwith $"%d{violations.Length} violation(s):\n%s{report violations}"

    test <@ census.ReadsAtOnce > 0 && census.ReadsLater > 0 @>
    test <@ census.PayloadsAtOnce > 0 && census.PayloadsLater > 0 @>
    test <@ census.WritesAtOnce > 0 && census.WritesLater > 0 @>

[<Theory(Timeout = SyncHangBackstopMs)>]
[<InlineData("one-read-per-part")>]
[<InlineData("partial-payload-as-whole")>]
[<InlineData("lost-completion")>]
let ``every planted defect is caught`` (name: string) =
    let mutant =
        match name with
        | "one-read-per-part" -> Mutant.OneReadPerPart
        | "partial-payload-as-whole" -> Mutant.PartialPayloadAsWhole
        | "lost-completion" -> Mutant.LostCompletion
        | other -> failwith $"no mutant named %s{other}"

    let violations, _ = explore (Subject.Mutated mutant) band true
    test <@ not violations.IsEmpty @>

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a violation reproduces from its seed`` () =
    let subject = Subject.Mutated Mutant.OneReadPerPart

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
