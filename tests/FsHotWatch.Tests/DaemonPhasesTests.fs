module FsHotWatch.Tests.DaemonPhasesTests

open System
open System.Threading
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.DaemonPhases

// AUTOMATION-555 (rework). The ledger is what lets a verdict cover the wall time a
// check spent waiting on the DAEMON — the scan, discovery, superseded plugin runs —
// so its two promises are: every phase that began is recorded on every exit, and a
// reader taking a snapshot mid-phase sees that time rather than a hole.

[<Fact(Timeout = 15000)>]
let ``a phase that began and completed is recorded with its scope, detail and a non-zero elapsed`` () =
    let ledger = Ledger()
    let phase = ledger.Begin(Phase.Scan "cold")
    Thread.Sleep 20
    phase.Complete(Some "cold scan: checked 3 of 3 registered file(s), unchecked 0")

    match ledger.Snapshot(DateTime.UtcNow) with
    | [ record ] ->
        test <@ record.Scope = "daemon.scan" @>
        test <@ record.Detail = Some "cold scan: checked 3 of 3 registered file(s), unchecked 0" @>
        test <@ record.Elapsed >= TimeSpan.FromMilliseconds 15.0 @>
    | other -> failwith $"expected exactly one completed phase, got %A{other}"

[<Fact(Timeout = 15000)>]
let ``a phase disposed without Complete is still recorded — wall time was spent either way`` () =
    let ledger = Ledger()

    do
        use _phase = ledger.Begin Phase.Discover
        Thread.Sleep 5

    match ledger.Snapshot(DateTime.UtcNow) with
    | [ record ] ->
        test <@ record.Scope = "daemon.discover" @>
        test <@ record.Detail = None @>
        test <@ record.Elapsed > TimeSpan.Zero @>
    | other -> failwith $"expected the disposed phase to be recorded, got %A{other}"

[<Fact(Timeout = 15000)>]
let ``Complete after Dispose does not record the phase twice`` () =
    let ledger = Ledger()
    let phase = ledger.Begin Phase.Check
    phase.Complete(Some "change batch: 2 file(s) checked")
    (phase :> IDisposable).Dispose()
    phase.Complete(Some "again")

    test <@ ledger.Snapshot(DateTime.UtcNow) |> List.length = 1 @>

[<Fact(Timeout = 15000)>]
let ``an in-flight phase appears in the snapshot clipped at now and marked in flight`` () =
    let ledger = Ledger()
    let startedBefore = DateTime.UtcNow
    let phase = ledger.Begin(Phase.Scan "forced")
    Thread.Sleep 10
    let now = DateTime.UtcNow

    match ledger.Snapshot now with
    | [ record ] ->
        test <@ record.Scope = "daemon.scan" @>
        test <@ record.Detail = Some "in flight" @>
        test <@ record.StartedAt >= startedBefore @>
        test <@ record.StartedAt + record.Elapsed <= now @>
        test <@ record.Elapsed > TimeSpan.Zero @>
    | other -> failwith $"expected the in-flight phase in the snapshot, got %A{other}"

    phase.Complete None

    test
        <@
            ledger.Snapshot(DateTime.UtcNow)
            |> List.forall (fun r -> r.Detail <> Some "in flight")
        @>

[<Fact(Timeout = 15000)>]
let ``Record places a plugin run under the plugin scope and floors a negative elapsed at zero`` () =
    let ledger = Ledger()
    let at = DateTime(2026, 9, 5, 21, 0, 0, DateTimeKind.Utc)
    ledger.Record(Phase.PluginRun "test-prune", at, TimeSpan.FromSeconds 114.0, Some "7 passed")
    ledger.Record(Phase.Startup, at, TimeSpan.FromSeconds -1.0, None)

    let scopes =
        ledger.Snapshot(DateTime.UtcNow) |> List.map (fun r -> r.Scope, r.Elapsed)

    test
        <@
            scopes = [ "plugin.test-prune", TimeSpan.FromSeconds 114.0
                       "daemon.startup", TimeSpan.Zero ]
        @>

[<Fact(Timeout = 15000)>]
let ``the ledger retains at most MaxRetained completed phases, dropping the oldest`` () =
    let ledger = Ledger()
    let origin = DateTime(2026, 9, 5, 0, 0, 0, DateTimeKind.Utc)

    for i in 0 .. MaxRetained + 9 do
        ledger.Record(Phase.Check, origin.AddSeconds(float i), TimeSpan.FromMilliseconds 1.0, Some(string i))

    let retained = ledger.Snapshot(DateTime.UtcNow)
    test <@ retained.Length = MaxRetained @>
    test <@ retained.Head.Detail = Some "10" @>

[<Fact(Timeout = 15000)>]
let ``every phase maps to a stable scope token`` () =
    test <@ Phase.scope Phase.Startup = "daemon.startup" @>
    test <@ Phase.scope Phase.Discover = "daemon.discover" @>
    test <@ Phase.scope (Phase.Scan "cold") = "daemon.scan" @>
    test <@ Phase.scope Phase.Check = "daemon.check" @>
    test <@ Phase.scope (Phase.PluginRun "build") = "plugin.build" @>

/// The analyzers plugin reports a terminal status PER FILE. Left as ~130 records
/// those evict startup and discovery; coalesced they are the one interval a reader
/// wants, and a run that starts after a real gap stays its own record.
[<Fact(Timeout = 15000)>]
let ``contiguous same-scope records coalesce into one phase; a gap keeps them apart`` () =
    let ledger = Ledger()
    let origin = DateTime(2026, 9, 5, 21, 0, 0, DateTimeKind.Utc)
    let ms (n: float) = TimeSpan.FromMilliseconds n
    ledger.Record(Phase.Startup, origin, ms 500.0, Some "startup")
    ledger.Record(Phase.PluginRun "analyzers", origin.AddMilliseconds 600.0, ms 100.0, Some "file 1")
    // Overlapping the previous run.
    ledger.Record(Phase.PluginRun "analyzers", origin.AddMilliseconds 650.0, ms 100.0, Some "file 2")
    // Within the coalescing gap of the previous end (750 → 900).
    ledger.Record(Phase.PluginRun "analyzers", origin.AddMilliseconds 900.0, ms 50.0, Some "file 3")
    // A different scope in between does not break the chain.
    ledger.Record(Phase.Check, origin.AddMilliseconds 800.0, ms 10.0, None)
    // Past the gap: its own record.
    ledger.Record(Phase.PluginRun "analyzers", origin.AddMilliseconds 2000.0, ms 30.0, Some "file 4")

    let snapshot =
        ledger.Snapshot(DateTime.UtcNow)
        |> List.map (fun r -> r.Scope, (r.StartedAt - origin).TotalMilliseconds, r.Elapsed.TotalMilliseconds, r.Detail)

    test
        <@
            snapshot = [ "daemon.startup", 0.0, 500.0, Some "startup"
                         "plugin.analyzers", 600.0, 350.0, Some "file 3"
                         "daemon.check", 800.0, 10.0, None
                         "plugin.analyzers", 2000.0, 30.0, Some "file 4" ]
        @>
