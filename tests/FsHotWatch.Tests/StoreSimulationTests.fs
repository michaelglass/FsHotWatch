/// AUTOMATION-1695's gate for `PluginWorkOwner.Store`: N seeded histories on the deterministic
/// runtime with no violation, every planted defect caught within the same N, and every writer
/// interleaving the pool reaches only by chance reached by the seed.
module FsHotWatch.Tests.StoreSimulationTests

open System
open Xunit
open Swensen.Unquote
open FsHotWatch.Tests.SimRuntime
open FsHotWatch.Tests.StoreSimulation
open FsHotWatch.Tests.TestHelpers

/// N histories per run, a FIXED band from the first seed, so the interleavings these histories
/// reach, and the coverage they contribute, are the same on every run. `FSHW_SIM_HISTORIES`
/// raises N (the nightly's 100,000); `FSHW_SIM_SEED` moves the band (the nightly's fresh seeds),
/// and with N = 1 replays one reported seed.
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

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``the store keeps its contract over every history`` () =
    let violations, census = explore Subject.Current firstSeed histories false

    if not violations.IsEmpty then
        failwith $"%d{violations.Length} violation(s):\n%s{report violations}"

    // Every interleaving a pool reaches only by chance is reached here, by the seed.
    test <@ census.AlreadyPublished > 0 @>
    test <@ census.Suspended > 0 @>
    test <@ census.JoinedPostedWriter > 0 @>
    test <@ census.WriterFoundWriting > 0 @>

/// The teeth: each planted defect is caught within the histories the gate runs.
[<Theory(Timeout = SyncHangBackstopMs)>]
[<InlineData("receipt-before-publication")>]
[<InlineData("lost-wakeup")>]
[<InlineData("post-order-swapped")>]
[<InlineData("duplicate-delivery")>]
let ``every planted defect is caught`` (name: string) =
    let mutant =
        match name with
        | "receipt-before-publication" -> Mutant.ReceiptBeforePublication
        | "lost-wakeup" -> Mutant.LostWakeup
        | "post-order-swapped" -> Mutant.PostOrderSwapped
        | "duplicate-delivery" -> Mutant.DuplicateDelivery
        | other -> failwith $"no mutant named %s{other}"

    let violations, _ = explore (Subject.Mutated mutant) firstSeed histories true
    test <@ not violations.IsEmpty @>

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a history is a function of its seed`` () =
    test <@ generate 42 HistoryLength = generate 42 HistoryLength @>
    test <@ generate 42 HistoryLength <> generate 43 HistoryLength @>

/// A caught violation replays from its seed alone: the same history, the same runtime choices,
/// the same violation at the same step.
[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a violation reproduces from its seed`` () =
    let subject = Subject.Mutated Mutant.ReceiptBeforePublication

    match explore subject firstSeed histories true with
    | first :: _, _ ->
        let again, _ = execute subject first.Seed (generate first.Seed HistoryLength)

        test
            <@
                again
                |> List.map (fun v -> v.Step, v.Theorem, v.Detail, v.Choices)
                |> List.contains (first.Step, first.Theorem, first.Detail, first.Choices)
            @>
    | [], _ -> failwith "the mutant was not caught, so there is nothing to replay"

/// A transition that waits on its own store is the documented deadlock. On the pool it hangs;
/// here the runtime names it at once.
[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a change that waits on its own store is reported as a deadlock, not hung on`` () =
    let sim = Sim 1
    use _ = sim.Install()
    let store = FsHotWatch.PluginWorkOwner.Store sim.Runtime

    let row =
        store.Register("row", 0, fun _ -> FsHotWatch.PluginWorkOwner.RowStatus.ofWork false 0L None)

    let waitsOnItsOwnStore =
        fun (_: FsHotWatch.PluginWorkOwner.WorkId) (n: int) ->
            store.Change(row, (fun _ m -> (m + 1, ())))
            (n + 1, ())

    raises<SimDeadlockException> <@ store.Change(row, waitsOnItsOwnStore) @>

    test <@ sim.Faults |> List.exists (fun f -> f.IsDeadlock) @>
