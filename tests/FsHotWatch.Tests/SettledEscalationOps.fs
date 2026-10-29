/// The real OS operations escalation needs (`ProcessHelper.liveEscalationOps`), with the
/// two waits that raced the box's load replaced by the events they stand for. Shared by
/// every LIVE escalation test: a kill test (`KillEscalationTests`, AUTOMATION-1312) and a
/// shutdown test (`ShutdownEscalationTests`, AUTOMATION-1354) failed the same way.
///
/// - A table read is retried until `ps` answers. `liveEscalationOps` bounds each read by
///   `EscalationTableBudget` (2 s), and a loaded box misses it: with one `yes` per core a
///   host's reads took over 2 s, and a missed read leaves every member unproven, which
///   escalation rightly never signals. The tests then asserted the box's load
///   (`KillTimedOut` for a killable tree, a shutdown's "escalation did not finish", or the
///   xUnit cap), not the escalation.
/// - The pause after SIGKILL waits until every member the escalation recorded is gone,
///   instead of trusting a loaded scheduler to run their exits within 200 ms. The pause
///   after SIGTERM is the production one: these trees ignore SIGTERM by construction, so
///   there is nothing to wait for.
///
/// Two polls per signal are then enough, so the SIGTERM round, which cannot succeed, costs
/// three table reads instead of eleven.
///
/// The fake half of this seam — a scripted table, start times and signal answers — is
/// `KillEscalationTests`' `FakeOs`; these are the ops a LIVE test injects instead.
module FsHotWatch.Tests.SettledEscalationOps

open System.Collections.Concurrent
open FsHotWatch.ProcessHelper
open FsHotWatch.Tests

[<WallClock("polling primitive: re-reads the real process table every 10 ms until the SIGKILLed members are gone; the interval is never the claim, and the test's cap reports a member that never goes")>]
let internal settledOps () : EscalationOps =
    let recorded = ConcurrentBag<int>()
    let lastSignal = ref 0

    let rec read () =
        match liveEscalationOps.ReadTable() with
        | Ok rows -> Ok rows
        | Error _ -> read ()

    let allGone () =
        match read () with
        | Ok rows ->
            recorded
            |> Seq.forall (fun pid -> rows |> List.forall (fun r -> r.Pid <> pid || r.Zombie))
        | Error _ -> false

    { liveEscalationOps with
        ReadTable = read
        StartTimeOf =
            fun pid ->
                recorded.Add pid
                liveEscalationOps.StartTimeOf pid
        Signal =
            fun pid signum ->
                lastSignal.Value <- signum
                liveEscalationOps.Signal pid signum
        Pause =
            fun () ->
                if lastSignal.Value = 9 then
                    while not (allGone ()) do
                        System.Threading.Thread.Sleep 10
                else
                    liveEscalationOps.Pause()
        Attempts = 2 }
